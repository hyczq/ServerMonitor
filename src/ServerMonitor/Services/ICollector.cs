using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 采集通道抽象。实现类在工作线程上同步执行，失败时直接抛异常，
    /// 由 <see cref="MonitorEngine"/> 统一捕获并转成离线快照。
    /// </summary>
    internal interface ICollector
    {
        /// <param name="cfg">目标服务器配置。</param>
        /// <param name="previous">上一次的快照，部分通道（如 WMI 的原始性能计数器）需要用它做差值。</param>
        ServerSnapshot Collect(ServerConfig cfg, ServerSnapshot previous);
    }
}
