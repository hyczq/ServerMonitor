using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ServerMonitor.Models;
using ServerMonitor.ViewModels;

namespace ServerMonitor.Views
{
    public partial class ServerEditDialog : Window
    {
        private readonly ServerEditViewModel _viewModel;
        private readonly Func<ServerConfig, Task<string>> _tester;

        public ServerEditDialog(ServerConfig config, bool isNew,
                                Func<ServerConfig, Task<string>> tester)
        {
            InitializeComponent();

            _viewModel = new ServerEditViewModel(config, isNew);
            _tester = tester;
            DataContext = _viewModel;

            // PasswordBox 出于安全设计不支持绑定，只能手工赋值。
            // 赋值会触发 PasswordChanged，由 OnPasswordChanged 负责同步到
            // 明文框和 ViewModel，所以这里不需要再手工赋值一次。
            PasswordInput.Password = config.Password ?? string.Empty;
        }

        /// <summary>
        /// 防止两个输入框在互相同步时来回触发事件。
        /// </summary>
        private bool _syncingPassword;

        /// <summary>
        /// 口令框内容变化 -> 同步到明文框和 ViewModel。
        ///
        /// 这里必须同步到 ViewModel：BuildProbe() 读的就是 ViewModel 里的口令。
        /// 早期版本只在"保存"时同步，导致「测试连接」发出去的是空口令或旧口令，
        /// 用户明明填对了也报认证失败。
        /// </summary>
        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword) return;

            _syncingPassword = true;
            try
            {
                PasswordPlain.Text = PasswordInput.Password;
                _viewModel.Password = PasswordInput.Password;
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        /// <summary>明文框内容变化 -> 同步回口令框和 ViewModel。</summary>
        private void OnPlainTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_syncingPassword) return;

            _syncingPassword = true;
            try
            {
                PasswordInput.Password = PasswordPlain.Text;
                _viewModel.Password = PasswordPlain.Text;
            }
            finally
            {
                _syncingPassword = false;
            }
        }

        /// <summary>
        /// 切换口令的明文 / 掩码显示。
        ///
        /// 只切换可见性 —— 两个输入框的内容由 OnPasswordChanged /
        /// OnPlainTextChanged 持续保持同步，这里不需要再搬运取值
        /// （搬运反而可能把过期的值盖上）。
        /// </summary>
        private void OnToggleReveal(object sender, RoutedEventArgs e)
        {
            bool showPlain = PasswordPlain.Visibility != Visibility.Visible;

            if (showPlain)
            {
                PasswordPlain.Visibility = Visibility.Visible;
                PasswordInput.Visibility = Visibility.Collapsed;
                PasswordPlain.Focus();
                PasswordPlain.CaretIndex = PasswordPlain.Text.Length;
            }
            else
            {
                PasswordInput.Visibility = Visibility.Visible;
                PasswordPlain.Visibility = Visibility.Collapsed;
            }

            // 图标表示"点下去会变成什么"，所以当前是明文就显示"隐藏"图标
            object geometry = TryFindResource(showPlain ? "IconEyeOff" : "IconEye");
            if (geometry != null) RevealIcon.Data = geometry as Geometry;

            RevealButton.ToolTip = showPlain ? "隐藏口令" : "显示口令明文";
        }

        private async void OnTest(object sender, RoutedEventArgs e)
        {
            if (_tester == null) return;

            _viewModel.IsTesting = true;
            _viewModel.SetTestResult(false, "正在连接，请稍候…");

            try
            {
                ServerConfig probe = _viewModel.BuildProbe();
                string error = await _tester(probe);

                if (error == null)
                {
                    _viewModel.SetTestResult(true,
                        "连接成功。采集通道：" + probe.ProtocolLabel);
                }
                else
                {
                    _viewModel.SetTestResult(false, "连接失败：" + error);
                }
            }
            catch (Exception ex)
            {
                _viewModel.SetTestResult(false, "连接失败：" + ex.Message);
            }
            finally
            {
                _viewModel.IsTesting = false;
            }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            // 口令已由输入事件实时同步进 ViewModel，这里不用再取一次
            string error;
            if (!_viewModel.TryCommit(out error))
            {
                return; // 校验消息已经显示在对话框里
            }

            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
