using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 通过 WMI/DCOM 采集 Windows 指标。这是 Windows Server 上最通用的远程通道，
    /// 不需要预先开启 WinRM。若目标机防火墙屏蔽了 WMI，请改用 WinRM 通道。
    /// </summary>
    internal sealed class WmiCollector : ICollector
    {
        /// <summary>原始性能计数器是累计值，需要跨次采样求差。按服务器 Id 缓存上一拍。</summary>
        private static readonly ConcurrentDictionary<string, RawCounterSample> RawSamples =
            new ConcurrentDictionary<string, RawCounterSample>();

        private sealed class RawCounterSample
        {
            public long Timestamp;
            public long Frequency;
            public long Counter;
        }

        public ServerSnapshot Collect(ServerConfig cfg, ServerSnapshot previous)
        {
            // WMI 不允许对本机连接指定凭证，提前拦下来给出准确原因，
            // 否则会收到 0x80041064 或各种 E_OUTOFMEMORY 之类的误导性错误。
            if (HostUtil.IsLocalHost(cfg.Host) && !string.IsNullOrEmpty(cfg.Username))
            {
                throw new InvalidOperationException(
                    "WMI 不允许对「本机」连接指定用户名和口令。" +
                    "如果要监控这台机器自己，请把用户名和口令留空（用当前登录身份采集）；" +
                    "如果要监控别的服务器，请在地址里填写那台机器的内网 IP。");
            }

            var sw = Stopwatch.StartNew();
            var snapshot = new ServerSnapshot { ServerId = cfg.Id, Online = true };

            var options = new ConnectionOptions
            {
                // 必须是 null 才表示"用当前登录身份"。
                // 空字符串会报「参数错误」，纯空白会报「拒绝访问」—— 都不知所云。
                Username = Blank(cfg.Username),
                Password = Blank(cfg.Password),
                Authentication = AuthenticationLevel.PacketPrivacy,
                Impersonation = ImpersonationLevel.Impersonate,
                EnablePrivileges = true,
                Timeout = TimeSpan.FromSeconds(20)
            };

            var scope = new ManagementScope(
                string.Format(@"\\{0}\root\cimv2", cfg.Host), options);
            scope.Connect();

            Logger.Debug("WMI", "已连接 \\\\" + cfg.Host + "\\root\\cimv2" +
                                " 用户=" + (string.IsNullOrEmpty(cfg.Username)
                                    ? "(当前身份)" : cfg.Username));

            ReadOperatingSystem(scope, snapshot);
            ReadComputerSystem(scope, snapshot);
            ReadDisks(scope, snapshot);
            ReadCpu(scope, cfg, snapshot);

            snapshot.Disks = DiskFilter.Sort(snapshot.Disks);

            sw.Stop();
            snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return snapshot;
        }

        private static void ReadOperatingSystem(ManagementScope scope, ServerSnapshot snapshot)
        {
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    "SELECT Caption, Version, TotalVisibleMemorySize, FreePhysicalMemory, " +
                    "LastBootUpTime FROM Win32_OperatingSystem")))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        snapshot.Hostname = FirstLine(AsString(item["Caption"]));
                        if (string.IsNullOrWhiteSpace(snapshot.Kernel))
                        {
                            snapshot.Kernel = "Windows " + AsString(item["Version"]);
                        }

                        double totalKb = AsDouble(item["TotalVisibleMemorySize"]);
                        double freeKb = AsDouble(item["FreePhysicalMemory"]);
                        snapshot.MemTotalBytes = totalKb * 1024.0;
                        snapshot.MemUsedBytes = Math.Max(0, totalKb - freeKb) * 1024.0;

                        try
                        {
                            string boot = AsString(item["LastBootUpTime"]);
                            if (!string.IsNullOrEmpty(boot))
                            {
                                DateTime bootTime = ManagementDateTimeConverter.ToDateTime(boot);
                                TimeSpan up = DateTime.Now - bootTime;
                                if (up.TotalSeconds > 0) snapshot.Uptime = up;
                            }
                        }
                        catch
                        {
                            // 开机时间拿不到不影响主指标
                        }
                        break;
                    }
                }
            }
        }

        private static void ReadComputerSystem(ManagementScope scope, ServerSnapshot snapshot)
        {
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    "SELECT Name, NumberOfLogicalProcessors, NumberOfProcessors, " +
                    "TotalPhysicalMemory FROM Win32_ComputerSystem")))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        string name = AsString(item["Name"]);
                        if (!string.IsNullOrWhiteSpace(name)) snapshot.Hostname = name;

                        int cores = (int)AsDouble(item["NumberOfLogicalProcessors"]);
                        if (cores <= 0) cores = (int)AsDouble(item["NumberOfProcessors"]);
                        snapshot.CpuCores = cores > 0 ? cores : 1;

                        // 兜底：万一 Win32_OperatingSystem 没取到内存总量
                        if (snapshot.MemTotalBytes <= 0)
                        {
                            snapshot.MemTotalBytes = AsDouble(item["TotalPhysicalMemory"]);
                        }
                        break;
                    }
                }
            }

            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Name FROM Win32_Processor")))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        snapshot.CpuModel = AsString(item["Name"]);
                        break;
                    }
                }
            }
        }

        private static void ReadDisks(ManagementScope scope, ServerSnapshot snapshot)
        {
            using (var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery(
                    "SELECT DeviceID, Size, FreeSpace, FileSystem, VolumeName " +
                    "FROM Win32_LogicalDisk WHERE DriveType = 3")))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        double size = AsDouble(item["Size"]);
                        if (size <= 0) continue; // 未插入介质的光驱等

                        double free = AsDouble(item["FreeSpace"]);
                        string deviceId = AsString(item["DeviceID"]);
                        string label = AsString(item["VolumeName"]);
                        string fs = AsString(item["FileSystem"]);

                        snapshot.Disks.Add(new DiskUsage
                        {
                            Mount = string.IsNullOrWhiteSpace(label)
                                ? deviceId
                                : deviceId + " (" + label + ")",
                            FileSystem = fs,
                            TotalBytes = size,
                            UsedBytes = Math.Max(0, size - free)
                        });
                    }
                }
            }
        }

        /// <summary>
        /// CPU 占用率三级降级策略：
        /// 1) 格式化性能类（最准，但要求性能计数器健康）
        /// 2) 原始性能类 + 跨次差值（同样准，且不依赖 WMI 的性能库刷新）
        /// 3) Win32_Processor.LoadPercentage（瞬时采样，作为最后兜底）
        /// </summary>
        /// <summary>
        /// CPU 占用率三级降级策略。
        ///
        /// 每一级为什么失败都要记下来——这套降级平时是静默的，
        /// 一旦某台机器的 CPU 读数异常（恒为 0、跳变、明显偏低），
        /// 必须能从日志里看出当时走的是哪一级、前几级为什么不可用。
        /// </summary>
        private static void ReadCpu(ManagementScope scope, ServerConfig cfg, ServerSnapshot snapshot)
        {
            if (TryReadFormattedCpu(scope, cfg, snapshot))
            {
                Logger.Trace(cfg.DisplayName, "CPU 取值：格式化性能类（Win32_PerfFormattedData）");
                return;
            }

            if (TryReadRawCpu(scope, cfg, snapshot))
            {
                Logger.Trace(cfg.DisplayName, "CPU 取值：原始性能计数器跨次差值（Win32_PerfRawData）");
                return;
            }

            TryReadFallbackCpu(scope, cfg, snapshot);

            Logger.Debug(cfg.DisplayName,
                "CPU 取值：Win32_Processor.LoadPercentage（瞬时采样）。" +
                "前两级都不可用，说明目标机的性能计数器可能已损坏，" +
                "读数会比真实值跳变，建议在目标机执行 winmgmt /verifyrepository 检查");
        }

        private static bool TryReadFormattedCpu(ManagementScope scope, ServerConfig cfg,
                                                ServerSnapshot snapshot)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery(
                        "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor " +
                        "WHERE Name = '_Total'")))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        using (item)
                        {
                            double value = AsDouble(item["PercentProcessorTime"]);
                            snapshot.CpuPercent = Clamp(value);
                            return true;
                        }
                    }
                }

                Logger.Debug(cfg.DisplayName, "格式化性能类没有返回 _Total 实例，降级到原始计数器");
            }
            catch (Exception ex)
            {
                Logger.Debug(cfg.DisplayName,
                    "格式化性能类不可用（" + ex.Message + "），降级到原始计数器");
            }
            return false;
        }

        private static bool TryReadRawCpu(ManagementScope scope, ServerConfig cfg,
                                          ServerSnapshot snapshot)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery(
                        "SELECT Timestamp_PerfTime, Frequency_PerfTime, PercentProcessorTime " +
                        "FROM Win32_PerfRawData_PerfOS_Processor WHERE Name = '_Total'")))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        using (item)
                        {
                            var current = new RawCounterSample
                            {
                                Timestamp = (long)AsDouble(item["Timestamp_PerfTime"]),
                                Frequency = (long)AsDouble(item["Frequency_PerfTime"]),
                                Counter = (long)AsDouble(item["PercentProcessorTime"])
                            };

                            RawCounterSample last;
                            bool hasLast = RawSamples.TryGetValue(cfg.Id, out last);
                            RawSamples[cfg.Id] = current;

                            if (!hasLast)
                            {
                                // 首拍没有基线。这是正常的，但要说清楚——否则
                                // 用户会奇怪"为什么这台机器第一次采集 CPU 是 0"。
                                Logger.Debug(cfg.DisplayName,
                                    "原始计数器首次采样，本轮无基线可用，CPU 暂由下一级兜底");
                                return false;
                            }

                            long elapsed = current.Timestamp - last.Timestamp;
                            if (elapsed <= 0 || current.Frequency <= 0)
                            {
                                Logger.Debug(cfg.DisplayName,
                                    "原始计数器差值无效（时间差=" + elapsed +
                                    " 频率=" + current.Frequency + "），降级到下一级");
                                return false;
                            }

                            // 计数器累计的是 核心数 × 时间，除以核心数得到整机占用率
                            double raw = (double)(current.Counter - last.Counter)
                                         / elapsed * current.Frequency;
                            double cores = snapshot.CpuCores > 0 ? snapshot.CpuCores : 1;
                            snapshot.CpuPercent = Clamp(raw / cores);
                            return true;
                        }
                    }
                }

                Logger.Debug(cfg.DisplayName, "原始计数器没有返回 _Total 实例，降级到下一级");
            }
            catch (Exception ex)
            {
                Logger.Debug(cfg.DisplayName,
                    "原始计数器不可用（" + ex.Message + "），降级到下一级");
            }
            return false;
        }

        private static void TryReadFallbackCpu(ManagementScope scope, ServerConfig cfg,
                                               ServerSnapshot snapshot)
        {
            try
            {
                double sum = 0;
                int count = 0;
                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT LoadPercentage FROM Win32_Processor")))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject item in results)
                    {
                        using (item)
                        {
                            sum += AsDouble(item["LoadPercentage"]);
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    snapshot.CpuPercent = Clamp(sum / count);
                    return;
                }

                Logger.Warn(cfg.DisplayName,
                    "CPU 三级取值全部失败，本轮 CPU 显示为 0（不影响内存与磁盘）");
            }
            catch (Exception ex)
            {
                // 三级全失败则保持 0，不影响其余指标展示
                Logger.Warn(cfg.DisplayName,
                    "CPU 三级取值全部失败，本轮 CPU 显示为 0（不影响内存与磁盘）：" + ex.Message);
            }
        }

        /// <summary>空串和空白统一归一成 null，否则 WMI 会抛出与真实原因无关的错误。</summary>
        private static string Blank(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static double AsDouble(object value)
        {
            if (value == null) return 0;
            double result;
            if (double.TryParse(Convert.ToString(value),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return 0;
        }

        private static string AsString(object value)
        {
            return value == null ? null : Convert.ToString(value);
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int idx = text.IndexOf('\n');
            return idx < 0 ? text.Trim() : text.Substring(0, idx).Trim();
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
