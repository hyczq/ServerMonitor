using System;
using System.Windows;
using System.Windows.Media;

namespace ServerMonitor.Themes
{
    /// <summary>
    /// 主题切换。两套 token 字典键名完全一致，切换时整表替换。
    /// 自绘控件（环形仪表/趋势线/条形计）的着色发生在 OnRender 里，
    /// WPF 不会因为资源字典变化自动重画，所以这里统一遍历视觉树强制重绘。
    /// </summary>
    public static class ThemeManager
    {
        private const string DarkSource = "pack://application:,,,/Themes/Tokens.Dark.xaml";
        private const string LightSource = "pack://application:,,,/Themes/Tokens.Light.xaml";

        public static bool IsDark { get; private set; }

        public static void Apply(bool dark)
        {
            Application app = Application.Current;
            if (app == null) return;

            // pack:// 是绝对 URI，必须用 UriKind.Absolute
            var replacement = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute)
            };

            bool replaced = false;
            var dictionaries = app.Resources.MergedDictionaries;
            for (int i = 0; i < dictionaries.Count; i++)
            {
                Uri source = dictionaries[i].Source;
                if (source != null &&
                    source.OriginalString.IndexOf("Tokens.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dictionaries[i] = replacement;
                    replaced = true;
                    break;
                }
            }

            if (!replaced) dictionaries.Insert(0, replacement);

            IsDark = dark;
            InvalidateVisualTree();
        }

        private static void InvalidateVisualTree()
        {
            Application app = Application.Current;
            if (app == null) return;

            foreach (Window window in app.Windows)
            {
                InvalidateNode(window);
            }
        }

        private static void InvalidateNode(DependencyObject node)
        {
            if (node == null) return;

            var element = node as FrameworkElement;
            if (element != null) element.InvalidateVisual();

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                InvalidateNode(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
