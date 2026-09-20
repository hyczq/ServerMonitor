using System;
using Newtonsoft.Json;

namespace ServerMonitor.Models
{
    /// <summary>全局设置，对应 data/settings.json。</summary>
    public class AppSettings
    {
        /// <summary>自动刷新间隔（秒）。</summary>
        public int RefreshSeconds { get; set; }

        /// <summary>达到该百分比即视为"偏高"。</summary>
        public double WarnThreshold { get; set; }

        /// <summary>达到该百分比即视为"告警"。</summary>
        public double CriticalThreshold { get; set; }

        /// <summary>是否使用深色主题。</summary>
        public bool DarkTheme { get; set; }

        /// <summary>原始采样文件保留天数，超期自动清理；日汇总永久保留。</summary>
        public int RawRetentionDays { get; set; }

        /// <summary>卡片上趋势图的采样点数。</summary>
        public int SparklinePoints { get; set; }

        /// <summary>同时采集的最大并发数，避免网络与目标机压力过大。</summary>
        public int MaxConcurrency { get; set; }

        // ---------- AI ----------

        /// <summary>是否启用 AI 功能。关闭时界面上的 AI 相关按钮不显示。</summary>
        public bool AiEnabled { get; set; }

        /// <summary>
        /// 接口地址（OpenAI 兼容格式的 base url）。
        /// 默认 DeepSeek 官方地址；内网自建的 vLLM / Ollama 也兼容这套协议，
        /// 改成它们各自的地址即可，不用改代码。
        /// </summary>
        public string AiEndpoint { get; set; }

        /// <summary>
        /// 模型名。
        ///
        /// 刻意用字符串而不是写死的下拉框：DeepSeek 的模型名一年内改过几次
        /// （deepseek-chat → deepseek-v4-flash → deepseek-flash），
        /// 硬编码的话对方一改名程序就废了，只能等发新版本。
        /// </summary>
        public string AiModel { get; set; }

        /// <summary>请求超时（秒）。</summary>
        public int AiTimeoutSeconds { get; set; }

        /// <summary>明文密钥，仅内存中使用，不参与序列化。</summary>
        [JsonIgnore]
        public string AiApiKey { get; set; }

        /// <summary>DPAPI 加密后的密钥（Base64），落盘用。</summary>
        [JsonProperty("aiApiKeyEnc")]
        public string AiApiKeyEncrypted { get; set; }

        // ---------- 日志 ----------

        /// <summary>
        /// 日志级别：Error / Warn / Info / Debug / Trace。
        /// 刻意用字符串而不是枚举：settings.json 是人会去手改的，
        /// 写 "Debug" 比写 3 直观得多。
        /// </summary>
        public string LogLevel { get; set; }

        /// <summary>日志文件保留天数。</summary>
        public int LogRetentionDays { get; set; }

        // ---------- Webhook ----------

        /// <summary>是否启用 Webhook 推送。</summary>
        public bool WebhookEnabled { get; set; }

        /// <summary>推送目标类型，决定请求体的结构。</summary>
        public WebhookProvider WebhookProvider { get; set; }

        /// <summary>PushPlus 的消息标题。</summary>
        public string WebhookTitle { get; set; }

        /// <summary>PushPlus 的消息模板：txt / html / markdown / json。</summary>
        public string WebhookTemplate { get; set; }

        /// <summary>PushPlus 群组编码，留空表示只发给自己。</summary>
        public string WebhookTopic { get; set; }

        /// <summary>接收推送的接口地址。</summary>
        public string WebhookUrl { get; set; }

        /// <summary>鉴权令牌，会放在 Authorization 和 X-Monitor-Token 两个头里。</summary>
        public string WebhookToken { get; set; }

        /// <summary>只在存在异常时才推送，避免无人值守时刷屏。</summary>
        public bool WebhookOnlyOnProblem { get; set; }

        /// <summary>请求超时（秒）。</summary>
        public int WebhookTimeoutSeconds { get; set; }

        public string WebhookContentType { get; set; }

        /// <summary>忽略 HTTPS 证书错误（内网自签名证书常见）。默认关闭。</summary>
        public bool WebhookIgnoreCertErrors { get; set; }

        public AppSettings()
        {
            RefreshSeconds = 60;
            WarnThreshold = 70;
            CriticalThreshold = 90;
            DarkTheme = true;
            RawRetentionDays = 30;
            SparklinePoints = 40;
            MaxConcurrency = 8;

            AiEnabled = false;
            AiEndpoint = "https://api.deepseek.com";
            AiModel = "deepseek-flash";
            AiTimeoutSeconds = 30;
            AiApiKey = string.Empty;

            LogLevel = "Info";
            LogRetentionDays = 14;

            WebhookEnabled = false;
            WebhookProvider = WebhookProvider.Generic;
            WebhookUrl = string.Empty;
            WebhookToken = string.Empty;
            WebhookTitle = "服务器监控告警";
            WebhookTemplate = "txt";
            WebhookTopic = string.Empty;
            WebhookOnlyOnProblem = true;
            WebhookTimeoutSeconds = 10;
            WebhookContentType = "application/json";
            WebhookIgnoreCertErrors = false;
        }

        /// <summary>把越界的配置夹回合法范围，避免用户改坏配置文件后程序异常。</summary>
        public void Normalize()
        {
            if (RefreshSeconds < 10) RefreshSeconds = 10;
            if (RefreshSeconds > 3600) RefreshSeconds = 3600;

            if (WarnThreshold < 1) WarnThreshold = 1;
            if (WarnThreshold > 99) WarnThreshold = 99;

            if (CriticalThreshold <= WarnThreshold) CriticalThreshold = WarnThreshold + 1;
            if (CriticalThreshold > 100) CriticalThreshold = 100;

            if (RawRetentionDays < 1) RawRetentionDays = 1;
            if (RawRetentionDays > 3650) RawRetentionDays = 3650;

            if (SparklinePoints < 10) SparklinePoints = 10;
            if (SparklinePoints > 240) SparklinePoints = 240;

            if (MaxConcurrency < 1) MaxConcurrency = 1;
            if (MaxConcurrency > 64) MaxConcurrency = 64;

            if (AiTimeoutSeconds < 5) AiTimeoutSeconds = 5;
            if (AiTimeoutSeconds > 300) AiTimeoutSeconds = 300;

            if (string.IsNullOrWhiteSpace(AiEndpoint))
            {
                AiEndpoint = "https://api.deepseek.com";
            }
            else
            {
                AiEndpoint = AiEndpoint.Trim().TrimEnd('/');
            }

            if (string.IsNullOrWhiteSpace(AiModel))
            {
                AiModel = "deepseek-flash";
            }
            else
            {
                AiModel = AiModel.Trim();
            }

            // 没填密钥就不要保持"已启用"，否则每次调用都白报一次错
            if (string.IsNullOrWhiteSpace(AiApiKey)) AiEnabled = false;

            if (LogRetentionDays < 1) LogRetentionDays = 1;
            if (LogRetentionDays > 3650) LogRetentionDays = 3650;

            // 级别名写错时退回默认，避免配置手改错了导致日志系统失效
            if (string.IsNullOrWhiteSpace(LogLevel))
            {
                LogLevel = "Info";
            }
            else
            {
                try
                {
                    LogLevel normalized = (LogLevel)Enum.Parse(
                        typeof(LogLevel), LogLevel.Trim(), true);
                    LogLevel = normalized.ToString();
                }
                catch
                {
                    LogLevel = "Info";
                }
            }

            if (WebhookTimeoutSeconds < 1) WebhookTimeoutSeconds = 1;
            if (WebhookTimeoutSeconds > 120) WebhookTimeoutSeconds = 120;

            if (string.IsNullOrWhiteSpace(WebhookContentType))
            {
                WebhookContentType = "application/json";
            }

            // 地址没填就不要保持"已启用"，否则每轮都会白报一次错
            if (string.IsNullOrWhiteSpace(WebhookUrl)) WebhookEnabled = false;
            if (WebhookUrl != null) WebhookUrl = WebhookUrl.Trim();
        }
    }
}
