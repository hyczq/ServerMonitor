using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ServerMonitor.Controls
{
    /// <summary>趋势图上的一条线。</summary>
    public class TrendSeries
    {
        public string Name { get; set; }

        /// <summary>主题中的序列色资源名（Series1/2/3）。</summary>
        public string SeriesKey { get; set; }

        public List<double> Values { get; set; }

        public TrendSeries()
        {
            Values = new List<double>();
            SeriesKey = "Series1";
        }
    }

    /// <summary>
    /// 全天走势折线图：网格 + 轴标签 + 多条序列 + 十字准线与悬浮提示。
    /// 纵轴固定 0–100%，保证不同服务器、不同日期的曲线可直接比较。
    /// </summary>
    public class TrendChart : GaugeBase
    {
        private const double PadLeft = 40;
        private const double PadRight = 46;   // 右侧留给线尾直接标注
        private const double PadTop = 12;
        private const double PadBottom = 24;

        public static readonly DependencyProperty SeriesProperty =
            DependencyProperty.Register("Series", typeof(IEnumerable<TrendSeries>), typeof(TrendChart),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

        public static readonly DependencyProperty LabelsProperty =
            DependencyProperty.Register("Labels", typeof(IList<string>), typeof(TrendChart),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private int _hoverIndex = -1;
        private INotifyCollectionChanged _observed;

        public IEnumerable<TrendSeries> Series
        {
            get { return (IEnumerable<TrendSeries>)GetValue(SeriesProperty); }
            set { SetValue(SeriesProperty, value); }
        }

        public IList<string> Labels
        {
            get { return (IList<string>)GetValue(LabelsProperty); }
            set { SetValue(LabelsProperty, value); }
        }

        private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var chart = (TrendChart)d;
            if (chart._observed != null)
            {
                chart._observed.CollectionChanged -= chart.OnCollectionChanged;
                chart._observed = null;
            }

            var observable = e.NewValue as INotifyCollectionChanged;
            if (observable != null)
            {
                observable.CollectionChanged += chart.OnCollectionChanged;
                chart._observed = observable;
            }

            chart.InvalidateVisual();
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            Point position = e.GetPosition(this);
            double plotWidth = ActualWidth - PadLeft - PadRight;
            if (plotWidth <= 1) return;

            List<TrendSeries> series = SnapshotSeries();
            int pointCount = MaxPoints(series);
            if (pointCount < 2) return;

            double ratio = (position.X - PadLeft) / plotWidth;
            int index = (int)Math.Round(ratio * (pointCount - 1));
            if (index < 0) index = 0;
            if (index > pointCount - 1) index = pointCount - 1;

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 8 || height <= 8) return;

            // 透明填充让整块区域可命中鼠标（空填充不参与命中测试）
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            double plotWidth = width - PadLeft - PadRight;
            double plotHeight = height - PadTop - PadBottom;
            if (plotWidth <= 1 || plotHeight <= 1) return;

            Brush muted = ResolveBrush("TextMuted", Color.FromRgb(0x7C, 0x88, 0x96));
            var gridPen = new Pen(ResolveBrush("GridLine", Color.FromRgb(0x20, 0x28, 0x32)), 1);
            var baselinePen = new Pen(ResolveBrush("Baseline", Color.FromRgb(0x2E, 0x38, 0x44)), 1);

            // ---- 网格与纵轴刻度 ----
            double[] ticks = { 0, 25, 50, 75, 100 };
            foreach (double tick in ticks)
            {
                double y = PadTop + plotHeight * (1.0 - tick / 100.0);
                dc.DrawLine(tick <= 0.01 ? baselinePen : gridPen,
                    new Point(PadLeft, y), new Point(PadLeft + plotWidth, y));

                FormattedText label = CreateText(tick.ToString("0") + "%", 10.5, muted, FontWeights.Normal);
                dc.DrawText(label, new Point(PadLeft - label.Width - 6, y - label.Height / 2));
            }

            List<TrendSeries> series = SnapshotSeries();
            int pointCount = MaxPoints(series);
            if (pointCount < 2 || series.Count == 0)
            {
                // 只有一个点时如实说明还差多少，比笼统的"暂无数据"更有指导性
                string message = pointCount == 1
                    ? "已采集到 1 个点，至少需要 2 个点才能连成走势"
                    : "当日暂无采样数据";

                FormattedText empty = CreateText(message, 12.5, muted, FontWeights.Normal);
                dc.DrawText(empty, new Point(PadLeft + (plotWidth - empty.Width) / 2,
                                             PadTop + (plotHeight - empty.Height) / 2));
                return;
            }

            // ---- 横轴时间标签 ----
            DrawTimeLabels(dc, width, plotWidth, pointCount, muted);

            // ---- 各条序列 ----
            var endLabels = new List<KeyValuePair<double, FormattedText>>();

            foreach (TrendSeries item in series)
            {
                if (item == null || item.Values == null || item.Values.Count < 2) continue;

                Brush brush = ResolveBrush(item.SeriesKey, Color.FromRgb(0x39, 0x87, 0xE5));
                List<Point> points = BuildPoints(item.Values, pointCount, plotWidth, plotHeight);

                var geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    ctx.BeginFigure(points[0], false, false);
                    ctx.PolyLineTo(points.GetRange(1, points.Count - 1), true, false);
                }
                geometry.Freeze();

                var pen = new Pen(brush, 2.0)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                dc.DrawGeometry(null, pen, geometry);

                // 线尾直接标注序列名，避免读者来回对照图例
                Point last = points[points.Count - 1];
                FormattedText endLabel = CreateText(item.Name, 10.5, brush, FontWeights.SemiBold);
                endLabels.Add(new KeyValuePair<double, FormattedText>(last.Y, endLabel));
            }

            DrawEndLabels(dc, endLabels, width, plotHeight);

            // ---- 十字准线 ----
            if (_hoverIndex >= 0 && _hoverIndex < pointCount)
            {
                DrawCrosshair(dc, series, pointCount, plotWidth, plotHeight, width, muted);
            }
        }

        private void DrawTimeLabels(DrawingContext dc, double width, double plotWidth,
                                    int pointCount, Brush muted)
        {
            IList<string> labels = Labels;

            // 最多放 6 个时间刻度，其余抽稀
            int wanted = 6;
            int stride = Math.Max(1, (int)Math.Ceiling(pointCount / (double)wanted));

            for (int i = 0; i < pointCount; i += stride)
            {
                string text = null;
                if (labels != null && i < labels.Count) text = labels[i];
                if (string.IsNullOrEmpty(text)) continue;

                FormattedText label = CreateText(text, 10.5, muted, FontWeights.Normal);
                double x = PadLeft + plotWidth * (i / (double)(pointCount - 1)) - label.Width / 2.0;

                // 首尾两个刻度贴着绘图区边缘，需要夹回画布内
                if (x < 2) x = 2;
                if (x + label.Width > width - 2) x = width - 2 - label.Width;

                dc.DrawText(label, new Point(x, ActualHeight - PadBottom + 5));
            }
        }

        /// <summary>
        /// 线尾标注上下防重叠：按 y 排序后依次推开，保证至少间隔 13px。
        /// </summary>
        private void DrawEndLabels(DrawingContext dc,
            List<KeyValuePair<double, FormattedText>> labels,
            double width, double plotHeight)
        {
            if (labels.Count == 0) return;

            labels.Sort((a, b) => a.Key.CompareTo(b.Key));

            const double minGap = 13.0;
            var adjusted = new double[labels.Count];
            for (int i = 0; i < labels.Count; i++) adjusted[i] = labels[i].Key;

            for (int i = 1; i < adjusted.Length; i++)
            {
                if (adjusted[i] - adjusted[i - 1] < minGap)
                {
                    adjusted[i] = adjusted[i - 1] + minGap;
                }
            }

            double overflow = adjusted.Length > 0 ? adjusted[adjusted.Length - 1] - (PadTop + plotHeight) : 0;
            if (overflow > 0)
            {
                for (int i = 0; i < adjusted.Length; i++) adjusted[i] -= overflow;
            }

            for (int i = 0; i < labels.Count; i++)
            {
                FormattedText text = labels[i].Value;
                double x = width - PadRight + 5;
                double y = adjusted[i] - text.Height / 2;
                dc.DrawText(text, new Point(x, y));
            }
        }

        private void DrawCrosshair(DrawingContext dc, List<TrendSeries> series, int pointCount,
                                   double plotWidth, double plotHeight, double width, Brush muted)
        {
            double x = PadLeft + plotWidth * (_hoverIndex / (double)(pointCount - 1));
            var linePen = new Pen(ResolveBrush("StrokeStrong", Color.FromRgb(0x2E, 0x38, 0x44)), 1)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0)
            };
            dc.DrawLine(linePen, new Point(x, PadTop), new Point(x, PadTop + plotHeight));

            // 每个序列在该点的实心圆点，外加一圈底色描边避免与线混在一起
            var surfacePen = new Pen(ResolveBrush("SurfaceCard", Color.FromRgb(0x16, 0x1C, 0x24)), 2);
            var rows = new List<KeyValuePair<string, string>>();
            var swatches = new List<Brush>();

            foreach (TrendSeries item in series)
            {
                if (item == null || item.Values == null || item.Values.Count < 2) continue;

                int index = ScaledIndex(_hoverIndex, pointCount, item.Values.Count);
                double value = Clamp(item.Values[index]);
                double y = PadTop + plotHeight * (1.0 - value / 100.0);

                Brush brush = ResolveBrush(item.SeriesKey, Color.FromRgb(0x39, 0x87, 0xE5));
                dc.DrawEllipse(brush, surfacePen, new Point(x, y), 4, 4);

                rows.Add(new KeyValuePair<string, string>(item.Name, value.ToString("0.0") + "%"));
                swatches.Add(brush);
            }

            if (rows.Count == 0) return;

            FormattedText caption = null;
            IList<string> labels = Labels;
            if (labels != null && _hoverIndex < labels.Count)
            {
                caption = CreateText(labels[_hoverIndex], 10.5, muted, FontWeights.Normal);
            }

            DrawTooltip(dc, x, rows, swatches, caption, width);
        }

        private void DrawTooltip(DrawingContext dc, double anchorX,
                                 List<KeyValuePair<string, string>> rows, List<Brush> swatches,
                                 FormattedText caption, double width)
        {
            Brush surface = ResolveBrush("SurfaceElevated", Color.FromRgb(0x1F, 0x28, 0x33));
            Brush border = ResolveBrush("StrokeStrong", Color.FromRgb(0x2E, 0x38, 0x44));
            Brush primary = ResolveBrush("TextPrimary", Color.FromRgb(0xF2, 0xF5, 0xF8));

            var nameTexts = new List<FormattedText>();
            var valueTexts = new List<FormattedText>();

            double contentWidth = caption != null ? caption.Width : 0;
            double lineHeight = 15;

            for (int i = 0; i < rows.Count; i++)
            {
                FormattedText name = CreateText(rows[i].Key, 11, primary, FontWeights.Normal);
                FormattedText value = CreateText(rows[i].Value, 11, primary, FontWeights.SemiBold);
                nameTexts.Add(name);
                valueTexts.Add(value);

                double rowWidth = 10 + 4 + name.Width + 14 + value.Width;
                if (rowWidth > contentWidth) contentWidth = rowWidth;
            }

            double padding = 9;
            double boxWidth = contentWidth + padding * 2;
            double boxHeight = padding * 2 + lineHeight * rows.Count +
                               (caption != null ? lineHeight : 0);

            double boxX = anchorX + 14;
            if (boxX + boxWidth > width - 4) boxX = anchorX - boxWidth - 14;
            if (boxX < 4) boxX = 4;

            double boxY = PadTop + 4;
            var box = new Rect(boxX, boxY, boxWidth, boxHeight);

            dc.DrawRoundedRectangle(surface, new Pen(border, 1), box, 6, 6);

            double cursorY = boxY + padding;
            if (caption != null)
            {
                dc.DrawText(caption, new Point(boxX + padding, cursorY));
                cursorY += lineHeight;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                dc.DrawEllipse(swatches[i], null,
                    new Point(boxX + padding + 4, cursorY + nameTexts[i].Height / 2), 3.5, 3.5);

                dc.DrawText(nameTexts[i], new Point(boxX + padding + 12, cursorY));

                double valueX = boxX + boxWidth - padding - valueTexts[i].Width;
                dc.DrawText(valueTexts[i], new Point(valueX, cursorY));

                cursorY += lineHeight;
            }
        }

        private List<Point> BuildPoints(List<double> values, int pointCount,
                                        double plotWidth, double plotHeight)
        {
            var points = new List<Point>(pointCount);
            for (int i = 0; i < pointCount; i++)
            {
                int sourceIndex = ScaledIndex(i, pointCount, values.Count);
                double value = Clamp(values[sourceIndex]);
                double x = PadLeft + plotWidth * (i / (double)(pointCount - 1));
                double y = PadTop + plotHeight * (1.0 - value / 100.0);
                points.Add(new Point(x, y));
            }
            return points;
        }

        /// <summary>把绘图索引映射回原始采样索引（点数被抽稀时按比例取）。</summary>
        private static int ScaledIndex(int index, int pointCount, int sourceCount)
        {
            if (sourceCount <= 0) return 0;
            if (pointCount <= 1) return 0;

            int result = (int)Math.Round(index * (sourceCount - 1) / (double)(pointCount - 1));
            if (result < 0) result = 0;
            if (result > sourceCount - 1) result = sourceCount - 1;
            return result;
        }

        private static int MaxPoints(List<TrendSeries> series)
        {
            int max = 0;
            foreach (TrendSeries item in series)
            {
                if (item != null && item.Values != null && item.Values.Count > max)
                {
                    max = item.Values.Count;
                }
            }

            // 一天最多 1440 个点，抽稀到 480 以内既够精细也不卡
            const int renderLimit = 480;
            return max > renderLimit ? renderLimit : max;
        }

        private List<TrendSeries> SnapshotSeries()
        {
            var result = new List<TrendSeries>();
            IEnumerable<TrendSeries> source = Series;
            if (source == null) return result;

            try
            {
                foreach (TrendSeries item in source) result.Add(item);
            }
            catch (InvalidOperationException)
            {
            }
            return result;
        }

        private static double Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
