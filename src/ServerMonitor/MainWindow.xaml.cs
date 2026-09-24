using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ServerMonitor.Services;
using ServerMonitor.ViewModels;
using ServerMonitor.Views;

namespace ServerMonitor
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly List<UserControl> _pages = new List<UserControl>();
        private bool _navReady;

        /// <summary>通知区域图标。第一次隐藏到后台时才创建，之后一直留着。</summary>
        private TrayIcon _tray;

        /// <summary>为真时不再询问，直接放行关闭。关机/注销与托盘「退出」会先把它立起来。</summary>
        private bool _forceClose;

        /// <summary>首次隐藏的提示气泡只弹一次。</summary>
        private bool _balloonShown;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();

            _viewModel = viewModel;
            DataContext = viewModel;

            WireDialogHooks();
            UpdateThemeIcon();

            Loaded += OnLoaded;
            StateChanged += OnWindowStateChanged;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        // ---------------------------------------------------------------
        // 页面
        // ---------------------------------------------------------------

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 页面是按索引存、导航也按索引走的，Loaded 若再触发一次会多出一整套页面
            // （历史页还会跟着多做一轮查询）。纯防御，已知路径上 Loaded 只会来一次。
            if (_navReady) return;

            // 顺序必须与左侧导航一致
            _pages.Add(new DashboardView { DataContext = _viewModel });
            _pages.Add(new ServersView { DataContext = _viewModel });
            _pages.Add(new HistoryView { DataContext = _viewModel.History });
            _pages.Add(new LogsView { DataContext = _viewModel.Logs });
            _pages.Add(new SettingsView { DataContext = _viewModel });

            _navReady = true;
            NavList.SelectedIndex = 0;
            ShowPage(0);
        }

        private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_navReady) return;
            ShowPage(NavList.SelectedIndex);
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) return;

            UserControl page = _pages[index];
            ContentHost.Content = page;

            // 历史页每次进入都重新拉一次日期列表，避免看到过期的数据
            var history = page as HistoryView;
            if (history != null) history.ReloadData();
        }

        // ---------------------------------------------------------------
        // 对话框接线
        // ---------------------------------------------------------------

        private void WireDialogHooks()
        {
            _viewModel.Confirm = (title, message) => MessageDialog.Confirm(VisibleOwner, title, message);
            _viewModel.ShowMessage = (title, message) => MessageDialog.Show(VisibleOwner, title, message);

            _viewModel.History.ShowMessage = (title, message) => MessageDialog.Show(VisibleOwner, title, message);
            _viewModel.Logs.ShowMessage = (title, message) => MessageDialog.Show(VisibleOwner, title, message);
            _viewModel.Logs.AskSavePath = suggested =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = suggested,
                    DefaultExt = ".log",
                    Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    InitialDirectory = _viewModel.DataDirectory
                };
                return dialog.ShowDialog(VisibleOwner) == true ? dialog.FileName : null;
            };
            _viewModel.History.AskSavePath = suggested =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = suggested,
                    DefaultExt = ".csv",
                    Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                    InitialDirectory = _viewModel.DataDirectory
                };
                return dialog.ShowDialog(VisibleOwner) == true ? dialog.FileName : null;
            };

            _viewModel.AskSavePath = suggested =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = suggested,
                    DefaultExt = ".txt",
                    Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    InitialDirectory = _viewModel.DataDirectory
                };
                return dialog.ShowDialog(VisibleOwner) == true ? dialog.FileName : null;
            };

            _viewModel.ShowServerEditor = draft =>
            {
                bool isNew = !_viewModel.Servers.Any(s => ReferenceEquals(s.Config, draft));

                var dialog = new ServerEditDialog(draft, isNew, _viewModel.TestAsync)
                {
                    Owner = VisibleOwner
                };
                return dialog.ShowDialog() == true;
            };
        }

        /// <summary>
        /// 弹对话框时的宿主窗口：先确保窗口是可见的，再把自己交出去当 Owner。
        ///
        /// 有几个对话框是异步的（测试连接 / 测试 Webhook / AI 测试 / 取模型），
        /// 用户点下去之后完全可能先把窗口关到后台；等回调回来时再弹一个 Owner
        /// 不可见的模态框，用户会看到一个凭空冒出来、还看不出属于谁的窗口。
        /// </summary>
        private Window VisibleOwner
        {
            get
            {
                if (!IsVisible) ShowFromTray();
                return this;
            }
        }

        // ---------------------------------------------------------------
        // 关闭与后台运行
        // ---------------------------------------------------------------

        /// <summary>
        /// 关窗口的唯一决策点：标题栏关闭按钮、Alt+F4、系统菜单都走到这里。
        ///
        /// 刻意不在这里再调一次 Close()：窗口正在关闭时重入会被丢掉，表现为
        /// "选了关闭却还在"。ShowDialog 只是嵌套一个新的消息循环，不会让本窗口
        /// 二次关闭——所以"放行"就是不取消，"留下"才取消。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            // 关机/注销：系统正在关会话，这里绝不能再拦（拦了会被提示"此程序阻止关机"），
            // 也不该弹一个用户看不见的询问框。旗子由 App 的 SessionEnding 立起来。
            if (_forceClose)
            {
                DisposeTray();
                return;
            }

            int action = _viewModel.CloseActionIndex;

            if (action == MainViewModel.CloseActionExit)
            {
                DisposeTray();
                return;
            }

            if (action == MainViewModel.CloseActionBackground)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // 每次询问
            CloseChoiceDialog dialog = CloseChoiceDialog.Ask(this);
            if (dialog.DialogResult != true)
            {
                e.Cancel = true;   // 取消：窗口留着，什么都没发生
                return;
            }

            if (dialog.Remember)
            {
                try
                {
                    _viewModel.CloseActionIndex = dialog.RunInBackground
                        ? MainViewModel.CloseActionBackground
                        : MainViewModel.CloseActionExit;
                }
                catch (Exception ex)
                {
                    // 记不住也要按用户刚选的执行：写配置文件失败（被占用、只读等）
                    // 绝不能让"后台运行"变成"直接退出"——那是用户没要的结果。
                    Logger.Warn("窗口", "保存关闭行为失败，本次仍按所选执行：" + ex.Message);
                }
            }

            if (dialog.RunInBackground)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            DisposeTray();
            // 不取消即放行：窗口自然关掉，App 的 ShutdownMode=OnMainWindowClose 负责收尾。
        }

        private void HideToTray()
        {
            if (!EnsureTray())
            {
                // 图标没建起来就绝不能把窗口藏起来——用户会以为程序没了，
                // 而它其实还在后台跑。退而求其次：最小化到任务栏，至少看得见。
                WindowState = WindowState.Minimized;
                return;
            }

            Hide();
            Logger.Info("窗口", "已隐藏到通知区域，采集与告警推送继续。");
        }

        /// <summary>懒创建通知区域图标，返回它是否可用。</summary>
        private bool EnsureTray()
        {
            if (_tray != null) return true;

            try
            {
                _tray = new TrayIcon(this, ShowFromTray, RefreshFromTray, ExitFromTray);
                _tray.Show();

                if (!_balloonShown)
                {
                    _balloonShown = true;
                    _tray.ShowFirstHideBalloon();
                }
                return true;
            }
            catch (Exception ex)
            {
                _tray = null;
                Logger.Warn("窗口", "通知区域图标创建失败，关闭窗口将改为最小化：" + ex.Message);
                return false;
            }
        }

        private void DisposeTray()
        {
            if (_tray == null) return;
            _tray.Dispose();
            _tray = null;
        }

        /// <summary>
        /// 把窗口从通知区域叫回来。App 收到"第二个实例启动"的唤醒信号时也会调它。
        /// </summary>
        internal void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            EnsureOnScreen();
            Activate();
        }

        /// <summary>
        /// 隐藏期间远程桌面重连、换分辨率之后，窗口可能落在可见区域之外，
        /// 恢复出来就是"点了一下但什么都没出现"。WM_GETMINMAXINFO 只管最大化，
        /// 管不到这个。
        /// </summary>
        private void EnsureOnScreen()
        {
            if (WindowState != WindowState.Normal) return;

            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;

            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double right = left + SystemParameters.VirtualScreenWidth;
            double bottom = top + SystemParameters.VirtualScreenHeight;

            // 至少要有 120 像素落在可见区域里，否则标题栏就够不着了
            const double margin = 120;
            bool onScreen = Left + width > left + margin && Left < right - margin
                         && Top + height > top + margin && Top < bottom - margin;
            if (onScreen) return;

            Rect work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - height) / 2);
            Logger.Info("窗口", "窗口落在可见区域之外（分辨率变化？），已移回主屏中央。");
        }

        private void RefreshFromTray()
        {
            _viewModel.RefreshNowCommand.Execute(null);
        }

        private void ExitFromTray()
        {
            // 先立旗：Application.Shutdown() 会把各个窗口再关一遍，
            // 不立旗就会弹出询问框，用户随手点个取消就永远退不掉。
            _forceClose = true;
            DisposeTray();
            Application.Current.Shutdown();
        }

        /// <summary>系统关机/注销时由 App 调用：只立旗子，不关窗口（系统自己会关）。</summary>
        internal void ForceClose()
        {
            _forceClose = true;
        }

        // ---------------------------------------------------------------
        // 窗口按钮
        // ---------------------------------------------------------------

        private void OnMinimize(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeRestore(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnToggleTheme(object sender, RoutedEventArgs e)
        {
            _viewModel.ToggleThemeCommand.Execute(null);
            UpdateThemeIcon();
        }

        private void UpdateThemeIcon()
        {
            // 图标表示"点下去会切到哪一边"
            object geometry = TryFindResource(_viewModel.ThemeIconKey);
            if (geometry != null) ThemeIcon.Data = geometry as Geometry;
        }

        private void OnWindowStateChanged(object sender, EventArgs e)
        {
            bool maximized = WindowState == WindowState.Maximized;

            object geometry = TryFindResource(maximized ? "IconRestore" : "IconMaximize");
            if (geometry != null) MaximizeIcon.Data = geometry as Geometry;

            MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F5 立即采集一轮
            if (e.Key == Key.F5)
            {
                _viewModel.RefreshNowCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+D 切换主题
            else if (e.Key == Key.D && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                _viewModel.ToggleThemeCommand.Execute(null);
                UpdateThemeIcon();
                e.Handled = true;
            }
        }

        // ---------------------------------------------------------------
        // 最大化时贴合作业区
        //
        // WindowStyle=None 的窗口在最大化时会盖住任务栏，需要自己处理
        // WM_GETMINMAXINFO，把尺寸限制到显示器工作区内。
        // ---------------------------------------------------------------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(WindowProc);
        }

        private const int WM_GETMINMAXINFO = 0x0024;

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
                                         ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                ApplyWorkAreaLimit(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ApplyWorkAreaLimit(IntPtr hwnd, IntPtr lParam)
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return;

            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(monitor, ref info)) return;

            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));

            RECT work = info.rcWork;
            RECT full = info.rcMonitor;

            mmi.ptMaxPosition.x = work.left - full.left;
            mmi.ptMaxPosition.y = work.top - full.top;
            mmi.ptMaxSize.x = work.right - work.left;
            mmi.ptMaxSize.y = work.bottom - work.top;

            // 不允许把窗口拖得比工作区还大
            mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
            mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
