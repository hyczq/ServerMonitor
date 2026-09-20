using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 历史数据存储。
    ///
    /// 原始采样：data/history/yyyy-MM-dd.json，每个服务器每分钟最多一条，
    ///          用于绘制全天走势；超过保留天数后自动删除。
    /// 日汇总  ：data/summary.json，按 服务器 × 日期 记录均值/峰值，
    ///          体积很小，永久保留，供"每天统计"长期使用。
    /// </summary>
    public sealed class HistoryStore
    {
        private readonly string _historyDirectory;
        private readonly string _summaryPath;

        private readonly object _gate = new object();

        /// <summary>日期 -> 服务器 Id -> 采样列表（已加载的整日数据）。</summary>
        private readonly Dictionary<string, Dictionary<string, List<MetricSample>>> _days =
            new Dictionary<string, Dictionary<string, List<MetricSample>>>(StringComparer.Ordinal);

        /// <summary>服务器 Id -> 日期 -> 日汇总。</summary>
        private readonly Dictionary<string, Dictionary<string, DailyAggregate>> _summary =
            new Dictionary<string, Dictionary<string, DailyAggregate>>(StringComparer.Ordinal);

        /// <summary>服务器 Id -> 本分钟是否已记录（"yyyy-MM-dd HH:mm"）。</summary>
        private readonly Dictionary<string, string> _lastRecordedMinute =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private DateTime _lastFlushUtc = DateTime.MinValue;
        private bool _dirty;

        public HistoryStore(string dataDirectory)
        {
            _historyDirectory = Path.Combine(dataDirectory, "history");
            _summaryPath = Path.Combine(dataDirectory, "summary.json");
            Directory.CreateDirectory(_historyDirectory);
        }

        public void Load()
        {
            lock (_gate)
            {
                _summary.Clear();
                if (File.Exists(_summaryPath))
                {
                    try
                    {
                        string text = File.ReadAllText(_summaryPath);
                        var loaded = JsonConvert.DeserializeObject
                            <Dictionary<string, Dictionary<string, DailyAggregate>>>(text);
                        if (loaded != null)
                        {
                            foreach (var pair in loaded)
                            {
                                if (pair.Value != null) _summary[pair.Key] = pair.Value;
                            }
                        }
                    }
                    catch
                    {
                        // 汇总文件损坏：从当天起重新累积，不影响程序运行
                    }
                }
            }
        }

        /// <summary>记录一次采集结果。同一服务器同一分钟内重复调用只保留第一条。</summary>
        public void Record(ServerSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.ServerId)) return;

            DateTime local = snapshot.TimeUtc.ToLocalTime();
            string date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string minuteKey = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            lock (_gate)
            {
                // ---- 日汇总 ----
                Dictionary<string, DailyAggregate> byDate;
                if (!_summary.TryGetValue(snapshot.ServerId, out byDate))
                {
                    byDate = new Dictionary<string, DailyAggregate>(StringComparer.Ordinal);
                    _summary[snapshot.ServerId] = byDate;
                }

                DailyAggregate aggregate;
                if (!byDate.TryGetValue(date, out aggregate))
                {
                    aggregate = new DailyAggregate { Date = date };
                    byDate[date] = aggregate;
                }
                aggregate.Add(snapshot);

                // ---- 原始采样 ----
                string lastMinute;
                bool alreadyRecorded = _lastRecordedMinute.TryGetValue(snapshot.ServerId, out lastMinute)
                                       && lastMinute == minuteKey;
                if (!alreadyRecorded)
                {
                    _lastRecordedMinute[snapshot.ServerId] = minuteKey;

                    Dictionary<string, List<MetricSample>> day = GetOrLoadDay(date);
                    List<MetricSample> samples;
                    if (!day.TryGetValue(snapshot.ServerId, out samples))
                    {
                        samples = new List<MetricSample>();
                        day[snapshot.ServerId] = samples;
                    }

                    // 采集失败时仍记一条 0 值，让走势图明确显示"这一分钟没数据"
                    samples.Add(new MetricSample(
                        local, snapshot.CpuPercent, snapshot.MemPercent, snapshot.DiskPercent));
                }

                _dirty = true;
                FlushIfDue(false);
            }
        }

        /// <summary>把待写数据落盘。程序退出时应调用一次。</summary>
        public void Flush()
        {
            lock (_gate)
            {
                FlushIfDue(true);
            }
        }

        private void FlushIfDue(bool force)
        {
            if (!_dirty) return;

            // 采样很频繁，落盘限流到 20 秒一次，避免频繁磁盘写入
            if (!force && (DateTime.UtcNow - _lastFlushUtc).TotalSeconds < 20) return;

            foreach (var pair in _days)
            {
                WriteDay(pair.Key, pair.Value);
            }

            WriteSummary();
            _dirty = false;
            _lastFlushUtc = DateTime.UtcNow;
        }

        private void WriteDay(string date, Dictionary<string, List<MetricSample>> day)
        {
            try
            {
                string path = Path.Combine(_historyDirectory, date + ".json");
                string json = JsonConvert.SerializeObject(day);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception ex)
            {
                // 单日写入失败不应影响其他日期，但这是数据丢失，必须记下来——
                // 之前这里是空 catch，历史数据写不进去也无人知晓。
                Logger.Error("历史数据", "写入 " + date + ".json 失败，当天采样会丢失：" + ex.Message, ex);
            }
        }

        private void WriteSummary()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_summary);
                string temp = _summaryPath + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(_summaryPath)) File.Replace(temp, _summaryPath, null);
                else File.Move(temp, _summaryPath);
            }
            catch (Exception ex)
            {
                // 日汇总会长期保留，写不进去影响的是长期统计，值得记 Error
                Logger.Error("历史数据", "写入 summary.json 失败，日汇总会丢失：" + ex.Message, ex);
            }
        }

        private Dictionary<string, List<MetricSample>> GetOrLoadDay(string date)
        {
            Dictionary<string, List<MetricSample>> day;
            if (_days.TryGetValue(date, out day)) return day;

            day = new Dictionary<string, List<MetricSample>>(StringComparer.Ordinal);
            string path = Path.Combine(_historyDirectory, date + ".json");
            if (File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path);
                    var loaded = JsonConvert.DeserializeObject
                        <Dictionary<string, List<MetricSample>>>(text);
                    if (loaded != null)
                    {
                        foreach (var pair in loaded)
                        {
                            if (pair.Value != null) day[pair.Key] = pair.Value;
                        }
                    }
                }
                catch
                {
                }
            }

            _days[date] = day;
            return day;
        }

        // ---------- 查询 ----------

        public List<MetricSample> GetSamples(string date, string serverId)
        {
            lock (_gate)
            {
                Dictionary<string, List<MetricSample>> day = GetOrLoadDay(date);
                List<MetricSample> samples;
                if (day.TryGetValue(serverId, out samples)) return new List<MetricSample>(samples);
                return new List<MetricSample>();
            }
        }

        public DailyAggregate GetAggregate(string serverId, string date)
        {
            lock (_gate)
            {
                Dictionary<string, DailyAggregate> byDate;
                if (_summary.TryGetValue(serverId, out byDate))
                {
                    DailyAggregate aggregate;
                    if (byDate.TryGetValue(date, out aggregate)) return aggregate;
                }
                return null;
            }
        }

        /// <summary>所有存在记录的日期，倒序排列（最新在前）。</summary>
        public List<string> GetAvailableDates()
        {
            var dates = new HashSet<string>(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (string file in Directory.GetFiles(_historyDirectory, "*.json"))
                {
                    dates.Add(Path.GetFileNameWithoutExtension(file));
                }
                foreach (var byDate in _summary.Values)
                {
                    foreach (string date in byDate.Keys) dates.Add(date);
                }
            }

            var list = dates.ToList();
            list.Sort();
            list.Reverse();
            return list;
        }

        /// <summary>删除超过保留期的原始采样文件（日汇总不受影响）。</summary>
        public void PruneRawFiles(int retentionDays)
        {
            if (retentionDays < 1) return;
            DateTime cutoff = DateTime.Today.AddDays(-retentionDays);

            try
            {
                foreach (string file in Directory.GetFiles(_historyDirectory, "*.json"))
                {
                    DateTime parsed;
                    if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),
                            "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out parsed))
                    {
                        continue;
                    }

                    if (parsed < cutoff)
                    {
                        try { File.Delete(file); }
                        catch { }
                    }
                }
            }
            catch
            {
            }

            lock (_gate)
            {
                foreach (string date in _days.Keys.ToList())
                {
                    DateTime parsed;
                    if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out parsed) && parsed < cutoff)
                    {
                        _days.Remove(date);
                    }
                }
            }
        }

        /// <summary>移除某台服务器的全部历史（删除服务器时调用）。</summary>
        public void RemoveServer(string serverId)
        {
            lock (_gate)
            {
                _summary.Remove(serverId);
                _lastRecordedMinute.Remove(serverId);
                foreach (var day in _days.Values) day.Remove(serverId);
                _dirty = true;
                FlushIfDue(true);
            }
        }
    }
}
