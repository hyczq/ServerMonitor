using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>一次对话请求的结果。</summary>
    public class AiReply
    {
        public bool Success { get; set; }
        public string Content { get; set; }
        public string Error { get; set; }
        public long ElapsedMs { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }

        public int TotalTokens { get { return PromptTokens + CompletionTokens; } }
    }

    /// <summary>
    /// AI 接口客户端，走 OpenAI 兼容的 /chat/completions 协议。
    ///
    /// 用这套协议而不是各家私有 SDK，是为了「换服务商只改地址和模型名」：
    /// DeepSeek 官方、内网自建的 vLLM / Ollama 都兼容它，不用改代码。
    ///
    /// 用 HttpWebRequest 而不是 HttpClient：后者在 net45 上要额外引包，
    /// 而且超时只能整体取消、拿不到分阶段控制，对「必须能降级」的场景不合适。
    /// </summary>
    internal static class AiClient
    {
        /// <summary>
        /// 显式启用 TLS 1.2。
        ///
        /// .NET Framework 4.5 的默认值**不包含** TLS 1.2，
        /// 直接调 HTTPS 会失败并报「基础连接已关闭」这类看不出原因的错。
        /// 这是 net45 上做任何 HTTPS 请求都会踩的坑。
        /// </summary>
        public static void EnableModernTls()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // 老系统上个别枚举值可能不存在，启用失败不影响其它协议
            }
        }

        /// <summary>
        /// 发一条对话请求。
        ///
        /// 失败时不抛异常，而是通过 <see cref="AiReply.Error"/> 返回原因——
        /// AI 是锦上添花的功能，出问题绝不能把采集、告警这些主流程带崩。
        /// </summary>
        public static AiReply Chat(AppSettings settings, string systemPrompt,
                                   string userPrompt, int maxTokens)
        {
            var reply = new AiReply();
            var sw = Stopwatch.StartNew();

            try
            {
                EnableModernTls();

                string url = (settings.AiEndpoint ?? string.Empty).TrimEnd('/') + "/chat/completions";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.UserAgent = "ServerMonitor/1.0";
                request.Timeout = settings.AiTimeoutSeconds * 1000;
                request.ReadWriteTimeout = settings.AiTimeoutSeconds * 1000;
                request.Headers["Authorization"] = "Bearer " + (settings.AiApiKey ?? string.Empty);

                var body = new JObject
                {
                    ["model"] = settings.AiModel,
                    ["messages"] = new JArray
                    {
                        new JObject { ["role"] = "system", ["content"] = systemPrompt ?? string.Empty },
                        new JObject { ["role"] = "user", ["content"] = userPrompt ?? string.Empty }
                    },
                    ["max_tokens"] = maxTokens,
                    ["stream"] = false
                };

                byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(payload, 0, payload.Length);
                }

                string responseText;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseText = reader.ReadToEnd();
                }

                sw.Stop();
                reply.ElapsedMs = sw.ElapsedMilliseconds;

                ParseResponse(responseText, reply);

                Logger.Debug("AI", "调用成功 模型=" + settings.AiModel +
                                   " 耗时=" + reply.ElapsedMs + "ms" +
                                   " tokens=" + reply.PromptTokens + "+" + reply.CompletionTokens);
                return reply;
            }
            catch (WebException ex)
            {
                sw.Stop();
                reply.ElapsedMs = sw.ElapsedMilliseconds;
                reply.Error = DescribeWebError(ex);
                Logger.Warn("AI", "调用失败：" + reply.Error, ex);
                return reply;
            }
            catch (Exception ex)
            {
                sw.Stop();
                reply.ElapsedMs = sw.ElapsedMilliseconds;
                reply.Error = ex.Message;
                Logger.Warn("AI", "调用失败：" + ex.Message, ex);
                return reply;
            }
        }

        private static void ParseResponse(string responseText, AiReply reply)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                reply.Error = "接口返回了空内容";
                return;
            }

            var root = JObject.Parse(responseText);

            JToken choices = root["choices"];
            if (choices == null || choices.Type != JTokenType.Array || ((JArray)choices).Count == 0)
            {
                // 有些兼容实现出错时也返回 HTTP 200，把原因写在 body 里
                JToken error = root["error"];
                reply.Error = error != null
                    ? "接口返回错误：" + (error["message"] ?? error).ToString()
                    : "接口返回的内容里没有 choices 字段";
                return;
            }

            JToken message = choices[0]["message"];
            reply.Content = message != null && message["content"] != null
                ? message["content"].ToString()
                : string.Empty;

            JToken usage = root["usage"];
            if (usage != null)
            {
                reply.PromptTokens = usage["prompt_tokens"] != null
                    ? usage["prompt_tokens"].Value<int>() : 0;
                reply.CompletionTokens = usage["completion_tokens"] != null
                    ? usage["completion_tokens"].Value<int>() : 0;
            }

            reply.Success = true;
        }

        /// <summary>把 HTTP 层错误翻译成能照着排查的说明。</summary>
        private static string DescribeWebError(WebException ex)
        {
            if (ex.Status == WebExceptionStatus.Timeout)
            {
                return "请求超时（" + ex.Message + "）。内网访问外部接口可能需要配置代理";
            }

            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                if (ex.Status == WebExceptionStatus.NameResolutionFailure ||
                    ex.Status == WebExceptionStatus.ConnectFailure)
                {
                    return "无法连接接口地址：内网可能访问不了外网，或地址填写有误";
                }
                return ex.Message;
            }

            int code = (int)response.StatusCode;
            string detail = string.Empty;
            try
            {
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        // 尽量取出 error.message，取不到就原样截断
                        try
                        {
                            JToken error = JObject.Parse(body)["error"];
                            string msg = error != null ? (error["message"] ?? error).ToString() : null;
                            detail = msg ?? body;
                        }
                        catch
                        {
                            detail = body;
                        }
                        if (detail.Length > 200) detail = detail.Substring(0, 200) + "…";
                    }
                }
            }
            catch
            {
            }

            string hint;
            switch (code)
            {
                case 401:
                    hint = "密钥无效或已过期，请检查 API Key";
                    break;
                case 402:
                    hint = "账户余额不足";
                    break;
                case 404:
                    hint = "接口地址或模型名不存在，请核对地址与模型名";
                    break;
                case 429:
                    hint = "请求过于频繁，已被限流";
                    break;
                default:
                    hint = code >= 500 ? "服务端错误，稍后重试" : "请核对配置";
                    break;
            }

            return "接口返回 HTTP " + code + "（" + hint + "）" +
                   (string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail);
        }

        /// <summary>
        /// 拉取可用模型列表（OpenAI 兼容的 GET /models）。
        ///
        /// 有这个方法就不必把模型名写死在程序里：服务商改名是常事
        /// （DeepSeek 一年内改过 deepseek-chat → deepseek-v4-flash → deepseek-flash），
        /// 让用户点一下就能拿到当前真实可用的名字，比任何提示文案都可靠。
        /// </summary>
        public static List<string> ListModels(AppSettings settings, out string error)
        {
            error = null;
            var models = new List<string>();

            try
            {
                EnableModernTls();

                string url = (settings.AiEndpoint ?? string.Empty).TrimEnd('/') + "/models";
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json";
                request.UserAgent = "ServerMonitor/1.0";
                request.Timeout = settings.AiTimeoutSeconds * 1000;
                request.ReadWriteTimeout = settings.AiTimeoutSeconds * 1000;
                request.Headers["Authorization"] = "Bearer " + (settings.AiApiKey ?? string.Empty);

                string responseText;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseText = reader.ReadToEnd();
                }

                var root = JObject.Parse(responseText);
                JToken data = root["data"];
                if (data == null || data.Type != JTokenType.Array)
                {
                    error = "接口返回的内容里没有 data 字段，可能不是 OpenAI 兼容的实现";
                    return models;
                }

                foreach (JToken item in (JArray)data)
                {
                    string id = item["id"] != null ? item["id"].ToString() : null;
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                }

                models.Sort(StringComparer.OrdinalIgnoreCase);

                Logger.Info("AI", "已获取模型列表，共 " + models.Count + " 个：" +
                                  string.Join(", ", models.ToArray()));
                return models;
            }
            catch (WebException ex)
            {
                error = DescribeWebError(ex);
                Logger.Warn("AI", "获取模型列表失败：" + error, ex);
                return models;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Logger.Warn("AI", "获取模型列表失败：" + ex.Message, ex);
                return models;
            }
        }

        /// <summary>
        /// 配置页的「测试连接」。发一条极短的请求，验证地址、密钥、模型名三件事。
        /// </summary>
        public static AiReply TestConnection(AppSettings settings)
        {
            Logger.Info("AI", "测试连接 地址=" + settings.AiEndpoint +
                              " 模型=" + settings.AiModel +
                              " 密钥=" + Logger.DescribeSecret(settings.AiApiKey));

            AiReply reply = Chat(settings,
                "你是一个测试助手。",
                "请只回复两个字：正常",
                16);

            if (reply.Success)
            {
                Logger.Info("AI", "测试连接成功 耗时=" + reply.ElapsedMs + "ms");
            }

            return reply;
        }
    }
}
