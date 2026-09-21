using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 用 AI 把一次采集结果写成一段人话，作为 Webhook 推送的正文。
    ///
    /// 定位：**只影响文案，不参与告警判断**。
    ///   "发不发"由规则决定（阈值 + WebhookOnlyOnProblem），这一层不插手；
    ///   生成失败就返回 null，调用方退回原来模板拼装的正文。
    /// 守住这条，AI 接口挂了、限流了、说胡话了，最坏只是消息难看一点，
    /// 不会漏掉任何一条告警。反过来若让 AI 决定发不发，它一宕机监控就静默了。
    ///
    /// 另一个原则：**所有数字都在这里用 C# 算好再交给模型**。
    /// 大模型的算术不可靠，而告警里的数字必须准。提示词里也明令禁止它自己计算。
    /// </summary>
    internal static class AiSummary
    {
        /// <summary>
        /// 摘要挡在推送路径上，超时必须短于常规调用：
        /// 让用户等 30 秒才收到告警，比收到模板拼的正文更糟。
        /// </summary>
        private const int SummaryTimeoutSeconds = 20;

        /// <summary>提示词要求 200 字以内；这里只是防模型跑飞的安全上限。</summary>
        private const int MaxChars = 600;

        /// <summary>
        /// 输出预算。
        ///
        /// 值看着比「200 字」大得多，是因为它不只是正文的长度上限：
        /// 实测 deepseek-flash 是推理型模型，会**先花掉一段预算用于思考**，
        /// 思考内容走 reasoning_content，正文才进 content，两者共用同一个 max_tokens。
        /// 设 600 时出现过思考没结束就撞上限、content 为空、整条摘要退回模板的情况，
        /// 日志里只留下 tokens=732+600 这种线索。留到 2000 才稳定出正文。
        ///
        /// 按实际生成量计费，上限本身不产生费用；
        /// 真正约束正文长度的是提示词里的「200 字以内」和 <see cref="MaxChars"/>。
        /// </summary>
        private const int MaxTokens = 2000;

        /// <summary>
        /// 提示词。
        ///
        /// 这里对输出结构的约束写得比一般提示词细，是被实测逼出来的：
        /// 只写「200 字以内」时，模型会把输入重新排版一遍交回来——逐台罗列
        /// 每个指标，430 多字，等于白调一次接口（模板拼装的正文本来就干这个）。
        /// 给出明确的句子结构、并点名「不要逐台罗列」之后，输出才收敛成摘要。
        /// </summary>
        private const string SystemPrompt =
            "你是一名运维值班助手，负责把监控系统刚采集到的数据写成一条简短的告警消息，发到手机上。\n" +
            "\n" +
            "严格按下面的结构写，不要加别的段落：\n" +
            "第一句：总体情况——几台需要关注，问题是否集中在某类机器上。\n" +
            "第二句起：最该先处理的 1 到 3 台，每台一句话，说清哪台、哪个指标、到了多少。\n" +
            "最后一句：其余正常的机器一句话带过。\n" +
            "\n" +
            "必须遵守：\n" +
            "1. 全文 200 字以内，这是硬性要求。写完自己数一遍，超了就删掉次要细节。\n" +
            "2. 只使用「本次数据」里出现过的数字和主机名，不要自己计算、换算、外推或估计任何新数字。\n" +
            "3. 不要编造数据里没有的主机名、服务名、进程名、原因或处理建议。\n" +
            "4. 只写触发关注的机器和它超标的指标。不要把每台机器的每个指标都列一遍——那样只是把数据重排一遍，没有价值。\n" +
            "5. 输出纯文本，不要用 Markdown（不要 #、*、`、列表符号、表格、代码块）。\n" +
            "6. 不要寒暄、不要标题、不要结尾提问，直接说事。\n" +
            "7. 用简体中文。";

        /// <summary>
        /// 生成摘要。失败返回 null（原因已写进日志），调用方应退回模板正文。
        /// </summary>
        public static string Compose(IList<ServerCardSnapshot> servers, AppSettings settings)
        {
            if (servers == null || servers.Count == 0) return null;

            string userPrompt = BuildUserPrompt(servers, settings);

            AiReply reply = AiClient.Chat(settings, SystemPrompt, userPrompt,
                                          MaxTokens, SummaryTimeoutSeconds);

            if (!reply.Success)
            {
                Logger.Warn("AI", "告警摘要生成失败，本次推送退回模板正文：" + reply.Error);
                return null;
            }

            string text = Clean(reply.Content);
            if (text.Length == 0)
            {
                Logger.Warn("AI", "告警摘要返回了空内容，本次推送退回模板正文");
                return null;
            }

            Logger.Info("AI", "告警摘要生成成功 耗时=" + reply.ElapsedMs + "ms" +
                              " tokens=" + reply.PromptTokens + "+" + reply.CompletionTokens +
                              " 字数=" + text.Length);
            return text;
        }

        // ---------------------------------------------------------------
        // 提示词
        // ---------------------------------------------------------------

        private static string BuildUserPrompt(IList<ServerCardSnapshot> servers, AppSettings settings)
        {
            var sb = new StringBuilder();

            sb.AppendLine("本次数据");
            sb.AppendLine("采集时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "告警阈值：偏高 {0:0.#}% 及以上，告警 {1:0.#}% 及以上",
                settings.WarnThreshold, settings.CriticalThreshold));
            sb.AppendLine();

            List<ServerCardSnapshot> problems = servers
                .Where(IsProblem)
                .OrderByDescending(s => !s.Online ? 99 : (int)s.Level)
                .ToList();

            int onlineCount = servers.Count(s => s.Online);

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "共 {0} 台服务器，在线 {1} 台，离线 {2} 台。",
                servers.Count, onlineCount, servers.Count - onlineCount));
            sb.AppendLine();

            if (problems.Count == 0)
            {
                sb.AppendLine("没有任何服务器触发告警阈值，全部正常。");
                sb.AppendLine();
                sb.AppendLine("请写一条消息说明全部服务器运行正常。");
                return sb.ToString();
            }

            sb.AppendLine("以下服务器需要关注：");
            sb.AppendLine();

            foreach (ServerCardSnapshot s in problems)
            {
                sb.AppendLine("主机：" + s.Name + "（" + s.Host + "，" + s.OsLabel + "）");

                if (!s.Online)
                {
                    sb.AppendLine("  状态：离线，采集失败。失败原因：" +
                                  (string.IsNullOrWhiteSpace(s.Error) ? "未提供" : s.Error));
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine("  状态：" + Converters.Formats.LevelText(s.Level));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  CPU {0:0.0}%，内存 {1:0.0}%，整机最高磁盘 {2:0.0}%",
                    s.CpuPercent, s.MemPercent, s.DiskPercent));
                sb.AppendLine("  内存用量：" + s.MemText);
                sb.AppendLine("  磁盘用量：" + s.DiskText);
                sb.AppendLine("  已运行：" + s.UptimeText);
                sb.AppendLine();
            }

            // 磁盘明细只展开需要关注的主机，免得无关数据淹没重点、白烧 token
            var withDisks = problems.Where(s => s.Online && s.Disks.Count > 0).ToList();
            if (withDisks.Count > 0)
            {
                sb.AppendLine("上述主机的磁盘明细：");
                foreach (ServerCardSnapshot s in withDisks)
                {
                    foreach (DiskSnapshot d in s.Disks)
                    {
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  {0} 的 {1}：{2:0.0}%（{3}）", s.Name, d.Mount, d.Percent, d.UsageText));
                    }
                }
                sb.AppendLine();
            }

            // 正常机器的明细没有价值，但台数和均值能帮模型判断"是个别问题还是普遍问题"
            List<ServerCardSnapshot> healthy = servers.Where(s => s.Online && !IsProblem(s)).ToList();
            if (healthy.Count > 0)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "其余 {0} 台正常，平均：CPU {1:0.0}%，内存 {2:0.0}%，磁盘 {3:0.0}%。",
                    healthy.Count,
                    healthy.Average(s => s.CpuPercent),
                    healthy.Average(s => s.MemPercent),
                    healthy.Average(s => s.DiskPercent)));
                sb.AppendLine();
            }

            sb.AppendLine("请根据以上数据写一条告警消息。");
            return sb.ToString();
        }

        /// <summary>与 WebhookNotifier.ShouldSkip 的口径保持一致，避免两处判断出现分歧。</summary>
        private static bool IsProblem(ServerCardSnapshot s)
        {
            return !s.Online || s.Level == HealthLevel.Warning || s.Level == HealthLevel.Critical;
        }

        // ---------------------------------------------------------------
        // 输出清理
        // ---------------------------------------------------------------

        /// <summary>
        /// 清理模型输出。
        ///
        /// 提示词里已经要求纯文本，但不能只靠提示词——模型经常自作主张加上
        /// Markdown 标记，而 PushPlus 的 txt 模板会把这些符号原样显示出来。
        /// </summary>
        private static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string text = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            // 整段被 ``` 围起来的外壳：模型有时连纯文本也会包进代码块
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                int firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text.Substring(firstBreak + 1);

                int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text.Substring(0, lastFence);
            }

            var sb = new StringBuilder();
            int blankRun = 0;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = StripMarker(rawLine);

                if (line.Length == 0)
                {
                    blankRun++;
                    // 连续空行压成一个，避免消息里出现大片留白
                    if (blankRun > 1) continue;
                }
                else
                {
                    blankRun = 0;
                }

                sb.AppendLine(line);
            }

            text = sb.ToString().Trim();

            // 行内标记。模型特别爱加粗，而 txt 模板会把星号原样显示出来。
            // 这些符号在告警文本里没有正当用途，直接去掉不会误伤。
            text = text.Replace("**", string.Empty)
                       .Replace("`", string.Empty)
                       .Replace("__", string.Empty);

            if (text.Length > MaxChars) text = text.Substring(0, MaxChars) + "…";
            return text;
        }

        /// <summary>
        /// 剥掉行首的 Markdown 标记。
        /// 只认「标记 + 空格」和「连续 #」两种写法，避免误伤正文里的连字符。
        /// </summary>
        private static string StripMarker(string line)
        {
            string result = line.Trim();

            bool stripped = true;
            while (stripped)
            {
                stripped = false;

                if (result.Length >= 2 &&
                    (result[0] == '#' || result[0] == '>' || result[0] == '*' || result[0] == '-') &&
                    result[1] == ' ')
                {
                    result = result.Substring(1).TrimStart();
                    stripped = true;
                    continue;
                }

                // "###标题" 这种不带空格的写法
                int hashes = 0;
                while (hashes < result.Length && result[hashes] == '#') hashes++;
                if (hashes > 0 && hashes < result.Length)
                {
                    result = result.Substring(hashes).TrimStart();
                    stripped = true;
                }
            }

            return result.TrimEnd();
        }
    }
}
