using System.Windows;
using System.Windows.Controls;
using ServerMonitor.ViewModels;

namespace ServerMonitor.Views
{
    public partial class SettingsView : UserControl
    {
        private MainViewModel _viewModel;
        private bool _syncingKey;

        public SettingsView()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as MainViewModel;
            if (_viewModel == null) return;

            // PasswordBox 出于安全设计不支持绑定，进入页面时手工回填。
            // _syncingKey 防止这次赋值又触发 PasswordChanged 反向写回。
            _syncingKey = true;
            AiKeyBox.Password = _viewModel.AiApiKey ?? string.Empty;
            _syncingKey = false;
        }

        /// <summary>
        /// 密钥变化时只更新内存，不写盘。
        /// PasswordBox 每敲一个字符都会触发这里，每次都落盘（还带一次 DPAPI 加密）
        /// 代价太大。落盘交给失焦和离开页面两个时机。
        /// </summary>
        private void OnAiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingKey || _viewModel == null) return;
            _viewModel.AiApiKey = AiKeyBox.Password;
        }

        private void OnAiKeyLostFocus(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.PersistSettings();
        }

        /// <summary>
        /// 离开页面（切页或关窗口）时落盘。
        /// 覆盖「填完密钥直接切走」这种情况，否则刚填的密钥会丢。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.PersistSettings();
        }
    }
}
