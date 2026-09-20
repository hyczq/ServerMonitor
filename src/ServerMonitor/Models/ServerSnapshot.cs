using System;
using System.Collections.Generic;
using System.Linq;

namespace ServerMonitor.Models
{
    /// <summary>单个挂载点的磁盘用量。</summary>
    public class DiskUsage
    {
        public string Mount { get; set; }
        public string FileSystem { get; set; }
        public double TotalBytes { get; set; }
        public double UsedBytes { get; set; }

        public double UsedPercent
        {
            get { return TotalBytes > 0 ? UsedBytes / TotalBytes * 100.0 : 0.0; }
        }

        public double FreeBytes
        {
            get { return Math.Max(0, TotalBytes - UsedBytes); }
        }
    }

    /// <summary>一次采集得到的服务器状态快照。</summary>
    public class ServerSnapshot
    {
        public string ServerId { get; set; }
        public DateTime TimeUtc { get; set; }
        public bool Online { get; set; }

        /// <summary>采集失败时的原因，成功时为 null。</summary>
        public string Error { get; set; }

        public double CpuPercent { get; set; }
        public int CpuCores { get; set; }

        public double MemTotalBytes { get; set; }
        public double MemUsedBytes { get; set; }

        public double SwapTotalBytes { get; set; }
        public double SwapUsedBytes { get; set; }

        public List<DiskUsage> Disks { get; set; }

        public string Hostname { get; set; }
        public string Kernel { get; set; }
        public string CpuModel { get; set; }
        public double LoadAvg1 { get; set; }
        public TimeSpan Uptime { get; set; }

        /// <summary>本次采集耗时（毫秒），用于界面展示响应速度。</summary>
        public double ElapsedMs { get; set; }

        public ServerSnapshot()
        {
            TimeUtc = DateTime.UtcNow;
            Disks = new List<DiskUsage>();
        }

        public double MemPercent
        {
            get { return MemTotalBytes > 0 ? MemUsedBytes / MemTotalBytes * 100.0 : 0.0; }
        }

        /// <summary>所有挂载点中用量最高的一个，作为整机磁盘水位。</summary>
        public double DiskPercent
        {
            get
            {
                if (Disks == null || Disks.Count == 0) return 0.0;
                return Disks.Max(d => d.UsedPercent);
            }
        }

        public DiskUsage WorstDisk
        {
            get
            {
                if (Disks == null || Disks.Count == 0) return null;
                return Disks.OrderByDescending(d => d.UsedPercent).First();
            }
        }

        public double DiskTotalBytes
        {
            get { return Disks == null ? 0 : Disks.Sum(d => d.TotalBytes); }
        }

        public double DiskUsedBytes
        {
            get { return Disks == null ? 0 : Disks.Sum(d => d.UsedBytes); }
        }

        public static ServerSnapshot Offline(string serverId, string error, double elapsedMs)
        {
            return new ServerSnapshot
            {
                ServerId = serverId,
                Online = false,
                Error = error,
                ElapsedMs = elapsedMs
            };
        }
    }
}
