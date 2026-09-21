using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 把服务器状态以 JSON POST 到指定接口。
    ///
    /// 用 HttpWebRequest 而不是 HttpClient：前者在 .NET 4.5 上就是完整实现，
    /// 不需要额外引包，也不用担心 HttpClient 在这些老系统上的 TLS 默认值问题。
    /// </summary>
    internal static class WebhookNotifier
    {
        /// <summary>
        /// 按配置的渠道组装请求体。
        /// </summary>
        /// <param name="aiSummary">
        /// AI 写的正文，为 null 时用模板拼装。
        /// 只在 PushPlus 渠道替换正文；通用 JSON 渠道额外加一个 aiSummary 字段，
        /// 不动原有结构化字段——对方的接口可能就是按那些字段解析的。
        /// </param>
        public static string BuildPayload(IList<ServerCardSnapshot> servers, AppSettings settings,
                                          string aiSummary = null)
        {
            return settings.WebhookProvider == WebhookProvider.PushPlus
                ? BuildPushPlusPayload(servers, settings, aiSummary)
                : BuildGenericPayload(servers, settings, aiSummary);
        }

        // ---------------------------------------------------------------
        // PushPlus（推送加）
        // ---------------------------------------------------------------

        private static string BuildPushPlusPayload(IList<ServerCardSnapshot> servers, AppSettings settings,
                                                   string aiSummary)
        {
            var root = new JObject
            {
                ["token"] = settings.WebhookToken ?? string.Empty,
                ["title"] = BuildPushPlusTitle(servers, settings),
                ["content"] = aiSummary ?? BuildPushPlusContent(servers),
                ["template"] = string.IsNullOrWhiteSpace(settings.WebhookTemplate)
                    ? "txt"
                    : settings.WebhookTemplate.Trim()
            };

            // 群组编码留空表示只发给自己，此时不能带这个字段
            if (!string.IsNullOrWhiteSpace(settings.WebhookTopic))
            {
                root["topic"] = settings.WebhookTopic.Trim();
            }

            // 用 None 不缩进：消息内容本身已经排好版，再套一层缩进反而难读
            return root.ToString(Formatting.None);
        }

        private static string BuildPushPlusTitle(IList<ServerCardSnapshot> servers, AppSettings settings)
        {
            string baseTitle = string.IsNullOrWhiteSpace(settings.WebhookTitle)
                ? "服务器监控告警"
                : settings.WebhookTitle.Trim();

            int problems = servers.Count(s => !s.Online ||
                                               s.Level == HealthLevel.Warning ||
                                               s.Level == HealthLevel.Critical);

            // 标题里带异常数，微信通知栏一眼就能看出要不要点开
            return problems > 0
                ? baseTitle + "：" + problems + "/" + servers.Count + " 台需关注"
                : baseTitle + "：全部正常";
        }

        /// <summary>
        /// 消息正文。微信推送能显示的空间有限，所以只列异常项 + 一行平均用量，
        /// 完整明细留给「巡检报告」功能。
        /// </summary>
        private static string BuildPushPlusContent(IList<ServerCardSnapshot> servers)
        {
            var sb = new StringBuilder();
            var online = servers.Where(s => s.Online).ToList();
            var problems = servers
                .Where(s => !s.Online || s.Level == HealthLevel.Warning || s.Level == HealthLevel.Critical)
                .OrderByDescending(s => s.Online ? (int)s.Level : 99)
                .ToList();

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "共 {0} 台服务器，{1} 台在线", servers.Count, online.Count));

            if (problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("需要关注");
                sb.AppendLine("------------------------------");

                foreach (ServerCardSnapshot s in problems)
                {
                    if (!s.Online)
                    {
                        sb.AppendLine("[离线] " + Safe(s.Name) + " (" + Safe(s.Host) + ")");
                        sb.AppendLine("    " + Safe(s.Error));
                        continue;
                    }

                    sb.AppendLine("[" + Converters.Formats.LevelText(s.Level) + "] " +
                                  Safe(s.Name) + " (" + Safe(s.Host) + ")");
                    sb.AppendLine("    CPU " + Pct(s.CpuPercent) +
                                  "  内存 " + Pct(s.MemPercent) +
                                  "  磁盘 " + Pct(s.DiskPercent));
                }

                int normal = servers.Count - problems.Count;
                if (normal > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("其余 " + normal + " 台正常");
                }
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("全部服务器运行正常。");
            }

            sb.AppendLine();
            if (online.Count > 0)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "平均：CPU {0:0.0}%  内存 {1:0.0}%  磁盘 {2:0.0}%",
                    online.Average(s => s.CpuPercent),
                    online.Average(s => s.MemPercent),
                    online.Average(s => s.DiskPercent)));
            }
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // PushPlus 文档写明 template=txt 时换行要用 \n。
            // AppendLine 在 Windows 上产生 \r\n，\r 会被当成可见字符显示出来。
            return sb.ToString().Replace("\r\n", "\n").TrimEnd();
        }

        private static string Pct(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        // ---------------------------------------------------------------
        // 通用 JSON
        // ---------------------------------------------------------------

        /// <summary>
        /// 组装推送内容。字段名用英文，方便对接方的接口直接取用；
        /// 嵌套结构保持扁平，减少对方的解析工作。
        /// </summary>
        private static string BuildGenericPayload(IList<ServerCardSnapshot> servers, AppSettings settings,
                                                  string aiSummary)
        {
            var online = servers.Where(s => s.Online).ToList();

            var root = new JObject
            {
                ["app"] = "ServerMonitor",
                ["host"] = Safe(Environment.MachineName),
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["summary"] = new JObject
                {
                    ["total"] = servers.Count,
                    ["online"] = online.Count,
                    ["offline"] = servers.Count - online.Count,
                    ["warning"] = servers.Count(s => s.Level == HealthLevel.Warning),
                    ["critical"] = servers.Count(s => s.Level == HealthLevel.Critical),
                    ["avgCpu"] = online.Count > 0 ? Math.Round(online.Average(s => s.CpuPercent), 1) : 0,
                    ["avgMem"] = online.Count > 0 ? Math.Round(online.Average(s => s.MemPercent), 1) : 0,
                    ["avgDisk"] = online.Count > 0 ? Math.Round(online.Average(s => s.DiskPercent), 1) : 0,
                    ["warnThreshold"] = settings.WarnThreshold,
                    ["criticalThreshold"] = settings.CriticalThreshold
                }
            };

            var array = new JArray();
            foreach (ServerCardSnapshot s in servers)
            {
                var disks = new JArray();
                foreach (DiskSnapshot d in s.Disks)
                {
                    disks.Add(new JObject
                    {
                        ["mount"] = d.Mount,
                        ["percent"] = Math.Round(d.Percent, 1),
                        ["usage"] = d.UsageText
                    });
                }

                array.Add(new JObject
                {
                    ["name"] = s.Name,
                    ["host"] = s.Host,
                    ["os"] = s.OsLabel,
                    ["protocol"] = s.ProtocolLabel,
                    ["online"] = s.Online,
                    ["level"] = LevelKey(s.Level),
                    ["levelText"] = Converters.Formats.LevelText(s.Level),
                    ["cpu"] = Math.Round(s.CpuPercent, 1),
                    ["mem"] = Math.Round(s.MemPercent, 1),
                    ["disk"] = Math.Round(s.DiskPercent, 1),
                    ["memText"] = s.MemText,
                    ["diskText"] = s.DiskText,
                    ["uptime"] = s.UptimeText,
                    ["updated"] = s.UpdatedText,
                    ["error"] = s.Error,
                    ["disks"] = disks
                });
            }
            root["servers"] = array;

            // AI 写的正文单独放一个字段，不动上面的结构化数据——
            // 对方的接口可能就是按那些字段解析的，替换掉会把人家的对接搞坏。
            if (!string.IsNullOrEmpty(aiSummary)) root["aiSummary"] = aiSummary;

            return root.ToString(Formatting.Indented);
        }

        private static string LevelKey(HealthLevel level)
        {
            switch (level)
            {
                case HealthLevel.Good: return "good";
                case HealthLevel.Warning: return "warning";
                case HealthLevel.Critical: return "critical";
                case HealthLevel.Offline: return "offline";
                default: return "unknown";
            }
        }

        /// <summary>
        /// 是否应该发送。返回 null 表示发送，否则返回跳过原因。
        /// </summary>
        public static string ShouldSkip(IList<ServerCardSnapshot> servers, AppSettings settings)
        {
            if (!settings.WebhookEnabled) return "Webhook 未启用";
            if (string.IsNullOrWhiteSpace(settings.WebhookUrl)) return "未填写 Webhook 地址";

            if (settings.WebhookOnlyOnProblem)
            {
                bool hasProblem = servers.Any(s => !s.Online ||
                                                   s.Level == HealthLevel.Warning ||
                                                   s.Level == HealthLevel.Critical);
                if (!hasProblem) return "当前无异常，按设置跳过";
            }

            return null;
        }

        /// <summary>
        /// 发送。失败不抛异常，而是通过 error 返回原因——推送失败不该影响采集。
        /// </summary>
        public static bool Send(string url, string json, AppSettings settings, out string error)
        {
            error = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = settings.WebhookContentType;
                request.Timeout = settings.WebhookTimeoutSeconds * 1000;
                request.ReadWriteTimeout = settings.WebhookTimeoutSeconds * 1000;
                request.UserAgent = "ServerMonitor/1.0";

                // PushPlus 的令牌是放在请求体里的，走请求头没意义
                if (settings.WebhookProvider != WebhookProvider.PushPlus &&
                    !string.IsNullOrWhiteSpace(settings.WebhookToken))
                {
                    // 两种常见的鉴权头都带上，对接方用哪种都能取到
                    request.Headers["Authorization"] = settings.WebhookToken;
                    request.Headers["X-Monitor-Token"] = settings.WebhookToken;
                }

                if (settings.WebhookIgnoreCertErrors &&
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // 内网接口常用自签名证书。这是显式开关，默认关闭。
                    ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
                }

                byte[] body = Encoding.UTF8.GetBytes(json);
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(body, 0, body.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    string responseText = ReadBody(response);

                    if (status < 200 || status >= 300)
                    {
                        error = "接口返回 HTTP " + status + " " + response.StatusDescription +
                                (string.IsNullOrWhiteSpace(responseText)
                                    ? string.Empty
                                    : "：" + Truncate(responseText, 200));
                        return false;
                    }

                    // PushPlus 是"HTTP 200 + 响应体里的 code"表示结果。
                    // 只看 HTTP 状态码的话，token 无效也会被判成推送成功。
                    if (settings.WebhookProvider == WebhookProvider.PushPlus)
                    {
                        return CheckPushPlusBody(responseText, out error);
                    }

                    return true;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    error = "接口返回 HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
                    try
                    {
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            string body = reader.ReadToEnd();
                            if (!string.IsNullOrWhiteSpace(body))
                            {
                                error += "：" + (body.Length > 200 ? body.Substring(0, 200) + "…" : body);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    error = "无法连接：" + ex.Message;
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Safe(string text)
        {
            return text ?? string.Empty;
        }

        private static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null) return string.Empty;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 校验 PushPlus 的响应体。它用 code=200 表示成功，
        /// HTTP 状态码永远是 200，只看状态码会把失败当成功。
        /// </summary>
        private static bool CheckPushPlusBody(string body, out string error)
        {
            error = null;

            // 有些自建代理不回响应体，这种情况不做判断，避免误报失败
            if (string.IsNullOrWhiteSpace(body)) return true;

            try
            {
                var root = JObject.Parse(body);
                JToken codeToken = root["code"];
                if (codeToken == null) return true;

                int code;
                if (!int.TryParse(codeToken.ToString(), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out code))
                {
                    return true;
                }

                if (code == 200) return true;

                string message = root["msg"] != null ? root["msg"].ToString() : "无附加说明";
                error = "PushPlus 返回 code=" + code + "：" + message;

                if (code == 401 || code == 402)
                {
                    error += "。请到 pushplus 后台确认令牌是否有效、是否已过期。";
                }
                return false;
            }
            catch
            {
                // 响应体不是 JSON 就不下结论
                return true;
            }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
