using System;
using System.Globalization;
using System.IO;
using System.Threading;
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
        private DiskTrendStore _diskTrend;
        private MainViewModel _viewModel;
        private MainWindow _window;

        /// <summary>
        /// 单实例用的命名事件：第二个实例给它 Set 一下，第一个实例的等待线程
        /// 就把窗口叫出来。它同时回答了"有没有人在跑"。
        /// </summary>
        private EventWaitHandle _showEvent;

        /// <summary>后台等待线程（IsBackground，不会拖住进程退出）。</summary>
        private Thread _showThread;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            SessionEnding += OnSessionEnding;

            // 单实例检查必须放在 Logger.Start 之前：两份程序按天追加同一个日志文件
            // 会共享冲突。代价是这一句要说的话此刻还写不了盘（Logger 在 Start 之前
            // 只把内容放进内存缓冲），所以先存在字段里，Logger.Start 之后补记。
            string singleInstanceWarning = null;
            if (!EnsureSingleInstance(ref singleInstanceWarning))
            {
                Shutdown();
                return;
            }

            // .NET Framework 4.5 的 ServicePointManager 默认不含 TLS 1.2，
            // 不显式打开的话，所有 HTTPS 调用都会失败并报「基础连接已关闭」
            // 这类看不出原因的错误。放在最前面，AI 接口和 Webhook 都受益。
            AiClient.EnableModernTls();

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

            // 单实例检查的结论只能等到这里才写得了盘
            if (singleInstanceWarning != null) Logger.Warn("启动", singleInstanceWarning);

            _history = new HistoryStore(dataDirectory);
            _history.Load();
            Logger.Debug("历史数据已加载");

            // 磁盘容量趋势的日级归档。与历史库一样：启动时加载，退出时落盘。
            _diskTrend = new DiskTrendStore(dataDirectory);
            _diskTrend.Load();

            ThemeManager.Apply(_config.Settings.DarkTheme);
            Logger.Debug("主题已应用：" + (_config.Settings.DarkTheme ? "深色" : "浅色"));

            _viewModel = new MainViewModel(_config, _history, _diskTrend);

            _window = new MainWindow(_viewModel);
            MainWindow = _window;
            _window.Show();
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

            // 单实例句柄放在日志之后释放。先 Set 一下让等待线程醒过来收工——
            // 它是后台线程，不 Set 也不会拖住进程，只是没必要让它继续挂着。
            try
            {
                if (_showEvent != null)
                {
                    _showEvent.Set();
                    _showEvent.Dispose();
                    _showEvent = null;
                }
            }
            catch
            {
            }

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

        // ---------------------------------------------------------------
        // 关机 / 注销
        // ---------------------------------------------------------------

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // 绝不设 e.Cancel：用户要关机就让他关。
            //
            // 这里只把主窗口的旗子立起来，免得藏在通知区域里的窗口弹出一个用户
            // 看不见的询问框挡住关机（紧接着系统就会提示"此程序阻止关机"）。
            // 旗子在两种顺序下都成立：系统若照常触发窗口的 Closing，它让流程直接
            // 放行；若不触发，进程也会随会话一起结束。
            //
            // 代价说清楚：万一之后有别的程序取消了这次关机，本进程已经走在退出的
            // 路上、不会再回来。对监控程序来说，"跟着系统干净地退出"比"卡住关机"
            // 重要得多。
            Logger.Info("系统", "系统会话结束（关机或注销）：" + e.ReasonSessionEnding);

            if (_window != null) _window.ForceClose();
        }

        // ---------------------------------------------------------------
        // 单实例
        //
        // 目的只有一个：别让两个进程同时写 data 下的 servers.json / disk-daily.json。
        // 名称里带数据目录的哈希，是为了让同一台机器上的两份便携副本（各写各的
        // data\）互不干扰——同名的话第二份会因为"已有人在跑"而打不开。
        // ---------------------------------------------------------------

        /// <summary>
        /// 检查是否已有实例在运行。返回 false 表示本进程应当立刻退出
        /// （那边的窗口已经收到唤醒信号）。
        ///
        /// 建不出命名事件时（权限或组策略限制）记一条警告后照常多实例运行：
        /// 单实例只是防手滑，不能因为它把程序弄得打不开。
        /// </summary>
        private bool EnsureSingleInstance(ref string warning)
        {
            string name;
            try
            {
                name = @"Local\ServerMonitor.Show."
                     + FolderKey(AppDomain.CurrentDomain.BaseDirectory);
            }
            catch (Exception ex)
            {
                warning = "单实例检查已跳过：" + ex.Message;
                return true;
            }

            try
            {
                bool createdNew;
                // AutoReset 顺手解决"第二个实例起得比等待线程还早"的竞态：
                // 信号不会被丢掉，而是排在那儿等下一次 WaitOne。
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name,
                                                 out createdNew);

                if (!createdNew)
                {
                    // 已经有一份在跑：把它的窗口叫出来，然后自己退出。
                    // 不弹任何提示——窗口自己出现就是最好的反馈。
                    _showEvent.Set();
                    _showEvent.Dispose();
                    _showEvent = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                warning = "单实例检查失败，本次按多实例运行：" + ex.Message;
                return true;
            }

            _showThread = new Thread(WaitForShowSignal);
            _showThread.IsBackground = true;
            _showThread.Name = "ServerMonitor.ShowWait";
            _showThread.Start();
            return true;
        }

        /// <summary>
        /// 等第二个实例的唤醒信号。跑在后台线程上，所以要调度回 UI 线程——
        /// 回调里会 Show/Activate 窗口，非 UI 线程碰窗口会抛。
        /// </summary>
        private void WaitForShowSignal()
        {
            while (true)
            {
                try
                {
                    _showEvent.WaitOne();
                }
                catch (Exception)
                {
                    return;   // 句柄已经在退出流程里释放了
                }

                MainWindow window = _window;
                if (window == null) return;

                try
                {
                    window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
                }
                catch (Exception)
                {
                    return;   // dispatcher 正在关闭
                }
            }
        }

        /// <summary>
        /// 数据目录的短哈希，给命名事件当后缀。
        ///
        /// 不用 string.GetHashCode()：它跨进程不保证一致，单实例会时灵时不灵。
        /// 也不用 MD5/SHA1：FIPS 强制模式下这些算法会直接抛异常。
        /// FNV-1a 十来行，既确定又没这些坑。
        /// </summary>
        private static string FolderKey(string directory)
        {
            string normalized = string.IsNullOrEmpty(directory)
                ? "?"
                : directory.TrimEnd('\\', '/').ToLowerInvariant();

            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                uint hash = offset;
                for (int i = 0; i < normalized.Length; i++)
                {
                    // 按 UTF-16 码元逐个混入，中文路径也照样得到一个确定的短串
                    hash ^= normalized[i];
                    hash *= prime;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static void ShowFatal(Exception ex)
        {
            string message = ex == null ? "未知错误" : ex.Message;
            try
            {
                // 主窗口正躲在通知区域时不弹框：那是用户自己藏起来的，凭空冒出
                // 一个没有归属的警告框只会吓人，这种情况日志就是全部记录。
                // 主窗口还没建起来（启动阶段就崩了）则照旧弹框——那种情况下没有
                // 任何别的反馈，用户只会看到"程序打不开"，连个原因都没有。
                Window main = Current != null ? Current.MainWindow : null;
                if (main != null && !main.IsVisible) return;

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
