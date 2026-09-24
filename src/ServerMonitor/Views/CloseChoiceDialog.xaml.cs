using System.Windows;

namespace ServerMonitor.Views
{
    /// <summary>
    /// 点主窗口关闭按钮时的三选一：取消 / 后台运行 / 关闭。
    ///
    /// 结果刻意不用自定义枚举承载：自定义类型出现在 public 类的 public 成员上
    /// 会触发 CS0050（可访问性不一致），而这个对话框与 MessageDialog 一样是 public 的。
    /// 两个 bool 加 DialogResult 足够表达三选一：
    ///     DialogResult != true  → 取消（窗口留着）
    ///     RunInBackground       → 后台运行（隐藏到通知区域）
    ///     否则                  → 关闭（真的退出）
    /// </summary>
    public partial class CloseChoiceDialog : Window
    {
        private CloseChoiceDialog()
        {
            InitializeComponent();
        }

        /// <summary>用户选了「后台运行」。</summary>
        public bool RunInBackground { get; private set; }

        /// <summary>用户勾了「记住我的选择」。</summary>
        public bool Remember { get; private set; }

        /// <summary>
        /// 弹出询问框并返回结果实例（不会返回 null）。
        /// DialogResult != true 即用户取消——此时 RunInBackground 无意义。
        /// </summary>
        public static CloseChoiceDialog Ask(Window owner)
        {
            var dialog = new CloseChoiceDialog();
            if (owner != null && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            // ShowDialog 内部跑的是嵌套消息循环，不会让正在关闭的主窗口重入关闭，
            // 也不会触发它自己的二次 Closing。返回后窗口已关闭，但实例上的
            // 两个 bool 与 DialogResult 照样读得到。
            dialog.ShowDialog();
            return dialog;
        }

        /// <summary>取消按钮与 Esc（IsCancel）都走这里。</summary>
        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnBackground(object sender, RoutedEventArgs e)
        {
            RunInBackground = true;
            Remember = RememberBox.IsChecked == true;
            DialogResult = true;
        }

        private void OnExit(object sender, RoutedEventArgs e)
        {
            RunInBackground = false;
            Remember = RememberBox.IsChecked == true;
            DialogResult = true;
        }
    }
}
