using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using ServerMonitor.Models;
using ServerMonitor.Services;

namespace ServerMonitor.ViewModels
{
    /// <summary>日志查看页的数据上下文。</summary>
    public class LogViewModel : ObservableObject
    {
        /// <summary>界面上的级别筛选与日志级别同义：选了某级就显示 ≤ 该级的日志。</summary>
        private int _levelFilterIndex = (int)LogLevel.Info;
        private bool _autoScroll = true;
        private int _shownCount;
        private int _totalCount;
        private string _summary = string.Empty;

        public LogViewModel()
        {
            Entries = new ObservableCollection<LogEntry>();

            ClearCommand = new RelayCommand(Clear);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            ExportCommand = new RelayCommand(Export);
        }

        /// <summary>每追加一条日志时的回调，由 View 层注入（用于自动滚动）。</summary>
        public Action EntryAppended { get; set; }

        /// <summary>由 View 层注入：保存文件对话框。</summary>
        public Func<string, string> AskSavePath { get; set; }

        /// <summary>由 View 层注入：提示消息。</summary>
        public Action<string, string> ShowMessage { get; set; }

        public ObservableCollection<LogEntry> Entries { get; private set; }

        public RelayCommand ClearCommand { get; private set; }
        public RelayCommand OpenFolderCommand { get; private set; }
        public RelayCommand ExportCommand { get; private set; }

        public int LevelFilterIndex
        {
            get { return _levelFilterIndex; }
            set
            {
                if (!SetProperty(ref _levelFilterIndex, value)) return;
                Reload();
            }
        }

        public bool AutoScroll
        {
            get { return _autoScroll; }
            set { SetProperty(ref _autoScroll, value); }
        }

        public string Summary
        {
            get { return _summary; }
            private set { SetProperty(ref _summary, value); }
        }

        /// <summary>从内存缓冲重新加载全部条目（切页或改筛选时调用）。</summary>
        public void Reload()
        {
            Entries.Clear();

            List<LogEntry> all = Logger.Snapshot();
            var filter = (LogLevel)_levelFilterIndex;

            int matched = 0;
            foreach (LogEntry entry in all)
            {
                if (entry.Level > filter) continue;
                Entries.Add(entry);
                matched++;
            }

            _totalCount = all.Count;
            _shownCount = matched;
            UpdateSummary();
        }

        /// <summary>收到新日志时调用（已在 UI 线程上）。</summary>
        public void Append(LogEntry entry)
        {
            _totalCount++;

            if (entry.Level > (LogLevel)_levelFilterIndex) return;

            Entries.Add(entry);
            _shownCount++;

            // 上限与 Logger 的环形缓冲一致，超出就丢最旧的，避免界面越跑越卡
            while (Entries.Count > 2000) Entries.RemoveAt(0);

            UpdateSummary();

            if (EntryAppended != null) EntryAppended();
        }

        private void UpdateSummary()
        {
            Summary = _shownCount == _totalCount
                ? "共 " + _totalCount + " 条"
                : "显示 " + _shownCount + " / " + _totalCount + " 条（已按级别筛选）";
        }

        private void Clear()
        {
            Logger.ClearBuffer();
            Entries.Clear();
            _totalCount = 0;
            _shownCount = 0;
            UpdateSummary();
            Logger.Info("日志", "界面上的日志显示已清空（磁盘文件未受影响）");
        }

        private void OpenFolder()
        {
            string dir = Logger.Directory;
            if (string.IsNullOrEmpty(dir))
            {
                if (ShowMessage != null) ShowMessage("无法打开目录", "日志系统尚未启动。");
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                if (ShowMessage != null) ShowMessage("无法打开目录", ex.Message);
            }
        }

        /// <summary>导出当前筛选结果，方便发给别人排查。</summary>
        private void Export()
        {
            if (Entries.Count == 0)
            {
                if (ShowMessage != null) ShowMessage("无法导出", "当前没有可导出的日志。");
                return;
            }

            string suggested = "服务器监控日志_" +
                               DateTime.Now.ToString("yyyyMMdd_HHmm") + ".log";
            string path = AskSavePath != null ? AskSavePath(suggested) : null;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("服务器监控台 诊断日志导出");
                sb.AppendLine("导出时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("级别筛选: " + Logger.LevelName((LogLevel)_levelFilterIndex).Trim());
                sb.AppendLine("条目数量: " + Entries.Count);
                sb.AppendLine(new string('=', 70));

                foreach (LogEntry entry in Entries)
                {
                    sb.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    sb.Append(" [").Append(Logger.LevelName(entry.Level)).Append(']');
                    if (!string.IsNullOrEmpty(entry.Scope))
                    {
                        sb.Append(" [").Append(entry.Scope).Append(']');
                    }
                    sb.Append(' ').AppendLine(entry.Message);
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

                if (ShowMessage != null)
                {
                    ShowMessage("导出成功", "已导出 " + Entries.Count + " 条日志到：\n" + path);
                }
            }
            catch (Exception ex)
            {
                if (ShowMessage != null) ShowMessage("导出失败", ex.Message);
            }
        }
    }
}
