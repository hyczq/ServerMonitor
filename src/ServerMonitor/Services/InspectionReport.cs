using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ServerMonitor.Converters;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 生成巡检报告（纯文本）。
    ///
    /// 面向"贴到工单/邮件里"的场景，所以：
    ///   - 用等宽对齐的纯文本，不依赖任何标记语言
    ///   - 异常项集中放在最前面，不用在几十行里找
    ///   - 结尾给出可直接执行的建议
    /// </summary>
    internal static class InspectionReport
    {
        public static string Build(IList<ServerCardSnapshot> servers, AppSettings settings,
                                   string dataDirectory)
        {
            var sb = new StringBuilder();
            DateTime now = DateTime.Now;

            int total = servers.Count;
            int online = servers.Count(s => s.Online);
            int warning = servers.Count(s => s.Level == HealthLevel.Warning);
            int critical = servers.Count(s => s.Level == HealthLevel.Critical);
            int offline = total - online;

            string line = new string('=', 66);
            string thin = new string('-', 66);

            sb.AppendLine(line);
            sb.AppendLine("                     服务器巡检报告");
            sb.AppendLine(line);
            sb.AppendLine("生成时间 : " + now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("报告主机 : " + Safe(Environment.MachineName));
            sb.AppendLine("数据目录 : " + Safe(dataDirectory));
            sb.AppendLine("阈值设置 : 偏高 >= " + settings.WarnThreshold.ToString("0.#", CultureInfo.InvariantCulture) +
                          "%   告警 >= " + settings.CriticalThreshold.ToString("0.#", CultureInfo.InvariantCulture) + "%");
            sb.AppendLine();

            // ---- 总览 ----
            sb.AppendLine(thin);
            sb.AppendLine("【一、总览】");
            sb.AppendLine(thin);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  服务器总数 : {0}", total));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  在线 / 离线: {0} / {1}", online, offline));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  告警 / 偏高: {0} / {1}", critical, warning));

            var onlineServers = servers.Where(s => s.Online).ToList();
            if (onlineServers.Count > 0)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  平均 CPU   : {0:0.0}%", onlineServers.Average(s => s.CpuPercent)));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  平均内存   : {0:0.0}%", onlineServers.Average(s => s.MemPercent)));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  平均磁盘   : {0:0.0}%", onlineServers.Average(s => s.DiskPercent)));
            }
            sb.AppendLine();

            // ---- 异常优先 ----
            var problems = servers
                .Where(s => !s.Online || s.Level == HealthLevel.Warning || s.Level == HealthLevel.Critical)
                .OrderByDescending(s => s.Online ? (int)s.Level : 99)
                .ToList();

            sb.AppendLine(thin);
            sb.AppendLine("【二、需要关注的项目】");
            sb.AppendLine(thin);

            if (problems.Count == 0)
            {
                sb.AppendLine("  全部正常，无需处理。");
            }
            else
            {
                foreach (ServerCardSnapshot s in problems)
                {
                    if (!s.Online)
                    {
                        sb.AppendLine("  [离线] " + Safe(s.Name) + "  (" + Safe(s.Host) + ")");
                        sb.AppendLine("         原因: " + Safe(s.Error));
                        continue;
                    }

                    var items = new List<string>();
                    if (s.CpuPercent >= settings.WarnThreshold)
                        items.Add("CPU " + Fmt(s.CpuPercent));
                    if (s.MemPercent >= settings.WarnThreshold)
                        items.Add("内存 " + Fmt(s.MemPercent));
                    foreach (var d in s.Disks)
                    {
                        if (d.Percent >= settings.WarnThreshold)
                            items.Add("磁盘 " + Safe(d.Mount) + " " + Fmt(d.Percent));
                    }

                    sb.AppendLine("  [" + Formats.LevelText(s.Level) + "] " + Safe(s.Name) +
                                  "  (" + Safe(s.Host) + ")");
                    sb.AppendLine("         超限项: " + (items.Count > 0 ? string.Join("，", items) : "—"));
                }
            }
            sb.AppendLine();

            // ---- 明细 ----
            sb.AppendLine(thin);
            sb.AppendLine("【三、服务器明细】");
            sb.AppendLine(thin);

            foreach (ServerCardSnapshot s in servers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine("  " + Safe(s.Name) + "  (" + Safe(s.Host) + ")");
                sb.AppendLine("    系统/通道 : " + Safe(s.OsLabel) + " / " + Safe(s.ProtocolLabel));
                sb.AppendLine("    状态      : " + (s.Online ? Formats.LevelText(s.Level) : "离线") +
                              "    更新时间: " + Safe(s.UpdatedText));

                if (!s.Online)
                {
                    sb.AppendLine("    失败原因  : " + Safe(s.Error));
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    CPU       : {0:0.0}%      内存: {1:0.0}%  ({2})",
                    s.CpuPercent, s.MemPercent, s.MemText));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "    磁盘总量  : {0}      运行时长: {1}",
                    s.DiskText, Safe(s.UptimeText)));

                if (s.Disks.Count > 0)
                {
                    sb.AppendLine("    分区明细  :");
                    foreach (var d in s.Disks)
                    {
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "      {0,-22} {1,7:0.0}%   {2}",
                            Trim(Safe(d.Mount), 22), d.Percent, Safe(d.UsageText)));
                    }
                }
                sb.AppendLine();
            }

            // ---- 建议 ----
            sb.AppendLine(thin);
            sb.AppendLine("【四、处理建议】");
            sb.AppendLine(thin);
            if (problems.Count == 0)
            {
                sb.AppendLine("  本次巡检未发现异常。");
            }
            else
            {
                if (critical > 0)
                    sb.AppendLine("  · 有 " + critical + " 台服务器达到告警阈值，建议立即处理。");
                if (warning > 0)
                    sb.AppendLine("  · 有 " + warning + " 台服务器资源偏高，建议安排清理或扩容。");
                if (offline > 0)
                    sb.AppendLine("  · 有 " + offline + " 台服务器采集失败，请检查网络、账号口令与服务状态。");
                sb.AppendLine("  · 磁盘长期高于阈值的分区，优先清理日志与临时文件。");
            }

            sb.AppendLine();
            sb.AppendLine(line);
            sb.AppendLine("报告结束。本报告由「服务器监控台」自动生成。");
            sb.AppendLine(line);

            return sb.ToString();
        }

        private static string Fmt(double percent)
        {
            return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        private static string Safe(string text)
        {
            return string.IsNullOrEmpty(text) ? "—" : text;
        }

        private static string Trim(string text, int max)
        {
            if (text == null) return "—";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }

    /// <summary>
    /// 生成报告用的服务器状态快照。
    /// 刻意不让报告层直接依赖 ViewModel —— 这样报告格式与界面改动互不影响。
    /// </summary>
    internal class ServerCardSnapshot
    {
        public string Name;
        public string Host;
        public string OsLabel;
        public string ProtocolLabel;
        public bool Online;
        public string Error;
        public HealthLevel Level;
        public double CpuPercent;
        public double MemPercent;
        public double DiskPercent;
        public string MemText;
        public string DiskText;
        public string UptimeText;
        public string UpdatedText;
        public List<DiskSnapshot> Disks = new List<DiskSnapshot>();
    }

    internal class DiskSnapshot
    {
        public string Mount;
        public double Percent;
        public string UsageText;
    }
}
