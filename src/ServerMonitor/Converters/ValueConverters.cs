using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ServerMonitor.Models;

// 日志级别来自 Models，和健康等级放在同一个命名空间下

namespace ServerMonitor.Converters
{
    /// <summary>健康等级 -> 状态色画刷。状态色始终与文字标签同时出现，不单独承载语义。</summary>
    public class HealthToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            HealthLevel level = value is HealthLevel ? (HealthLevel)value : HealthLevel.Unknown;
            string key;
            switch (level)
            {
                case HealthLevel.Good: key = "StatusGood"; break;
                case HealthLevel.Warning: key = "StatusWarning"; break;
                case HealthLevel.Critical: key = "StatusCritical"; break;
                case HealthLevel.Offline: key = "StatusOffline"; break;
                default: key = "TextMuted"; break;
            }

            object brush = Application.Current != null
                ? Application.Current.TryFindResource(key) : null;
            return brush as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>健康等级 -> 中文标签。</summary>
    public class HealthToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            HealthLevel level = value is HealthLevel ? (HealthLevel)value : HealthLevel.Unknown;
            return Formats.LevelText(level);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>健康等级 -> 图标几何。用"图标 + 文字"双重编码，色盲用户也能分辨。</summary>
    public class HealthToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            HealthLevel level = value is HealthLevel ? (HealthLevel)value : HealthLevel.Unknown;
            string key;
            switch (level)
            {
                case HealthLevel.Good: key = "IconCheck"; break;
                case HealthLevel.Warning: key = "IconWarning"; break;
                case HealthLevel.Critical: key = "IconWarning"; break;
                case HealthLevel.Offline: key = "IconClose"; break;
                default: key = "IconInfo"; break;
            }

            object geometry = Application.Current != null
                ? Application.Current.TryFindResource(key) : null;
            return geometry ?? DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>字节数 -> 人类可读文本。</summary>
    public class BytesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double bytes;
            if (value == null || !double.TryParse(value.ToString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out bytes))
            {
                return "--";
            }
            return Formats.Bytes(bytes);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>百分比数值 -> 文本，可选参数 "short" 表示取整。</summary>
    public class PercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent;
            if (value == null || !double.TryParse(value.ToString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
            {
                return "--";
            }

            string mode = parameter as string;
            if (string.Equals(mode, "short", StringComparison.OrdinalIgnoreCase))
            {
                return Formats.PercentShort(percent);
            }
            return Formats.Percent(percent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>布尔取反。</summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !(value is bool && (bool)value);
        }
    }

    /// <summary>布尔取反后转可见性：true -> Collapsed。</summary>
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool flag = value is bool && (bool)value;
            return flag ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>字符串非空 -> Visible；传参数 "inverse" 表示"为空时才显示"（用于占位提示）。</summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isEmpty = string.IsNullOrWhiteSpace(value as string);
            bool inverse = string.Equals(parameter as string, "inverse",
                StringComparison.OrdinalIgnoreCase);

            bool visible = inverse ? isEmpty : !isEmpty;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 空集合 / 零计数 -> Visible（用于"暂无数据"占位）。
    /// 同时接受集合本身和绑定的 .Count 数值 —— 两者都要支持，
    /// 否则绑定 .Count 时会因为不是 ICollection 而永远判定为空。
    /// </summary>
    public class EmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool empty;

            if (value == null)
            {
                empty = true;
            }
            else if (value is int || value is long || value is double)
            {
                empty = System.Convert.ToDouble(value, CultureInfo.InvariantCulture) <= 0;
            }
            else
            {
                var collection = value as System.Collections.ICollection;
                empty = collection == null || collection.Count == 0;
            }

            bool inverse = string.Equals(parameter as string, "inverse",
                StringComparison.OrdinalIgnoreCase);
            if (inverse) empty = !empty;

            return empty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// 日志级别 -> 文字颜色。
    /// 级别文字本身就是标签（"ERROR"/"WARN"），颜色只是辅助扫读的冗余通道，
    /// 不靠颜色单独表意。
    /// </summary>
    public class LogLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            LogLevel level = value is LogLevel ? (LogLevel)value : LogLevel.Info;
            string key;
            switch (level)
            {
                case LogLevel.Error: key = "StatusCritical"; break;
                case LogLevel.Warn: key = "StatusWarning"; break;
                case LogLevel.Info: key = "TextPrimary"; break;
                case LogLevel.Debug: key = "TextSecondary"; break;
                default: key = "TextMuted"; break;
            }

            object brush = Application.Current != null
                ? Application.Current.TryFindResource(key) : null;
            return brush as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>数值 -> 星号列宽，用于按比例切分阈值示意图。</summary>
    public class StarWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width;
            if (value == null || !double.TryParse(value.ToString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out width) || width <= 0)
            {
                return new GridLength(0.5, GridUnitType.Star);
            }
            return new GridLength(width, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>启用状态 -> 按钮文字（按钮文案表示"点下去会变成什么"）。</summary>
    public class EnabledLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool enabled = value is bool && (bool)value;
            return enabled ? "停用" : "启用";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>状态色资源名 -> 画刷（用于图例色块）。</summary>
    public class ResourceKeyToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string key = value as string;
            if (string.IsNullOrEmpty(key)) return Brushes.Gray;

            object brush = Application.Current != null
                ? Application.Current.TryFindResource(key) : null;
            return brush as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
