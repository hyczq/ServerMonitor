using System;

namespace ServerMonitor.Models
{
    /// <summary>一次落盘的采样点，用于绘制全天走势。</summary>
    public class MetricSample
    {
        /// <summary>采集时刻，格式 HH:mm。</summary>
        public string T { get; set; }

        public double Cpu { get; set; }
        public double Mem { get; set; }
        public double Disk { get; set; }

        public MetricSample() { }

        public MetricSample(DateTime localTime, double cpu, double mem, double disk)
        {
            T = localTime.ToString("HH:mm");
            Cpu = Round(cpu);
            Mem = Round(mem);
            Disk = Round(disk);
        }

        private static double Round(double value)
        {
            return Math.Round(value, 1, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>按天聚合的统计结果，用于"每日用量统计"页面。</summary>
    public class DailyAggregate
    {
        public string Date { get; set; }

        /// <summary>参与统计的采样次数。</summary>
        public int Samples { get; set; }

        /// <summary>其中采集失败的次数，用于判断当天数据是否可信。</summary>
        public int OfflineSamples { get; set; }

        public double CpuSum { get; set; }
        public double CpuMax { get; set; }
        public double CpuMin { get; set; }

        public double MemSum { get; set; }
        public double MemMax { get; set; }

        public double DiskSum { get; set; }
        public double DiskMax { get; set; }

        /// <summary>成功采到数据的次数。均值和峰值都只应该用这个数做分母。</summary>
        public int OnlineSamples { get { return Samples - OfflineSamples; } }

        public double CpuAvg { get { return OnlineSamples > 0 ? CpuSum / OnlineSamples : 0; } }
        public double MemAvg { get { return OnlineSamples > 0 ? MemSum / OnlineSamples : 0; } }
        public double DiskAvg { get { return OnlineSamples > 0 ? DiskSum / OnlineSamples : 0; } }

        public double OnlineRate
        {
            get { return Samples > 0 ? (Samples - OfflineSamples) * 100.0 / Samples : 0; }
        }

        public void Add(ServerSnapshot snapshot)
        {
            if (Samples == 0) CpuMin = snapshot.CpuPercent;

            Samples++;
            if (!snapshot.Online)
            {
                OfflineSamples++;
                return;
            }

            CpuSum += snapshot.CpuPercent;
            if (snapshot.CpuPercent > CpuMax) CpuMax = snapshot.CpuPercent;
            if (snapshot.CpuPercent < CpuMin) CpuMin = snapshot.CpuPercent;

            MemSum += snapshot.MemPercent;
            if (snapshot.MemPercent > MemMax) MemMax = snapshot.MemPercent;

            DiskSum += snapshot.DiskPercent;
            if (snapshot.DiskPercent > DiskMax) DiskMax = snapshot.DiskPercent;
        }
    }
}
