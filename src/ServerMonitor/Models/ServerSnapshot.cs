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

        /// <summary>
        /// 普通用户实际还能写入的字节数：Linux 取 df 的 Avail 列，Windows 取 FreeSpace。
        ///
        /// 它不等于 TotalBytes - UsedBytes：差额是文件系统给 root 预留的块
        /// （ext4 默认 5%）。所以"还剩多少"必须看它，不能拿容量相减。
        /// </summary>
        public double AvailableBytes { get; set; }

        /// <summary>
        /// 与 df 的 Use% 同口径：Used / (Used + Available)。
        ///
        /// 分母刻意不含预留块。若按 Used / Total 算，df 已经 77% 的盘
        /// 这里只会显示 73%，两边的告警线就对不上了。
        /// </summary>
        public double UsedPercent
        {
            get
            {
                double usable = UsedBytes + AvailableBytes;
                return usable > 0 ? UsedBytes / usable * 100.0 : 0.0;
            }
        }

        /// <summary>剩余可用空间（不是 Total - Used，差额是预留块）。</summary>
        public double FreeBytes
        {
            get { return Math.Max(0, AvailableBytes); }
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
