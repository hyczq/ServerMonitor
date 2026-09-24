using System;
using System.Collections.Generic;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 容量趋势的预警级别。
    ///
    /// 刻意独立于 HealthLevel：预测结果不参与卡片的 OverallLevel、
    /// 不进"只看异常"过滤、不影响 ShouldSkip。新增预测不该扰动现有阈值告警的行为。
    /// </summary>
    internal enum DiskForecastLevel
    {
        /// <summary>这次不预测。不等于"健康"，原因见 DiskForecastResult.Reason。</summary>
        None = 0,

        /// <summary>预计 WarnDays 天内写满。</summary>
        Watch = 1,

        /// <summary>预计 CriticalDays 天内写满，且近期窗口确认过。</summary>
        Critical = 2
    }

    /// <summary>拟合用的一个"已完成的天"。当天的数据不在这里，它一天里不断在涨。</summary>
    internal sealed class DiskTrendPoint
    {
        /// <summary>本地日期（当天 00:00）。</summary>
        public DateTime Date { get; set; }

        /// <summary>当天最后一次采集到的已用字节（日末值）。</summary>
        public double UsedBytes { get; set; }

        /// <summary>当天的总容量，用于识别扩容。</summary>
        public double TotalBytes { get; set; }
    }

    internal sealed class DiskForecastResult
    {
        public DiskForecastLevel Level { get; set; }

        /// <summary>拟合出的增速（字节/天）。未能拟合时为 0。</summary>
        public double BytesPerDay { get; set; }

        /// <summary>拟合的判定系数，越接近 1 越像直线。</summary>
        public double R2 { get; set; }

        /// <summary>预计写满天数。不预测时为 double.NaN。</summary>
        public double DaysToFull { get; set; }

        /// <summary>参与判定的已完成天数。用于"数据积累中 N/5 天"。</summary>
        public int DaysUsed { get; set; }

        /// <summary>为什么是现在这个结论。全部进 Debug 日志——这是"凭什么是 12 天"的答案。</summary>
        public string Reason { get; set; }

        /// <summary>卡片与推送共用的同一份文案。不预测时为空串。</summary>
        public string Text { get; set; }

        public DiskForecastResult()
        {
            DaysToFull = double.NaN;
            Reason = string.Empty;
            Text = string.Empty;
        }
    }

    /// <summary>
    /// 从按天的用量序列外推"还有几天写满"。
    ///
    /// 纯函数：不读文件、不取当前时间、不写日志。做成纯的是因为预测要 5 天以上
    /// 数据才有意义，而验证不能等 5 天——只有纯函数才能拿手工构造的点序列
    /// 把每条分支都直接跑一遍。日志由调用方记（见 MainViewModel.BuildForecastMap）。
    ///
    /// 契约：points 必须是"已完成的天"、按日期升序，且在途的今天已被调用方剔除
    /// （去除逻辑在 DiskTrendStore.GetServerSeries 里，只有它知道"今天"是哪天）。
    /// </summary>
    internal static class DiskForecast
    {
        /// <summary>参与拟合所需的最少已完成天数。</summary>
        public const int MinimumDays = 5;

        /// <summary>紧急确认用的近端窗口（天）。</summary>
        private const int ConfirmWindowDays = 14;

        /// <summary>紧急确认用的最近窗口（天）。</summary>
        private const int RecentWindowDays = 7;

        /// <summary>增速下限（1 MB/天）。只作数值兜底，真正的策略闸门是"天数 &lt;= WarnDays"。</summary>
        private const double MinimumBytesPerDay = 1024.0 * 1024.0;

        /// <summary>R² 门槛。刻意放宽：磁盘增长本身是阶梯式的（日志轮转、备份），
        /// 卡太严会漏掉真实的持续增长，靠"两段均值递增"的方向性检查兜噪声。</summary>
        private const double MinimumR2 = 0.3;

        /// <summary>超过这个天数就不预警了——"3 年后写满"对运维没有意义。</summary>
        private const int MaxDaysToFull = 999;

        /// <summary>扩容判定容差。容量相差在 1% 以内视为没扩容（读数本身有抖动）。</summary>
        private const double CapacityTolerance = 0.01;

        /// <summary>
        /// 外推结果。用传入的当前值做基准，而不是用最后一个拟合点——
        /// 拟合只负责给出"每天涨多少"，起点必须是此刻的真实值。
        /// </summary>
        public static DiskForecastResult Evaluate(
            IList<DiskTrendPoint> points, double currentUsedBytes, double currentTotalBytes,
            AppSettings settings)
        {
            var result = new DiskForecastResult();

            if (settings == null || !settings.DiskForecastEnabled)
            {
                result.Reason = "容量预测未开启";
                return result;
            }

            int available = points == null ? 0 : points.Count;
            result.DaysUsed = available;

            if (available < MinimumDays)
            {
                result.Reason = "数据积累中 " + available + "/" + MinimumDays + " 天";
                return result;
            }

            if (currentTotalBytes <= 0 || currentUsedBytes < 0)
            {
                result.Reason = "容量读数不可用（used=" + currentUsedBytes +
                                " total=" + currentTotalBytes + "）";
                return result;
            }

            double free = currentTotalBytes - currentUsedBytes;
            if (free <= 0)
            {
                result.Reason = "分区已写满";
                return result;
            }

            // 已经到告警线的分区交给现有阈值告警。预测只管"还没报警但正在涨"的，
            // 两者不重叠——否则同一个盘会被两套机制同时喊，还会互相盖住文案。
            double percent = currentUsedBytes / currentTotalBytes * 100.0;
            if (percent >= settings.CriticalThreshold)
            {
                result.Reason = "用量已达告警线 " + percent.ToString("0.0") + "%，交由阈值告警";
                return result;
            }

            List<DiskTrendPoint> fitPoints = TrimToCurrentCapacity(points, currentTotalBytes);
            result.DaysUsed = fitPoints.Count;

            if (fitPoints.Count < MinimumDays)
            {
                result.Reason = "扩容后有效数据 " + fitPoints.Count + "/" + MinimumDays + " 天";
                return result;
            }

            Fit fit = FitLine(fitPoints);
            result.BytesPerDay = fit.Slope;
            result.R2 = fit.R2;

            if (double.IsNaN(fit.Slope))
            {
                result.Reason = "日期跨度为 0，无法拟合";
                return result;
            }

            if (fit.Slope < MinimumBytesPerDay)
            {
                result.Reason = "增速低于 1 MB/天，视为不增长（slope=" +
                                fit.Slope.ToString("0") + "）";
                return result;
            }

            if (fit.R2 < MinimumR2)
            {
                result.Reason = "增长不成趋势（R²=" + fit.R2.ToString("0.00") + "）";
                return result;
            }

            if (!Rising(fitPoints))
            {
                result.Reason = "后半段均值未高于前半段，增长可能只是波动";
                return result;
            }

            double days = free / fit.Slope;
            result.DaysToFull = days;

            if (days > MaxDaysToFull)
            {
                result.Reason = "按当前增速要 " + ((int)Math.Ceiling(days)) +
                                " 天才写满，超过 " + MaxDaysToFull + " 天，不预警";
                result.DaysToFull = double.NaN;
                return result;
            }

            // 贴到满但没到告警线：按 1 天报，不要出现"0 天后写满"
            if (days < 1) days = 1;

            bool confirmed = true;
            if (days <= settings.DiskForecastCriticalDays)
            {
                confirmed = ConfirmCritical(fitPoints, free, settings.DiskForecastCriticalDays,
                                            result);
            }

            if (confirmed && days <= settings.DiskForecastCriticalDays)
            {
                result.Level = DiskForecastLevel.Critical;
            }
            else if (days <= settings.DiskForecastWarnDays)
            {
                result.Level = DiskForecastLevel.Watch;
            }
            else
            {
                result.Reason = "预计 " + ((int)Math.Ceiling(days)) + " 天后写满，在预警线（" +
                                settings.DiskForecastWarnDays + " 天）之外";
                result.DaysToFull = double.NaN;
                return result;
            }

            // 向上取整：宁可说 8 天也不说 6 天。否则 days=7.2 会被显示成"约 7 天"
            // 却因为 7.2 > 7 只判到 Watch，文案和级别自相矛盾。
            result.Text = "按当前增速约 " + ((int)Math.Ceiling(days)) + " 天后写满";
            result.DaysToFull = days;
            return result;
        }

        /// <summary>
        /// 紧急必须两个窗口一致：近 14 天单独拟合也要给出 &lt;= CriticalDays，
        /// 并且近 7 天斜率仍为正。误报紧急会半夜把人叫醒，漏报还有阈值告警兜底，
        /// 所以这道门宁可严。
        /// </summary>
        private static bool ConfirmCritical(List<DiskTrendPoint> points, double free,
                                            int criticalDays, DiskForecastResult result)
        {
            double slope14 = FitLine(Tail(points, ConfirmWindowDays)).Slope;
            if (double.IsNaN(slope14) || slope14 < MinimumBytesPerDay)
            {
                result.Reason = "已达紧急天数，但近 " + ConfirmWindowDays + " 天斜率不为正，降为观察";
                return false;
            }

            double days14 = free / slope14;
            if (days14 > criticalDays)
            {
                result.Reason = "整体已达紧急天数，但近 " + ConfirmWindowDays + " 天放缓到 " +
                                ((int)Math.Ceiling(days14)) + " 天，降为观察";
                return false;
            }

            double slope7 = FitLine(Tail(points, RecentWindowDays)).Slope;
            if (double.IsNaN(slope7) || slope7 < MinimumBytesPerDay)
            {
                result.Reason = "已达紧急天数，但近 " + RecentWindowDays + " 天斜率不为正，降为观察";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 扩容检测：盘扩容后旧点的 Used 与新点不可比（新增的容量会被算成用量下降），
        /// 所以从最新往回只保留容量与当前一致的点，遇到第一个不一致的就连同更老的一起丢弃。
        /// 静默丢弃——这既不是错误也不是缺数据，只是这段历史不能再用来外推了。
        /// </summary>
        private static List<DiskTrendPoint> TrimToCurrentCapacity(
            IList<DiskTrendPoint> points, double currentTotalBytes)
        {
            double tolerance = currentTotalBytes * CapacityTolerance;
            var kept = new List<DiskTrendPoint>();

            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(points[i].TotalBytes - currentTotalBytes) > tolerance) break;
                kept.Add(points[i]);
            }

            kept.Reverse();
            return kept;
        }

        /// <summary>取最近 windowDays 天（含最后一天）的点。不足就是全部。</summary>
        private static List<DiskTrendPoint> Tail(IList<DiskTrendPoint> points, int windowDays)
        {
            var tail = new List<DiskTrendPoint>();
            if (points == null || points.Count == 0) return tail;

            DateTime from = points[points.Count - 1].Date.AddDays(-(windowDays - 1));
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].Date >= from) tail.Add(points[i]);
            }
            return tail;
        }

        /// <summary>
        /// 后半段均值是否高于前半段。奇数个点时对称地取两端、丢掉正中间那个，
        /// 否则"前半段比后半段多一个点"会把比较带偏。
        /// </summary>
        private static bool Rising(List<DiskTrendPoint> points)
        {
            int half = points.Count / 2;
            if (half == 0) return false;

            double firstSum = 0;
            double lastSum = 0;
            for (int i = 0; i < half; i++) firstSum += points[i].UsedBytes;
            for (int i = points.Count - half; i < points.Count; i++) lastSum += points[i].UsedBytes;

            return lastSum / half > firstSum / half;
        }

        private sealed class Fit
        {
            public double Slope = double.NaN;
            public double R2 = double.NaN;
        }

        /// <summary>
        /// 最小二乘拟合 (天数, 已用字节)。x 用"自首点起的真实天数"而不是列表下标：
        /// 断采 3 天会把 3 天的增长压进 1 个 x 步长，斜率虚高约 3 倍，
        /// 报出假的"4 天后写满"。点少于 2 个或日期无跨度时 Slope 为 NaN。
        /// </summary>
        private static Fit FitLine(IList<DiskTrendPoint> points)
        {
            var fit = new Fit();
            if (points == null || points.Count < 2) return fit;

            int n = points.Count;
            var xs = new double[n];
            double meanX = 0;
            double meanY = 0;

            DateTime first = points[0].Date;
            for (int i = 0; i < n; i++)
            {
                xs[i] = (points[i].Date - first).TotalDays;
                meanX += xs[i];
                meanY += points[i].UsedBytes;
            }
            meanX /= n;
            meanY /= n;

            double sxx = 0;
            double sxy = 0;
            double syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - meanX;
                double dy = points[i].UsedBytes - meanY;
                sxx += dx * dx;
                sxy += dx * dy;
                syy += dy * dy;
            }

            if (sxx <= 0) return fit;

            fit.Slope = sxy / sxx;

            // 残差平方和 = syy - slope*sxy（最小二乘的恒等式）。
            // syy 为 0 说明所有点一样高，那是"不增长"，由斜率门槛拦。
            fit.R2 = syy > 0 ? 1.0 - (syy - fit.Slope * sxy) / syy : 0.0;

            return fit;
        }
    }
}
