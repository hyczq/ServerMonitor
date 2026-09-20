using System;
using System.Windows;
using System.Windows.Media;
using ServerMonitor.Models;

namespace ServerMonitor.Controls
{
    /// <summary>
    /// 水平条形计：细轨道 + 圆头填充，用于磁盘列表这类需要紧凑排布的位置。
    /// </summary>
    public class MeterBar : GaugeBase
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register("Value", typeof(double), typeof(MeterBar),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LevelProperty =
            DependencyProperty.Register("Level", typeof(HealthLevel), typeof(MeterBar),
                new FrameworkPropertyMetadata(HealthLevel.Unknown, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BarHeightProperty =
            DependencyProperty.Register("BarHeight", typeof(double), typeof(MeterBar),
                new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

        public double BarHeight
        {
            get { return (double)GetValue(BarHeightProperty); }
            set { SetValue(BarHeightProperty, value); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 || height <= 1) return;

            double barHeight = Math.Min(BarHeight, height);
            double radius = barHeight / 2.0;
            double top = (height - barHeight) / 2.0;

            var track = new Rect(0, top, width, barHeight);
            dc.DrawRoundedRectangle(
                ResolveBrush("RingTrack", Color.FromRgb(0x23, 0x2B, 0x36)),
                null, track, radius, radius);

            if (Level == HealthLevel.Offline) return;

            double value = ClampPercent(Value);
            if (value <= 0.4) return;

            // 圆弧按比例映射到像素宽度；最小宽度保证极低占比也仍然可见
            double fillWidth = width * value / 100.0;
            if (fillWidth < barHeight) fillWidth = barHeight;

            var fill = new Rect(0, top, fillWidth, barHeight);
            dc.DrawRoundedRectangle(LevelBrush(Level), null, fill, radius, radius);
        }
    }
}
