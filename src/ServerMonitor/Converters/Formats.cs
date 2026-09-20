using System;
using System.Globalization;
using ServerMonitor.Models;

namespace ServerMonitor.Converters
{
    /// <summary>界面与导出共用的格式化工具。</summary>
    public static class Formats
    {
        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>字节数转可读文本，按 1024 进制（与 df -h 的口径一致）。</summary>
        public static string Bytes(double bytes)
        {
            if (bytes <= 0) return "0 B";
            if (double.IsNaN(bytes) || double.IsInfinity(bytes)) return "--";

            int unit = 0;
            double value = bytes;
            while (value >= 1024.0 && unit < ByteUnits.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }

            string format = value >= 100 ? "0" : (value >= 10 ? "0.0" : "0.00");
            return value.ToString(format, CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
        }

        public static string Percent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "--";
            return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        public static string PercentShort(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "--";
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static string Uptime(TimeSpan span)
        {
            if (span <= TimeSpan.Zero) return "--";
            if (span.TotalDays >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} 天 {1} 小时",
                    (int)span.TotalDays, span.Hours);
            }
            if (span.TotalHours >= 1)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} 小时 {1} 分",
                    (int)span.TotalHours, span.Minutes);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0} 分", Math.Max(1, span.Minutes));
        }

        /// <summary>健康等级的中文标签。状态色永远和这个文字同时出现。</summary>
        public static string LevelText(HealthLevel level)
        {
            switch (level)
            {
                case HealthLevel.Good: return "正常";
                case HealthLevel.Warning: return "偏高";
                case HealthLevel.Critical: return "告警";
                case HealthLevel.Offline: return "离线";
                default: return "未知";
            }
        }

        /// <summary>按阈值把百分比归类到健康等级。</summary>
        public static HealthLevel Classify(double percent, AppSettings settings)
        {
            if (settings == null) return HealthLevel.Unknown;
            if (percent >= settings.CriticalThreshold) return HealthLevel.Critical;
            if (percent >= settings.WarnThreshold) return HealthLevel.Warning;
            return HealthLevel.Good;
        }

        /// <summary>整体健康度取三项指标中最差的一个。</summary>
        public static HealthLevel Worst(HealthLevel a, HealthLevel b, HealthLevel c)
        {
            return (HealthLevel)Math.Max((int)a, Math.Max((int)b, (int)c));
        }
    }
}
