namespace ServerMonitor.Models
{
    /// <summary>被监控服务器的操作系统类型。</summary>
    public enum OsType
    {
        Linux = 0,
        Windows = 1
    }

    /// <summary>采集通道。Auto 表示按操作系统类型自动选择。</summary>
    public enum ProtocolType
    {
        Auto = 0,
        Ssh = 1,
        Wmi = 2,
        WinRm = 3
    }

    /// <summary>
    /// 日志级别。数值越大越详细，设置某个级别后 ≤ 它的日志都会输出。
    /// </summary>
    public enum LogLevel
    {
        /// <summary>仅严重错误（程序自身出问题、数据损坏等）</summary>
        Error = 0,

        /// <summary>错误 + 警告（含采集失败、认证失败这类运维事件）</summary>
        Warn = 1,

        /// <summary>错误 + 警告 + 一般操作信息。默认级别。</summary>
        Info = 2,

        /// <summary>再加上详细信息（单台采集耗时、连接建立、配置读写）</summary>
        Debug = 3,

        /// <summary>全部日志，含远程命令原文与原始输出。排查疑难问题时才开。</summary>
        Trace = 4
    }

    /// <summary>
    /// Webhook 推送目标。不同服务要求的请求体结构完全不同，不能一套格式打通。
    /// </summary>
    public enum WebhookProvider
    {
        /// <summary>通用 JSON：把完整的结构化状态直接 POST 过去，供自建接口取用。</summary>
        Generic = 0,

        /// <summary>
        /// PushPlus（推送加）：需要 token / content 这类字段，
        /// 且用「HTTP 200 + 响应体 code」表示结果，与通用格式不兼容。
        /// </summary>
        PushPlus = 1
    }

    /// <summary>
    /// 资源用量健康等级，共三档。刻意只保留三档：
    /// 四档时"偏高(琥珀)"与"严重(橙)"在正常视觉下的色差低于可辨识底线，
    /// 用户在仪表盘上无法一眼区分，反而降低可读性。
    /// </summary>
    public enum HealthLevel
    {
        Unknown = 0,
        Good = 1,
        Warning = 2,
        Critical = 3,
        Offline = 4
    }
}
