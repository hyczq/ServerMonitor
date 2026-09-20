using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ServerMonitor.Models;
using ServerMonitor.Services;
using ServerMonitor.Themes;
using ServerMonitor.ViewModels;

namespace ServerMonitor
{
    public partial class App : Application
    {
        private ConfigStore _config;
        private HistoryStore _history;
        private MainViewModel _viewModel;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

            string dataDirectory = ResolveDataDirectory();

            _config = new ConfigStore(dataDirectory);
            _config.Load();

            // 日志要在读配置之后启动——级别和保留天数都来自配置
            Logger.Start(dataDirectory,
                         Logger.ParseLevel(_config.Settings.LogLevel, LogLevel.Info),
                         _config.Settings.LogRetentionDays);

            Logger.Info("==================================================");
            Logger.Info("程序启动  版本=" + typeof(App).Assembly.GetName().Version);
            Logger.Info("数据目录  " + dataDirectory);
            Logger.Info("操作系统  " + Environment.OSVersion +
                        "  .NET " + Environment.Version +
                        "  启动账户=" + Environment.UserName);
            Logger.Info("已配置服务器 " + _config.Servers.Count + " 台");

            _history = new HistoryStore(dataDirectory);
            _history.Load();
            Logger.Debug("历史数据已加载");

            ThemeManager.Apply(_config.Settings.DarkTheme);
            Logger.Debug("主题已应用：" + (_config.Settings.DarkTheme ? "深色" : "浅色"));

            _viewModel = new MainViewModel(_config, _history);

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
            Logger.Info("主窗口已显示");

            _viewModel.Start();
            Logger.Info("采集调度已启动，间隔 " + _config.Settings.RefreshSeconds + " 秒");
        }

        /// <summary>
        /// 数据目录优先放在程序旁边（便携）；若因权限不可写
        /// （例如装到了 Program Files），退回当前用户的 AppData。
        /// </summary>
        private static string ResolveDataDirectory()
        {
            string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (IsWritable(beside)) return beside;

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ServerMonitor");
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        private static bool IsWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info("程序正在退出");

            if (_viewModel != null)
            {
                try { _viewModel.Dispose(); }
                catch (Exception ex) { Logger.Error("退出清理", "释放资源失败", ex); }
            }

            Logger.Info("程序已退出");
            Logger.Shutdown();

            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 界面线程异常不直接崩掉整个程序，否则长时间跑着的监控会白干
            e.Handled = true;
            Logger.Error("未处理异常", "界面线程出现未捕获异常", e.Exception);
            ShowFatal(e.Exception);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Error("未处理异常", "后台线程出现未捕获异常（进程即将结束=" + e.IsTerminating + "）", ex);
            ShowFatal(ex);
        }

        private static void ShowFatal(Exception ex)
        {
            string message = ex == null ? "未知错误" : ex.Message;
            try
            {
                MessageBox.Show(
                    "程序遇到一个未处理的错误：\n\n" + message +
                    "\n\n监控会继续运行。如果反复出现，请把该提示反馈给维护人员。",
                    "服务器监控台",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
            }
        }
    }
}
