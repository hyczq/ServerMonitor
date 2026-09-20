using System;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    internal static class CollectorFactory
    {
        private static readonly ICollector Ssh = new SshCollector();
        private static readonly ICollector Wmi = new WmiCollector();
        private static readonly ICollector WinRm = new WinRmCollector();

        public static ICollector Create(ServerConfig cfg)
        {
            switch (cfg.EffectiveProtocol)
            {
                case ProtocolType.Ssh: return Ssh;
                case ProtocolType.Wmi: return Wmi;
                case ProtocolType.WinRm: return WinRm;
                default:
                    throw new NotSupportedException(
                        "不支持的采集通道：" + cfg.EffectiveProtocol);
            }
        }
    }
}
