using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Renci.SshNet;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 通过 SSH 采集 Linux 指标。整包命令一次往返拿到全部数据，
    /// CPU 使用率用 /proc/stat 的两次采样求差，比读 load 更准确。
    /// </summary>
    internal sealed class SshCollector : ICollector
    {
        /// <summary>该脚本刻意只依赖 POSIX 基础工具，兼容 CentOS 6 等老系统。</summary>
        private const string LinuxScript =
            "LC_ALL=C; export LC_ALL\n" +
            "echo '##CPU_A'; grep '^cpu ' /proc/stat\n" +
            "sleep 1\n" +
            "echo '##CPU_B'; grep '^cpu ' /proc/stat\n" +
            "echo '##MEM'; grep -E '^(MemTotal|MemFree|MemAvailable|Buffers|Cached|SwapTotal|SwapFree):' /proc/meminfo\n" +
            "echo '##LOAD'; cut -d' ' -f1 /proc/loadavg 2>/dev/null\n" +
            "echo '##UP'; cut -d' ' -f1 /proc/uptime 2>/dev/null\n" +
            "echo '##HOST'; hostname 2>/dev/null\n" +
            "echo '##KERN'; uname -r 2>/dev/null\n" +
            "echo '##MODEL'; grep -m1 'model name' /proc/cpuinfo 2>/dev/null | cut -d: -f2\n" +
            "echo '##CORES'; getconf _NPROCESSORS_ONLN 2>/dev/null\n" +
            "echo '##DISK'; df -P -k 2>/dev/null\n" +
            "echo '##END'\n";

        public ServerSnapshot Collect(ServerConfig cfg, ServerSnapshot previous)
        {
            var sw = Stopwatch.StartNew();
            var snapshot = new ServerSnapshot { ServerId = cfg.Id, Online = true };

            // SSH 和 WMI 不同：它必须有个登录名，留空是配置错误而不是"用当前身份"
            if (string.IsNullOrWhiteSpace(cfg.Username))
            {
                throw new InvalidOperationException(
                    "SSH 通道必须填写用户名（Linux 上不存在「用当前登录身份」这种方式）。");
            }

            // 口令必须兜底成空串：PasswordConnectionInfo 内部会走
            // Encoding.GetBytes(password)，传 null 会抛出无关的
            // ArgumentNullException("s")，用户看到的是天书而不是原因。
            var connectionInfo = new PasswordConnectionInfo(
                cfg.Host, cfg.EffectivePort,
                cfg.Username,
                cfg.Password ?? string.Empty)
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            // 部分老服务器只提供 ssh-rsa / diffie-hellman-group1-sha1
            connectionInfo.ChannelCloseTimeout = TimeSpan.FromSeconds(5);

            using (var client = new SshClient(connectionInfo))
            {
                client.KeepAliveInterval = TimeSpan.FromSeconds(5);
                client.Connect();

                Logger.Debug("SSH", "已连接 " + cfg.Host + ":" + cfg.EffectivePort +
                                    " 用户=" + cfg.Username +
                                    " 目标系统=" + cfg.Os);

                // Windows 目标机：通过 ssh 执行 powershell.exe。
                // 用 -EncodedCommand 传脚本，脚本里的引号、换行、$ 才不会被
                // 远端的 cmd.exe 解析坏（直接拼命令行会被拆得面目全非）。
                bool windowsTarget = cfg.Os == OsType.Windows;
                string commandText = windowsTarget
                    ? "powershell -NoProfile -NonInteractive -EncodedCommand "
                      + WindowsMetrics.BuildEncodedCommand()
                    : LinuxScript;

                // 远程命令原文只在 trace 级别输出：它很长（尤其 Windows 那条
                // Base64 编码的命令），日常级别下会把日志冲得没法看。
                if (Logger.IsEnabled(LogLevel.Trace))
                {
                    Logger.Trace("SSH", "执行命令（" + (windowsTarget ? "Windows" : "Linux") +
                                        " 脚本）：\n" + commandText);
                }

                string output;
                using (var command = client.CreateCommand(commandText))
                {
                    command.CommandTimeout = TimeSpan.FromSeconds(45);
                    output = command.Execute();

                    if (Logger.IsEnabled(LogLevel.Trace))
                    {
                        Logger.Trace("SSH", "退出码=" + command.ExitStatus +
                                            " 输出长度=" + (output == null ? 0 : output.Length) +
                                            "\n--- 原始输出开始 ---\n" + output +
                                            "\n--- 原始输出结束 ---");
                    }

                    // 判定"脚本是否真的跑完了"，而不是只看退出码。
                    //
                    // 曾经这里只判断 ExitStatus != 0，结果在部分 Linux 服务器上
                    // 误报失败：有些 SSH 会话会返回非 0 退出码，但脚本其实完整执行、
                    // 数据也拿到了。改用脚本自己输出的结束标记来判断更可靠——
                    // 拿到了标记就说明采集成功，退出码是多少都不重要。
                    bool completed = windowsTarget
                        ? !string.IsNullOrWhiteSpace(output)
                        : (output != null && output.IndexOf("##END", StringComparison.Ordinal) >= 0);

                    if (!completed)
                    {
                        throw new InvalidOperationException(
                            DescribeFailure(command.ExitStatus, output, command.Error, windowsTarget));
                    }
                }

                client.Disconnect();

                if (windowsTarget) WindowsMetrics.Parse(output, snapshot, cfg.DisplayName);
                else ParseLinux(output, snapshot, cfg.DisplayName);
            }

            sw.Stop();
            snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return snapshot;
        }

        /// <summary>
        /// 把 SSH 执行失败翻译成能直接照做的提示。
        ///
        /// 最常见的失败是「系统类型选错了」：把 Linux 脚本发给 Windows 的 cmd.exe，
        /// 或者把 powershell 命令发给 Linux 的 sh。原始报错是
        /// "'LC_ALL' 不是内部或外部命令" 这类，用户完全看不出是配置问题。
        /// </summary>
        private static string DescribeFailure(int exitStatus, string stdout, string stderr,
                                              bool windowsTarget)
        {
            string detail = ((stderr ?? string.Empty) + "\n" + (stdout ?? string.Empty)).Trim();
            string lower = detail.ToLowerInvariant();

            bool looksLikeCmd =
                detail.IndexOf("不是内部或外部命令", StringComparison.Ordinal) >= 0 ||
                lower.Contains("is not recognized as an internal or external command") ||
                lower.Contains("'export' ") || lower.Contains("lc_all");

            bool looksLikeSh =
                lower.Contains("command not found") ||
                (lower.Contains("powershell") && lower.Contains("not found"));

            if (!windowsTarget && looksLikeCmd)
            {
                return "目标机的命令行不认识这些 Linux 命令，看起来它其实是 Windows。\n"
                     + "请编辑这台服务器，把「系统类型」从 Linux 改成 Windows。";
            }

            if (windowsTarget && looksLikeSh)
            {
                return "目标机上找不到 powershell，看起来它其实是 Linux。\n"
                     + "请编辑这台服务器，把「系统类型」从 Windows 改成 Linux。";
            }

            if (looksLikeCmd || looksLikeSh)
            {
                return "远程命令执行失败（返回码 " + exitStatus + "），目标系统类型可能与配置不符。\n"
                     + "请确认「系统类型」选择正确。原始输出：\n" + Truncate(detail, 200);
            }

            return "远程命令执行失败（返回码 " + exitStatus + "）。原始输出：\n"
                 + Truncate(detail, 400);
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "（无输出）";
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static void ParseLinux(string output, ServerSnapshot snapshot, string label)
        {
            string[] lines = (output ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            string section = string.Empty;

            string cpuLineA = null, cpuLineB = null;
            double memTotalKb = 0, memFreeKb = 0, memAvailableKb = -1, buffersKb = 0, cachedKb = 0;
            bool haveMemAvailable = false;
            double swapTotalKb = 0, swapFreeKb = 0;
            var disks = new List<DiskUsage>();

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    section = line.Substring(2);
                    continue;
                }

                switch (section)
                {
                    case "CPU_A": if (cpuLineA == null) cpuLineA = line; break;
                    case "CPU_B": if (cpuLineB == null) cpuLineB = line; break;

                    case "MEM":
                        {
                            int colon = line.IndexOf(':');
                            if (colon <= 0) break;
                            string key = line.Substring(0, colon);
                            string valText = line.Substring(colon + 1).Trim()
                                .Replace("kB", "").Trim();
                            double value;
                            if (!double.TryParse(valText, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out value)) break;

                            switch (key)
                            {
                                case "MemTotal": memTotalKb = value; break;
                                case "MemFree": memFreeKb = value; break;
                                case "MemAvailable": memAvailableKb = value; haveMemAvailable = true; break;
                                case "Buffers": buffersKb = value; break;
                                case "Cached": cachedKb = value; break;
                                case "SwapTotal": swapTotalKb = value; break;
                                case "SwapFree": swapFreeKb = value; break;
                            }
                        }
                        break;

                    case "LOAD":
                        {
                            double load;
                            if (double.TryParse(line, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out load))
                            {
                                snapshot.LoadAvg1 = load;
                            }
                        }
                        break;

                    case "UP":
                        {
                            double up;
                            if (double.TryParse(line, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out up))
                            {
                                snapshot.Uptime = TimeSpan.FromSeconds(up);
                            }
                        }
                        break;

                    case "HOST": snapshot.Hostname = line; break;
                    case "KERN": snapshot.Kernel = line; break;
                    case "MODEL": snapshot.CpuModel = line; break;

                    case "CORES":
                        {
                            int cores;
                            if (int.TryParse(line, out cores)) snapshot.CpuCores = cores;
                        }
                        break;

                    case "DISK": ParseDiskLine(line, disks, label); break;
                }
            }

            snapshot.CpuPercent = ComputeCpuPercent(cpuLineA, cpuLineB);

            // /proc/stat 没取到时，CPU 会静默显示成 0，看起来跟"真的很闲"一模一样。
            // 这种情况必须说出来，否则用户会以为机器负载为零。
            if (cpuLineA == null || cpuLineB == null)
            {
                Logger.Warn(label,
                    "/proc/stat 未取到 cpu 行（两次采样中至少缺一次），本轮 CPU 显示为 0");
            }

            if (snapshot.CpuCores <= 0)
            {
                // 不能用 Environment.ProcessorCount 兜底——那是监控机自己的核数，
                // 拿来当目标机的会得出完全错误的结论。
                snapshot.CpuCores = 1;
                Logger.Debug(label, "未取到目标机核心数（nproc / /proc/cpuinfo 都没有），暂按 1 处理");
            }

            snapshot.MemTotalBytes = memTotalKb * 1024.0;

            // 优先用 MemAvailable（内核 3.14+ 提供），否则按 Buffers/Cached 估算
            double usedKb;
            if (haveMemAvailable && memAvailableKb >= 0)
            {
                usedKb = memTotalKb - memAvailableKb;
            }
            else
            {
                usedKb = memTotalKb - memFreeKb - buffersKb - cachedKb;
            }
            if (usedKb < 0) usedKb = 0;
            if (usedKb > memTotalKb) usedKb = memTotalKb;
            snapshot.MemUsedBytes = usedKb * 1024.0;

            snapshot.SwapTotalBytes = swapTotalKb * 1024.0;
            snapshot.SwapUsedBytes = Math.Max(0, swapTotalKb - swapFreeKb) * 1024.0;

            snapshot.Disks = DiskFilter.Sort(disks);
        }

        /// <summary>
        /// 解析形如：/dev/vda1 41153856 8234567 30819289 22% /
        /// </summary>
        private static void ParseDiskLine(string line, List<DiskUsage> disks, string label)
        {
            string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
            {
                Logger.Trace(label, "df 输出行字段不足，已跳过：" + line);
                return;
            }

            double blocks, used;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out blocks) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out used))
            {
                Logger.Trace(label, "df 输出的容量字段无法解析，已跳过：" + line);
                return;
            }
            if (blocks <= 0) return;

            // 挂载点可能含空格，从第 6 列起全部拼接回来
            string mount = string.Join(" ", parts, 5, parts.Length - 5).Trim();
            string fs = parts[0];

            // 记录被过滤掉的挂载点：用户问"我的 /data 分区怎么没显示"时，
            // 这条 trace 能直接给出答案，而不用去猜过滤规则。
            if (!DiskFilter.IsRealDisk(fs, mount))
            {
                Logger.Trace(label, "已忽略挂载点 " + mount + "（" + fs + "）：内存盘或伪文件系统");
                return;
            }

            disks.Add(new DiskUsage
            {
                FileSystem = fs,
                Mount = mount,
                TotalBytes = blocks * 1024.0,
                UsedBytes = used * 1024.0
            });
        }

        /// <summary>
        /// /proc/stat 首行格式：cpu user nice system idle iowait irq softirq steal ...
        /// 用两次采样的差值算出区间内的真实占用率。
        /// </summary>
        private static double ComputeCpuPercent(string lineA, string lineB)
        {
            if (lineA == null || lineB == null) return 0;

            long[] a = ParseCpuCounters(lineA);
            long[] b = ParseCpuCounters(lineB);
            if (a == null || b == null || a.Length < 5 || b.Length < 5) return 0;

            long totalA = 0, totalB = 0;
            for (int i = 0; i < a.Length; i++) totalA += a[i];
            for (int i = 0; i < b.Length; i++) totalB += b[i];

            // idle 从 0 开始计数（field[3]），iowait 是 field[4]
            long idleA = a[3] + a[4];
            long idleB = b[3] + b[4];

            long totalDelta = totalB - totalA;
            long idleDelta = idleB - idleA;
            if (totalDelta <= 0) return 0;

            double busy = (double)(totalDelta - idleDelta) / totalDelta * 100.0;
            if (busy < 0) busy = 0;
            if (busy > 100) busy = 100;
            return busy;
        }

        private static long[] ParseCpuCounters(string line)
        {
            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu") return null;

            var values = new long[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                long v;
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                {
                    return null;
                }
                values[i - 1] = v;
            }
            return values;
        }
    }
}
