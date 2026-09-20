using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// Windows 指标采集的公共部分。
    ///
    /// 这段 PowerShell 脚本和结果解析同时被两条通道使用：
    ///   WinRM 通道  —— 通过 PowerShell 远程调用
    ///   SSH 通道    —— 通过 ssh 执行 powershell.exe
    /// 抽出来是为了避免两处各写一份、日后改漏。
    /// </summary>
    internal static class WindowsMetrics
    {
        /// <summary>
        /// 远端脚本刻意使用 Get-WmiObject 而非 Get-CimInstance：
        /// 前者从 PowerShell 2.0 起就存在，兼容性最好。
        /// 结果以 JSON 输出，便于两条通道共用解析逻辑。
        /// </summary>
        public const string PowerShellScript = @"
$ErrorActionPreference = 'Stop'
# 关掉进度流：输出被重定向时（SSH 就是管道），PowerShell 会把进度记录
# 序列化成 CLIXML 混进 stdout，把 JSON 淹掉。
$ProgressPreference = 'SilentlyContinue'
# 输出改成 UTF-8。Windows 命令行默认按系统 ANSI 代码页输出（中文系统是 GBK），
# 而 SSH 客户端按 UTF-8 解码，系统名称这类中文会变成乱码。
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$os = Get-WmiObject Win32_OperatingSystem
$cs = Get-WmiObject Win32_ComputerSystem
$cpu = $null
try {
    $cpu = (Get-WmiObject Win32_PerfFormattedData_PerfOS_Processor -Filter ""Name='_Total'"").PercentProcessorTime
} catch { }
if ($cpu -eq $null) {
    try {
        $cpu = (Get-WmiObject Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    } catch { }
}
$disks = @()
try {
    $disks = @(Get-WmiObject Win32_LogicalDisk -Filter ""DriveType=3"" | ForEach-Object {
        [pscustomobject]@{
            mount = $_.DeviceID
            total = [double]$_.Size
            free  = [double]$_.FreeSpace
            fs    = $_.FileSystem
        }
    })
} catch { }
$boot = $null
try { $boot = $os.ConvertToDateTime($os.LastBootUpTime) } catch { }
$up = 0
if ($boot -ne $null) { $up = ((Get-Date) - $boot).TotalSeconds }
[pscustomobject]@{
    hostname  = $cs.Name
    cpu       = [double]$cpu
    cores     = [int]$cs.NumberOfLogicalProcessors
    memTotal  = [double]$os.TotalVisibleMemorySize * 1024
    memFree   = [double]$os.FreePhysicalMemory * 1024
    uptimeSec = [double]$up
    caption   = $os.Caption
    version   = $os.Version
    disks     = $disks
} | ConvertTo-Json -Compress -Depth 4
";

        /// <summary>
        /// 把脚本编码成 powershell.exe -EncodedCommand 需要的 Base64（UTF-16LE）。
        ///
        /// 走 SSH 时必须用这种方式传脚本：脚本里含引号、换行、$ 等字符，
        /// 直接拼进命令行会被远端的 cmd.exe 解析得面目全非。
        /// </summary>
        public static string BuildEncodedCommand()
        {
            byte[] bytes = Encoding.Unicode.GetBytes(PowerShellScript);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>解析脚本输出的 JSON，填充快照。</summary>
        /// <param name="rawOutput">
        /// 远端原始输出。可能夹杂 PowerShell 的 CLIXML 头、进度记录等噪声，
        /// 这里会先把真正的 JSON 摘出来。
        /// </param>
        public static void Parse(string rawOutput, ServerSnapshot snapshot, string label)
        {
            JObject root = JObject.Parse(ExtractJson(rawOutput));

            snapshot.Hostname = GetString(root, "hostname");
            snapshot.CpuPercent = Clamp(GetDouble(root, "cpu"));
            snapshot.CpuModel = GetString(root, "caption");
            snapshot.Kernel = "Windows " + GetString(root, "version");

            int cores = (int)GetDouble(root, "cores");
            if (cores <= 0)
            {
                Logger.Debug(label, "未取到目标机逻辑处理器数，暂按 1 处理");
                cores = 1;
            }
            snapshot.CpuCores = cores;

            double memTotal = GetDouble(root, "memTotal");
            double memFree = GetDouble(root, "memFree");
            snapshot.MemTotalBytes = memTotal;
            snapshot.MemUsedBytes = Math.Max(0, memTotal - memFree);

            // 内存总量为 0 说明 Win32_OperatingSystem 没查到，界面上会显示成 "--"，
            // 看着像"用了 0 字节"而不是"没取到"。
            if (memTotal <= 0)
            {
                Logger.Warn(label,
                    "未取到内存总量（Win32_OperatingSystem 查询失败），本轮内存显示为 --");
            }

            double uptimeSec = GetDouble(root, "uptimeSec");
            if (uptimeSec > 0) snapshot.Uptime = TimeSpan.FromSeconds(uptimeSec);

            JToken disks = root["disks"];
            if (disks != null && disks.Type != JTokenType.Null)
            {
                // 单个磁盘时 ConvertTo-Json 会退化成对象而非数组，两种都要兼容
                IEnumerable<JToken> items = disks.Type == JTokenType.Array
                    ? (IEnumerable<JToken>)disks
                    : new[] { disks };

                foreach (JToken item in items)
                {
                    if (item.Type != JTokenType.Object) continue;

                    double total = GetDouble((JObject)item, "total");
                    string mount = GetString((JObject)item, "mount");
                    if (total <= 0)
                    {
                        Logger.Trace(label, "已跳过容量为 0 的盘符 " + (mount ?? "(未知)") +
                                            "（通常是空的光驱或读卡器）");
                        continue;
                    }

                    double free = GetDouble((JObject)item, "free");
                    snapshot.Disks.Add(new DiskUsage
                    {
                        Mount = mount,
                        FileSystem = GetString((JObject)item, "fs"),
                        TotalBytes = total,
                        UsedBytes = Math.Max(0, total - free)
                    });
                }
            }

            if (snapshot.Disks.Count == 0)
            {
                Logger.Debug(label, "未发现可统计的磁盘分区（Win32_LogicalDisk 未返回固定磁盘）");
            }

            snapshot.Disks = DiskFilter.Sort(snapshot.Disks);
        }

        /// <summary>
        /// 从远端原始输出中摘出 JSON。
        ///
        /// 脚本用 ConvertTo-Json -Compress 输出的是单行 JSON，所以只要找到
        /// 那一行即可。之所以不能整体解析：PowerShell 的输出被重定向时
        /// 会在开头加 "#&lt; CLIXML"，并把进度流以 XML 形式追加在末尾。
        /// </summary>
        private static string ExtractJson(string rawOutput)
        {
            if (string.IsNullOrWhiteSpace(rawOutput))
            {
                throw new InvalidOperationException("远端没有返回任何数据。");
            }

            string trimmed = rawOutput.Trim();

            // 快路径：整段就是 JSON
            if (trimmed.Length > 1 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}')
            {
                return trimmed;
            }

            // 慢路径：从后往前找第一个形如完整对象的行
            string[] lines = rawOutput.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length > 1 && line[0] == '{' && line[line.Length - 1] == '}')
                {
                    return line;
                }
            }

            string preview = rawOutput.Length > 300 ? rawOutput.Substring(0, 300) + "…" : rawOutput;
            throw new InvalidOperationException(
                "无法从远端输出中解析出 JSON。远端返回的内容是：\n" + preview);
        }

        private static string GetString(JObject obj, string name)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.ToString();
        }

        private static double GetDouble(JObject obj, string name)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null) return 0;

            double value;
            if (double.TryParse(token.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0;
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
