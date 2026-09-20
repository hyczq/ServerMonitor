using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ServerMonitor.ViewModels;
using ServerMonitor.Views;

namespace ServerMonitor
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly List<UserControl> _pages = new List<UserControl>();
        private bool _navReady;

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
            _viewModel.Confirm = (title, message) => MessageDialog.Confirm(this, title, message);
            _viewModel.ShowMessage = (title, message) => MessageDialog.Show(this, title, message);

            _viewModel.History.ShowMessage = (title, message) => MessageDialog.Show(this, title, message);
            _viewModel.Logs.ShowMessage = (title, message) => MessageDialog.Show(this, title, message);
            _viewModel.Logs.AskSavePath = suggested =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = suggested,
                    DefaultExt = ".log",
                    Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    InitialDirectory = _viewModel.DataDirectory
                };
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
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
                return dialog.ShowDialog(this) == true ? dialog.FileName : null;
            };

            _viewModel.ShowServerEditor = draft =>
            {
                bool isNew = !_viewModel.Servers.Any(s => ReferenceEquals(s.Config, draft));

                var dialog = new ServerEditDialog(draft, isNew, _viewModel.TestAsync)
                {
                    Owner = this
                };
                return dialog.ShowDialog() == true;
            };
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
