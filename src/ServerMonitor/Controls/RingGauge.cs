using System;
using System.Windows;
using System.Windows.Media;
using ServerMonitor.Models;

namespace ServerMonitor.Controls
{
    /// <summary>
    /// 环形仪表：一圈轨道 + 一段按百分比绘制的圆弧，圆心显示数值。
    /// 弧形两端的圆头是刻意的——数据末端要圆润收口，不能是硬切边。
    /// </summary>
    public class RingGauge : GaugeBase
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double), typeof(RingGauge),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register("Level", typeof(HealthLevel), typeof(RingGauge),
                new FrameworkPropertyMetadata(HealthLevel.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty =
            DependencyProperty.Register("Thickness", typeof(double), typeof(RingGauge),
                new FrameworkPropertyMetadata(7.5, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ValueFontSizeProperty =
            DependencyProperty.Register("ValueFontSize", typeof(double), typeof(RingGauge),
                new FrameworkPropertyMetadata(25.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty UnitFontSizeProperty =
                DependencyProperty.Register("UnitFontSize", typeof(double), typeof(RingGauge),
                    new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>0–100 的百分比。通过 DoubleAnimation 赋值即可获得平滑过渡。</summary>
        public double Value
        {
            get { return (double)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public HealthLevel Level
        {
            get { return (HealthLevel)GetValue(LevelProperty); }
            set { SetValue(LevelProperty, value); }
        }

        public double Thickness
        {
            get { return (double)GetValue(ThicknessProperty); }
            set { SetValue(ThicknessProperty, value); }
        }

        public double ValueFontSize
        {
            get { return (double)GetValue(ValueFontSizeProperty); }
            set { SetValue(ValueFontSizeProperty, value); }
        }

        public double UnitFontSize
        {
            get { return (double)GetValue(UnitFontSizeProperty); }
            set { SetValue(UnitFontSizeProperty, value); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 || height <= 2) return;

            double thickness = Thickness;
            double radius = Math.Min(width, height) / 2.0 - thickness / 2.0;
            if (radius <= 1) return;

            var center = new Point(width / 2.0, height / 2.0);
            bool offline = Level == HealthLevel.Offline;

            var trackPen = new Pen(ResolveBrush("RingTrack", Color.FromRgb(0x23, 0x2B, 0x36)), thickness);
            dc.DrawEllipse(null, trackPen, center, radius, radius);

            double value = ClampPercent(Value);
            if (!offline && value > 0.4)
            {
                var fillPen = new Pen(LevelBrush(Level), thickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                dc.DrawGeometry(null, fillPen, BuildArc(center, radius, 360.0 * value / 100.0));
            }

            DrawCenterText(dc, center, offline);
        }

        private void DrawCenterText(DrawingContext dc, Point center, bool offline)
        {
            Brush primary = ResolveBrush("TextPrimary", Color.FromRgb(0xF2, 0xF5, 0xF8));
            Brush muted = ResolveBrush("TextMuted", Color.FromRgb(0x7C, 0x88, 0x96));

            string valueText = offline ? "--" : Math.Round(ClampPercent(Value)).ToString("0");

            FormattedText value = CreateText(valueText, ValueFontSize, primary, FontWeights.SemiBold);
            FormattedText unit = offline ? null : CreateText("%", UnitFontSize, muted, FontWeights.Normal);

            double unitWidth = unit == null ? 0 : unit.Width + 1.5;
            double totalWidth = value.Width + unitWidth;
            double left = center.X - totalWidth / 2.0;
            double top = center.Y - value.Height / 2.0;

            dc.DrawText(value, new Point(left, top));

            if (unit != null)
            {
                // 把百分号贴到数字基线右下角，视觉上不抢主体
                double unitTop = top + value.Height - unit.Height - value.Height * 0.10;
                dc.DrawText(unit, new Point(left + value.Width + 1.5, unitTop));
            }
        }

        /// <summary>从正上方出发顺时针画一段圆弧。</summary>
        private static Geometry BuildArc(Point center, double radius, double sweepDegrees)
        {
            // 整圆时首尾重合会让弧段退化，留一点余量
            if (sweepDegrees >= 359.9) sweepDegrees = 359.9;
            if (sweepDegrees <= 0) return Geometry.Empty;

            const double startAngle = -90.0;
            Point start = PointOnCircle(center, radius, startAngle);
            Point end = PointOnCircle(center, radius, startAngle + sweepDegrees);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(radius, radius), 0.0,
                          sweepDegrees > 180.0, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Point PointOnCircle(Point center, double radius, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            return new Point(
                center.X + radius * Math.Cos(radians),
                center.Y + radius * Math.Sin(radians));
        }
    }
}
