using System;
using Newtonsoft.Json;

namespace ServerMonitor.Models
{
    /// <summary>一台被监控服务器的连接配置。该对象与 servers.json 一一对应。</summary>
    public class ServerConfig
    {
        public string Id { get; set; }

        /// <summary>显示名称，留空时回退为 Host。</summary>
        public string Name { get; set; }

        /// <summary>内网 IP 或主机名。</summary>
        public string Host { get; set; }

        /// <summary>端口。0 表示使用该协议的默认端口。</summary>
        public int Port { get; set; }

        public string Username { get; set; }

        /// <summary>明文口令，仅在内存中使用，不参与序列化。</summary>
        [JsonIgnore]
        public string Password { get; set; }

        /// <summary>DPAPI 加密后的口令（Base64），落盘时使用。</summary>
        [JsonProperty("passwordEnc")]
        public string PasswordEncrypted { get; set; }

        public OsType Os { get; set; }

        public ProtocolType Protocol { get; set; }

        public bool Enabled { get; set; }

        /// <summary>分组名，用于总览页筛选。</summary>
        public string Group { get; set; }

        public ServerConfig()
        {
            Id = Guid.NewGuid().ToString("N");
            Enabled = true;
            Protocol = ProtocolType.Auto;
            Os = OsType.Linux;
            Port = 0;
            Group = "默认";
        }

        /// <summary>实际使用的协议（把 Auto 解析成具体通道）。</summary>
        [JsonIgnore]
        public ProtocolType EffectiveProtocol
        {
            get
            {
                if (Protocol != ProtocolType.Auto) return Protocol;
                return Os == OsType.Windows ? ProtocolType.Wmi : ProtocolType.Ssh;
            }
        }

        /// <summary>实际使用的端口。</summary>
        [JsonIgnore]
        public int EffectivePort
        {
            get
            {
                if (Port > 0) return Port;
                switch (EffectiveProtocol)
                {
                    case ProtocolType.Ssh: return 22;
                    case ProtocolType.WinRm: return 5985;
                    default: return 0; // WMI 走 DCOM，无固定端口
                }
            }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Host : Name; }
        }

        /// <summary>用于界面展示的协议描述。</summary>
        [JsonIgnore]
        public string ProtocolLabel
        {
            get
            {
                switch (EffectiveProtocol)
                {
                    case ProtocolType.Ssh: return "SSH";
                    case ProtocolType.Wmi: return "WMI";
                    case ProtocolType.WinRm: return "WinRM";
                    default: return "未知";
                }
            }
        }

        public ServerConfig Clone()
        {
            return (ServerConfig)MemberwiseClone();
        }
    }
}
