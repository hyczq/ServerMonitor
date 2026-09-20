using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;

namespace ServerMonitor.Controls
{
    /// <summary>
    /// 迷你趋势线：只画折线和一层很淡的面积渐变，不放坐标轴。
    ///
    /// 纵轴固定 0–100（而不是按数据自适应），这样不同服务器的走势线高度可以横向比较，
    /// 也不会把 2% 的抖动放大成剧烈波动。
    /// </summary>
    public class Sparkline : GaugeBase
    {
        public static readonly DependencyProperty ValuesProperty =
            DependencyProperty.Register("Values", typeof(IEnumerable<double>), typeof(Sparkline),
                new FrameworkPropertyMetadata(null,
                    FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

        public static readonly DependencyProperty SeriesKeyProperty =
            DependencyProperty.Register("SeriesKey", typeof(string), typeof(Sparkline),
                new FrameworkPropertyMetadata("Series1", FrameworkPropertyMetadataOptions.AffectsRender));

        private INotifyCollectionChanged _observed;

        public IEnumerable<double> Values
        {
            get { return (IEnumerable<double>)GetValue(ValuesProperty); }
            set { SetValue(ValuesProperty, value); }
        }

        /// <summary>主题中序列色资源名，默认 Series1。</summary>
        public string SeriesKey
        {
            get { return (string)GetValue(SeriesKeyProperty); }
            set { SetValue(SeriesKeyProperty, value); }
        }

        private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sparkline = (Sparkline)d;

            if (sparkline._observed != null)
            {
                sparkline._observed.CollectionChanged -= sparkline.OnCollectionChanged;
                sparkline._observed = null;
            }

            var observable = e.NewValue as INotifyCollectionChanged;
            if (observable != null)
            {
                observable.CollectionChanged += sparkline.OnCollectionChanged;
                sparkline._observed = observable;
            }

            sparkline.InvalidateVisual();
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 || height <= 2) return;

            List<double> values = Snapshot();
            if (values.Count < 2)
            {
                DrawPlaceholder(dc, width, height, values.Count == 1);
                return;
            }

            var points = new List<Point>(values.Count);
            double step = width / (values.Count - 1);
            for (int i = 0; i < values.Count; i++)
            {
                double value = values[i];
                if (value < 0) value = 0;
                if (value > 100) value = 100;

                // 上下各留 1.5px，避免 0% 和 100% 的线被裁掉一半
                const double padding = 1.5;
                double usable = Math.Max(1, height - padding * 2);
                double y = padding + usable * (1.0 - value / 100.0);
                points.Add(new Point(i * step, y));
            }

            Brush stroke = ResolveBrush(SeriesKey, Color.FromRgb(0x39, 0x87, 0xE5));

            // 面积：折线下方到画布底边，用同色渐变收尾
            var area = new StreamGeometry();
            using (StreamGeometryContext ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, height), true, true);
                ctx.LineTo(points[0], true, false);
                ctx.PolyLineTo(points.GetRange(1, points.Count - 1), true, false);
                ctx.LineTo(new Point(points[points.Count - 1].X, height), true, false);
            }
            area.Freeze();

            Color baseColor = (stroke as SolidColorBrush) != null
                ? ((SolidColorBrush)stroke).Color
                : Color.FromRgb(0x39, 0x87, 0xE5);

            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0x38, baseColor.R, baseColor.G, baseColor.B), 0),
                    new GradientStop(Color.FromArgb(0x00, baseColor.R, baseColor.G, baseColor.B), 1)
                }
            };
            gradient.Freeze();

            dc.DrawGeometry(gradient, null, area);

            var line = new StreamGeometry();
            using (StreamGeometryContext ctx = line.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                ctx.PolyLineTo(points.GetRange(1, points.Count - 1), true, false);
            }
            line.Freeze();

            var pen = new Pen(stroke, 2.0)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            dc.DrawGeometry(null, pen, line);

            // 最新一点用实心圆收尾，明确"数据到这里为止"
            var last = points[points.Count - 1];
            dc.DrawEllipse(stroke, null, last, 2.6, 2.6);
        }

        private void DrawPlaceholder(DrawingContext dc, double width, double height, bool singlePoint)
        {
            Brush muted = ResolveBrush("StrokeStrong", Color.FromRgb(0x2E, 0x38, 0x44));
            var pen = new Pen(muted, 1.5) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };

            double y = height / 2.0;
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));

            if (singlePoint)
            {
                dc.DrawEllipse(muted, null, new Point(width / 2.0, y), 2.6, 2.6);
            }
        }

        /// <summary>把绑定集合拷成快照，避免绘制过程中集合被后台线程改写。</summary>
        private List<double> Snapshot()
        {
            var result = new List<double>();
            IEnumerable<double> source = Values;
            if (source == null) return result;

            try
            {
                foreach (double value in source) result.Add(value);
            }
            catch (InvalidOperationException)
            {
                // 集合正在被另一线程修改：本次绘制用已取到的部分即可
            }
            return result;
        }
    }
}
