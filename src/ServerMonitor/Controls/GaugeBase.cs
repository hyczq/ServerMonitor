using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ServerMonitor.Models;

namespace ServerMonitor.Controls
{
    /// <summary>
    /// 自绘控件的公共基类：主题色解析、DPI 缩放、状态色映射。
    /// 主题切换时由 ThemeManager 统一调用 InvalidateVisual，这里每次绘制都重新取色，
    /// 因此不需要缓存画刷。
    /// </summary>
    public abstract class GaugeBase : FrameworkElement
    {
        protected Brush ResolveBrush(string resourceKey, Color fallback)
        {
            object value = TryFindResource(resourceKey);
            Brush brush = value as Brush;
            if (brush != null) return brush;

            if (Application.Current != null)
            {
                brush = Application.Current.TryFindResource(resourceKey) as Brush;
                if (brush != null) return brush;
            }

            var solid = new SolidColorBrush(fallback);
            solid.Freeze();
            return solid;
        }

        /// <summary>把健康等级映射到状态色。状态色永远搭配文字标签出现，不单独表意。</summary>
        protected Brush LevelBrush(HealthLevel level)
        {
            switch (level)
            {
                case HealthLevel.Good: return ResolveBrush("StatusGood", Color.FromRgb(0x0C, 0xA3, 0x0C));
                case HealthLevel.Warning: return ResolveBrush("StatusWarning", Color.FromRgb(0xFA, 0xB2, 0x19));
                case HealthLevel.Critical: return ResolveBrush("StatusCritical", Color.FromRgb(0xD0, 0x3B, 0x3B));
                case HealthLevel.Offline: return ResolveBrush("StatusOffline", Color.FromRgb(0x5A, 0x66, 0x72));
                default: return ResolveBrush("TextMuted", Color.FromRgb(0x7C, 0x88, 0x96));
            }
        }

        protected FormattedText CreateText(string text, double fontSize, Brush brush,
                                           FontWeight weight)
        {
            var typeface = new Typeface(
                new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"),
                FontStyles.Normal, weight, FontStretches.Normal);

            // 注意：带 pixelsPerDip 的重载要 .NET 4.6.2 才有，
            // 本项目锁定 net45，只能用这个六参数版本。
            // 因为清单里声明了系统级 DPI 感知，整体坐标系已是 DIP，显示效果一致。
            return new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush);
        }

        protected static double ClampPercent(double value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}
