using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 趋势归档里的一天。
    ///
    /// Total 也存下来是为了识别扩容：盘扩容后旧点与新点的 Used 不可比，
    /// 做趋势外推时必须整段丢弃变过容量的点，否则会把增长算成下降。
    /// </summary>
    internal sealed class DiskTrendDay
    {
        /// <summary>当天最后一次采集到的已用字节（日末值）。</summary>
        public double Used { get; set; }

        /// <summary>当天最后一次采集到的总容量。</summary>
        public double Total { get; set; }

        /// <summary>当天已用字节的峰值。用于日报写"当日峰值"，与日末值区分开。</summary>
        public double Peak { get; set; }
    }

    /// <summary>一个挂载点的逐日序列。</summary>
    internal sealed class DiskMountSeries
    {
        /// <summary>采集通道给出的原始挂载点字符串（可能含卷标），仅用于显示。</summary>
        public string Name { get; set; }

        /// <summary>日期（yyyy-MM-dd）-> 当天数据。</summary>
        public Dictionary<string, DiskTrendDay> Days { get; set; }
    }

    /// <summary>
    /// 磁盘容量趋势的按日归档，对应 data/disk-daily.json。
    ///
    /// 为什么不并进 HistoryStore：
    ///   1) 粒度不同——这里是"每挂载点每天一条"，HistoryStore 是"每分钟一条"，
    ///      混进去会让每 20 秒一次的全量重写体积按挂载点数量翻几倍。
    ///   2) 主键不同——这里的主键是归一化后的挂载点名，HistoryStore 的主键是服务器 Id。
    ///   3) 保留期不同（180 天 vs 原始采样默认 30 天）。
    ///   4) HistoryStore.GetAggregate 返回的是锁内的活对象。往里加嵌套字典后，
    ///      采集线程一边写、别的线程一边枚举，会抛"集合已修改"，
    ///      而这个异常出现在后台线程上会直接结束进程。
    ///
    /// 写入节流到 15 分钟一次，另外跨天与退出时各强制一次。丢一刻钟的代价只是
    /// 曲线少一个点，不值得为它把写入提到 20 秒。
    ///
    /// 无论容量预测功能是否开启都必须记录——否则用户开启后还要再等 5 天才有数据。
    /// </summary>
    public sealed class DiskTrendStore
    {
        /// <summary>日级数据的保留天数。不做成设置项：设置面已经够大，
        /// 而 180 天足以覆盖任何有意义的容量趋势。</summary>
        private const int RetentionDays = 180;

        /// <summary>常规落盘间隔（分钟）。</summary>
        private const int FlushIntervalMinutes = 15;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _path;
        private readonly object _gate = new object();

        /// <summary>服务器 Id -> 归一化挂载点 -> 逐日序列。</summary>
        private readonly Dictionary<string, Dictionary<string, DiskMountSeries>> _servers =
            new Dictionary<string, Dictionary<string, DiskMountSeries>>(StringComparer.Ordinal);

        private bool _dirty;
        private DateTime _lastFlushUtc = DateTime.MinValue;

        /// <summary>上次落盘时的日期，用于识别跨天（跨天要立刻把前一天锁下来）。</summary>
        private string _lastFlushDate;

        public DiskTrendStore(string dataDirectory)
        {
            _path = Path.Combine(dataDirectory, "disk-daily.json");
        }

        public void Load()
        {
            lock (_gate)
            {
                _servers.Clear();
                if (!File.Exists(_path)) return;

                try
                {
                    string text = File.ReadAllText(_path);
                    var loaded = JsonConvert.DeserializeObject
                        <Dictionary<string, Dictionary<string, DiskMountSeries>>>(text);

                    if (loaded != null)
                    {
                        foreach (var server in loaded)
                        {
                            if (server.Value == null) continue;

                            var mounts = new Dictionary<string, DiskMountSeries>(StringComparer.Ordinal);
                            foreach (var mount in server.Value)
                            {
                                if (mount.Value == null || mount.Value.Days == null) continue;
                                mounts[mount.Key] = mount.Value;
                            }
                            if (mounts.Count > 0) _servers[server.Key] = mounts;
                        }
                    }

                    PruneOldDays();

                    int mountCount = 0;
                    foreach (var mounts in _servers.Values) mountCount += mounts.Count;
                    Logger.Debug("磁盘趋势", "已加载 " + _servers.Count + " 台服务器、共 " +
                                             mountCount + " 个挂载点的日级趋势");
                }
                catch (Exception ex)
                {
                    // 趋势文件损坏不值得让程序起不来，从空重新积累即可。
                    // 丢的是预测曲线，采集与告警完全不受影响。
                    Logger.Warn("磁盘趋势", "disk-daily.json 读取失败，将从空重新积累：" + ex.Message);
                    _servers.Clear();
                }
            }
        }

        /// <summary>记录一次采集结果。与 HistoryStore.Record 并列调用。</summary>
        public void Record(ServerSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.ServerId)) return;

            // 离线快照的 Disks 是空的；即使有残留值也不该记——
            // 否则一次连接超时就会把当天的日末值覆盖成 0。
            if (!snapshot.Online || snapshot.Disks == null || snapshot.Disks.Count == 0) return;

            string date = snapshot.TimeUtc.ToLocalTime()
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            lock (_gate)
            {
                Dictionary<string, DiskMountSeries> mounts;
                if (!_servers.TryGetValue(snapshot.ServerId, out mounts))
                {
                    mounts = new Dictionary<string, DiskMountSeries>(StringComparer.Ordinal);
                    _servers[snapshot.ServerId] = mounts;
                }

                foreach (DiskUsage disk in snapshot.Disks)
                {
                    string key = NormalizeMount(disk.Mount);
                    if (string.IsNullOrEmpty(key) || disk.TotalBytes <= 0) continue;

                    DiskMountSeries series;
                    if (!mounts.TryGetValue(key, out series))
                    {
                        series = new DiskMountSeries
                        {
                            Name = disk.Mount,
                            Days = new Dictionary<string, DiskTrendDay>(StringComparer.Ordinal)
                        };
                        mounts[key] = series;

                        // Info 而不是 Debug：开始记录新挂载点是件罕见且值得留痕的事，
                        // 也是排查"为什么分区没有预测"时的第一手线索。
                        Logger.Info("磁盘趋势", "开始记录新挂载点 " +
                                                 snapshot.ServerId + " " + disk.Mount +
                                                 "（键 " + key + "）");
                    }

                    // 显示名每次刷新：卷标可能被改过，趋势不该因此断掉
                    series.Name = disk.Mount;
                    if (series.Days == null)
                    {
                        series.Days = new Dictionary<string, DiskTrendDay>(StringComparer.Ordinal);
                    }

                    DiskTrendDay day;
                    if (!series.Days.TryGetValue(date, out day))
                    {
                        day = new DiskTrendDay
                        {
                            Used = disk.UsedBytes,
                            Total = disk.TotalBytes,
                            Peak = disk.UsedBytes
                        };
                        series.Days[date] = day;
                    }
                    else
                    {
                        // Used 取当天最后一次（日末值），Peak 单独取最大
                        day.Used = disk.UsedBytes;
                        day.Total = disk.TotalBytes;
                        if (disk.UsedBytes > day.Peak) day.Peak = disk.UsedBytes;
                    }
                }

                _dirty = true;
                FlushIfDue(false);
            }
        }

        /// <summary>强制落盘。程序退出时调用。</summary>
        public void Flush()
        {
            lock (_gate)
            {
                FlushIfDue(true);
            }
        }

        /// <summary>移除某台服务器的全部趋势数据（删除服务器时调用）。</summary>
        public void RemoveServer(string serverId)
        {
            if (string.IsNullOrEmpty(serverId)) return;

            lock (_gate)
            {
                if (!_servers.Remove(serverId)) return;

                // 删除是不可逆操作，不参与节流，立刻落盘
                _dirty = true;
                FlushIfDue(true);
            }
        }

        /// <summary>
        /// 把任意采集通道给出的挂载点名归一成稳定的键。
        ///
        /// Windows：WMI 通道产出 "D: (卷标)"（WmiCollector），SSH/WinRM 通道只给出
        /// 盘符。同一个分区换个采集通道就会变成两条序列，趋势因此断掉——
        /// 所以这里统一抹成 "D:"。
        /// 盘符大小写也要统一，"d:" 与 "D:" 是同一个盘。
        ///
        /// Linux：df 给出的挂载点去掉尾部斜杠，根目录 "/" 除外。
        /// </summary>
        public static string NormalizeMount(string mount)
        {
            if (string.IsNullOrWhiteSpace(mount)) return string.Empty;
            string text = mount.Trim();

            bool looksLikeDrive =
                text.Length >= 2 && text[1] == ':' &&
                ((text[0] >= 'A' && text[0] <= 'Z') || (text[0] >= 'a' && text[0] <= 'z'));

            if (looksLikeDrive)
            {
                // 取第一个 " (" 之前的部分当盘符。
                // 只在形如 "X:" 时才剥卷标——否则会误伤合法的 Linux 挂载点
                // "/mnt/Data (old)"。卷标本身可能带括号（"C: (系统 (OS))"），
                // 取第一个而非最后一个才能正确切出 "C:"。
                int cut = text.IndexOf(" (", StringComparison.Ordinal);
                if (cut >= 2) text = text.Substring(0, cut);

                // "D:\" 与 "D:" 归一，盘符统一大写
                string drive = text.Substring(0, 2).ToUpperInvariant();
                string rest = text.Length > 2 ? text.Substring(2).TrimEnd('\\', '/') : string.Empty;
                return drive + rest;
            }

            if (text.Length > 1) text = text.TrimEnd('/');
            return text;
        }

        /// <summary>
        /// 取某台服务器每个挂载点的逐日序列，供容量预测用。
        ///
        /// 返回的是**副本**：采集线程随时在改 _servers 里的对象，
        /// 把活引用交出去会重演"集合已修改"直接结束进程的老问题。
        ///
        /// 只返回**已完成的天**。今天这一天的值还在涨，放进拟合会让斜率全天
        /// 抬升、午夜归零，预测在阈值线上下反复跳。今天的现状由调用方用实时
        /// 快照的当前值传进 DiskForecast。
        /// </summary>
        internal Dictionary<string, List<DiskTrendPoint>> GetServerSeries(string serverId)
        {
            var result = new Dictionary<string, List<DiskTrendPoint>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(serverId)) return result;

            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            lock (_gate)
            {
                Dictionary<string, DiskMountSeries> mounts;
                if (!_servers.TryGetValue(serverId, out mounts)) return result;

                foreach (var mount in mounts)
                {
                    DiskMountSeries series = mount.Value;
                    if (series == null || series.Days == null) continue;

                    var points = new List<DiskTrendPoint>();
                    foreach (var day in series.Days)
                    {
                        // 今天的记录还在增长，由调用方用实时值代替
                        if (string.Equals(day.Key, today, StringComparison.Ordinal)) continue;

                        DiskTrendDay value = day.Value;
                        if (value == null || value.Total <= 0) continue;

                        DateTime date;
                        if (!DateTime.TryParseExact(day.Key, "yyyy-MM-dd",
                                CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        {
                            continue;   // 键不是合法日期就当这条不存在，不影响别的天
                        }

                        points.Add(new DiskTrendPoint
                        {
                            Date = date,
                            UsedBytes = value.Used,
                            TotalBytes = value.Total
                        });
                    }

                    if (points.Count == 0) continue;

                    points.Sort(CompareByDate);
                    result[mount.Key] = points;
                }
            }

            return result;
        }

        private static int CompareByDate(DiskTrendPoint a, DiskTrendPoint b)
        {
            return a.Date.CompareTo(b.Date);
        }

        private void FlushIfDue(bool force)
        {
            if (!_dirty) return;

            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool dayChanged = _lastFlushDate != null &&
                              !string.Equals(_lastFlushDate, today, StringComparison.Ordinal);

            if (!force && !dayChanged &&
                (DateTime.UtcNow - _lastFlushUtc).TotalMinutes < FlushIntervalMinutes)
            {
                return;
            }

            if (dayChanged)
            {
                PruneOldDays();
                Logger.Debug("磁盘趋势", "已跨天，归档 " + _lastFlushDate + " 并清理超期数据");
            }

            Write();
            _dirty = false;
            _lastFlushUtc = DateTime.UtcNow;
            _lastFlushDate = today;
        }

        private void Write()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_servers);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json, Utf8NoBom);

                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                // 趋势数据丢失不影响采集与告警，但必须留痕——
                // 否则用户会发现"预测一直没数据"却查不出原因。
                Logger.Error("磁盘趋势", "写入 disk-daily.json 失败，趋势数据会丢失：" + ex.Message, ex);
            }
        }

        private void PruneOldDays()
        {
            DateTime cutoff = DateTime.Today.AddDays(-RetentionDays);

            foreach (var server in _servers)
            {
                List<string> emptyMounts = null;

                foreach (var mount in server.Value)
                {
                    DiskMountSeries series = mount.Value;
                    if (series.Days == null || series.Days.Count == 0)
                    {
                        if (emptyMounts == null) emptyMounts = new List<string>();
                        emptyMounts.Add(mount.Key);
                        continue;
                    }

                    // 先收集再删除：不能边遍历 Keys 边改字典
                    List<string> stale = null;
                    foreach (string date in series.Days.Keys)
                    {
                        DateTime parsed;
                        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out parsed))
                        {
                            continue;
                        }
                        if (parsed < cutoff)
                        {
                            if (stale == null) stale = new List<string>();
                            stale.Add(date);
                        }
                    }

                    if (stale != null)
                    {
                        foreach (string date in stale) series.Days.Remove(date);
                    }

                    // 分区被卸载后序列会一直空着。不清掉的话，一台机器多年下来
                    // 会积累上百个早已不存在的挂载点条目。
                    if (series.Days.Count == 0)
                    {
                        if (emptyMounts == null) emptyMounts = new List<string>();
                        emptyMounts.Add(mount.Key);
                    }
                }

                if (emptyMounts != null)
                {
                    foreach (string key in emptyMounts) server.Value.Remove(key);
                }
            }

            // 服务器条目本身由 DeleteServer 负责清理，这里不动
        }
    }
}
