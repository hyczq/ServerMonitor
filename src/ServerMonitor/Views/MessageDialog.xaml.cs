using System.Windows;

namespace ServerMonitor.Views
{
    /// <summary>与主界面同主题的轻量提示 / 确认框。</summary>
    public partial class MessageDialog : Window
    {
        private MessageDialog()
        {
            InitializeComponent();
        }

        private static MessageDialog Build(Window owner, string title, string message, bool confirm)
        {
            var dialog = new MessageDialog();
            if (owner != null && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            dialog.TitleText.Text = string.IsNullOrEmpty(title) ? "提示" : title;
            dialog.BodyText.Text = message ?? string.Empty;

            if (confirm)
            {
                dialog.OkButton.Content = "确定";
                dialog.CancelButton.Visibility = Visibility.Visible;
            }

            return dialog;
        }

        public static void Show(Window owner, string title, string message)
        {
            Build(owner, title, message, false).ShowDialog();
        }

        public static bool Confirm(Window owner, string title, string message)
        {
            return Build(owner, title, message, true).ShowDialog() == true;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
