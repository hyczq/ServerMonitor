using System;
using System.Windows;
using System.Windows.Controls;
using ServerMonitor.Services;
using ServerMonitor.ViewModels;

namespace ServerMonitor.Views
{
    public partial class LogsView : UserControl
    {
        private LogViewModel _viewModel;
        private bool _subscribed;

        public LogsView()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as LogViewModel;
            if (_viewModel == null) return;

            _viewModel.EntryAppended = ScrollToEndIfFollowing;

            if (!_subscribed)
            {
                Logger.EntryWritten += OnLogEntryWritten;
                _subscribed = true;
            }

            // 每次进入本页都重新拉一次：切走期间产生的日志也要显示出来
            _viewModel.Reload();
            ScrollToEndIfFollowing();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 页面切走后取消订阅，避免不可见的页面还在往集合里塞数据
            if (_subscribed)
            {
                Logger.EntryWritten -= OnLogEntryWritten;
                _subscribed = false;
            }
        }

        /// <summary>
        /// 日志可能来自采集线程。这里统一调度回 UI 线程再动集合，
        /// ObservableCollection 不支持跨线程修改。
        /// </summary>
        private void OnLogEntryWritten(object sender, LogEntry entry)
        {
            Application app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel != null) _viewModel.Append(entry);
            }));
        }

        private void ScrollToEndIfFollowing()
        {
            if (_viewModel == null || !_viewModel.AutoScroll) return;

            int count = LogList.Items.Count;
            if (count == 0) return;

            LogList.ScrollIntoView(LogList.Items[count - 1]);
        }
    }
}
