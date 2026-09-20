using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ServerMonitor.Converters;
using ServerMonitor.Controls;
using ServerMonitor.Models;
using ServerMonitor.Services;

namespace ServerMonitor.ViewModels
{
    /// <summary>历史统计表格中的一行（某台服务器在某一天的汇总）。</summary>
    public class HistoryRowViewModel
    {
        public string ServerId { get; set; }
        public string ServerName { get; set; }
        public string ServerHost { get; set; }
        public string OsLabel { get; set; }

        public int Samples { get; set; }
        public int OfflineSamples { get; set; }
        public double OnlineRate { get; set; }

        public double CpuAvg { get; set; }
        public double CpuMax { get; set; }
        public double CpuMin { get; set; }
        public double MemAvg { get; set; }
        public double MemMax { get; set; }
        public double DiskAvg { get; set; }
        public double DiskMax { get; set; }

        public HealthLevel PeakLevel { get; set; }

        public string OnlineRateText { get { return OnlineRate.ToString("0.0", CultureInfo.InvariantCulture) + "%"; } }
        public string CpuAvgText { get { return Formats.Percent(CpuAvg); } }
        public string CpuMaxText { get { return Formats.Percent(CpuMax); } }
        public string MemAvgText { get { return Formats.Percent(MemAvg); } }
        public string MemMaxText { get { return Formats.Percent(MemMax); } }
        public string DiskAvgText { get { return Formats.Percent(DiskAvg); } }
        public string DiskMaxText { get { return Formats.Percent(DiskMax); } }

        public string SampleText
        {
            get
            {
                if (OfflineSamples == 0) return Samples.ToString();
                return Samples + "（失败 " + OfflineSamples + "）";
            }
        }
    }

    /// <summary>图例条目：颜色只是辅助，名称才是识别依据。</summary>
    public class LegendEntry
    {
        public string Name { get; set; }
        public string SeriesKey { get; set; }
    }

    public class HistoryViewModel : ObservableObject
    {
        private readonly ConfigStore _config;
        private readonly HistoryStore _history;

        private string _selectedDate;
        private HistoryRowViewModel _selectedRow;
        private string _summaryText = "暂无数据";
        private string _chartTitle = "请选择服务器查看当日走势";

        /// <summary>由 View 层注入：弹出保存文件对话框，返回目标路径。</summary>
        public Func<string, string> AskSavePath { get; set; }

        /// <summary>由 View 层注入：提示消息。</summary>
        public Action<string, string> ShowMessage { get; set; }

        public HistoryViewModel(ConfigStore config, HistoryStore history)
        {
            _config = config;
            _history = history;

            AvailableDates = new ObservableCollection<string>();
            Rows = new ObservableCollection<HistoryRowViewModel>();
            TrendSeries = new ObservableCollection<TrendSeries>();
            TrendLabels = new List<string>();
            Legend = new ObservableCollection<LegendEntry>();

            ExportCsvCommand = new RelayCommand(ExportCsv, () => Rows.Count > 0);
            RefreshCommand = new RelayCommand(Reload);
        }

        public ObservableCollection<string> AvailableDates { get; private set; }
        public ObservableCollection<HistoryRowViewModel> Rows { get; private set; }
        public ObservableCollection<TrendSeries> TrendSeries { get; private set; }
        public IList<string> TrendLabels { get; private set; }

        /// <summary>图表图例。两条以上序列必须有图例，识别不能只靠颜色。</summary>
        public ObservableCollection<LegendEntry> Legend { get; private set; }

        public RelayCommand ExportCsvCommand { get; private set; }
        public RelayCommand RefreshCommand { get; private set; }

        public string SelectedDate
        {
            get { return _selectedDate; }
            set
            {
                if (!SetProperty(ref _selectedDate, value)) return;
                LoadRows();
            }
        }

        public HistoryRowViewModel SelectedRow
        {
            get { return _selectedRow; }
            set
            {
                if (!SetProperty(ref _selectedRow, value)) return;
                LoadTrend();
            }
        }

        public string SummaryText
        {
            get { return _summaryText; }
            private set { SetProperty(ref _summaryText, value); }
        }

        public string ChartTitle
        {
            get { return _chartTitle; }
            private set { SetProperty(ref _chartTitle, value); }
        }

        /// <summary>切换页面时调用，重新拉取可选日期。</summary>
        public void Reload()
        {
            string previous = _selectedDate;

            List<string> dates = _history.GetAvailableDates();
            if (dates.Count == 0) dates.Add(DateTime.Today.ToString("yyyy-MM-dd"));

            AvailableDates.Clear();
            foreach (string date in dates) AvailableDates.Add(date);

            string target = previous != null && AvailableDates.Contains(previous)
                ? previous
                : AvailableDates[0];

            if (_selectedDate == target)
            {
                LoadRows();
            }
            else
            {
                SelectedDate = target; // 触发 LoadRows
            }
        }

        private void LoadRows()
        {
            Rows.Clear();
            TrendSeries.Clear();
            TrendLabels = new List<string>();
            Raise("TrendLabels");

            if (string.IsNullOrEmpty(_selectedDate))
            {
                SummaryText = "暂无数据";
                return;
            }

            foreach (ServerConfig server in _config.Servers)
            {
                DailyAggregate aggregate = _history.GetAggregate(server.Id, _selectedDate);
                if (aggregate == null || aggregate.Samples == 0) continue;

                Rows.Add(new HistoryRowViewModel
                {
                    ServerId = server.Id,
                    ServerName = server.DisplayName,
                    ServerHost = server.Host,
                    OsLabel = server.Os == OsType.Windows ? "Windows" : "Linux",
                    Samples = aggregate.Samples,
                    OfflineSamples = aggregate.OfflineSamples,
                    OnlineRate = aggregate.OnlineRate,
                    CpuAvg = aggregate.CpuAvg,
                    CpuMax = aggregate.CpuMax,
                    CpuMin = aggregate.CpuMin,
                    MemAvg = aggregate.MemAvg,
                    MemMax = aggregate.MemMax,
                    DiskAvg = aggregate.DiskAvg,
                    DiskMax = aggregate.DiskMax,

                    // 全天一次都没采到时不能标成"正常"——0% 是缺数据，不是低负载。
                    // 绿色会让运维误以为这台机器状态良好。
                    PeakLevel = aggregate.OnlineSamples == 0
                        ? HealthLevel.Offline
                        : Formats.Classify(
                            Math.Max(aggregate.CpuMax,
                                     Math.Max(aggregate.MemMax, aggregate.DiskMax)),
                            _config.Settings)
                });
            }

            if (Rows.Count == 0)
            {
                SummaryText = _selectedDate + " 没有采样记录";
                ChartTitle = "请选择服务器查看当日走势";
                return;
            }

            double avgOnline = Rows.Average(r => r.OnlineRate);
            SummaryText = string.Format(CultureInfo.InvariantCulture,
                "{0} 共 {1} 台服务器有记录，平均在线率 {2:0.0}%",
                _selectedDate, Rows.Count, avgOnline);

            SelectedRow = Rows[0];
        }

        private void LoadTrend()
        {
            TrendSeries.Clear();
            Legend.Clear();
            TrendLabels = new List<string>();
            Raise("TrendLabels");

            if (_selectedRow == null || string.IsNullOrEmpty(_selectedDate))
            {
                ChartTitle = "请选择服务器查看当日走势";
                return;
            }

            List<MetricSample> samples = _history.GetSamples(_selectedDate, _selectedRow.ServerId);
            if (samples.Count == 0)
            {
                ChartTitle = _selectedRow.ServerName + " · 当日无采样数据";
                return;
            }

            var labels = new List<string>(samples.Count);
            var cpu = new List<double>(samples.Count);
            var mem = new List<double>(samples.Count);
            var disk = new List<double>(samples.Count);

            foreach (MetricSample sample in samples)
            {
                labels.Add(sample.T);
                cpu.Add(sample.Cpu);
                mem.Add(sample.Mem);
                disk.Add(sample.Disk);
            }

            TrendSeries.Add(new TrendSeries { Name = "CPU", SeriesKey = "Series1", Values = cpu });
            TrendSeries.Add(new TrendSeries { Name = "内存", SeriesKey = "Series2", Values = mem });
            TrendSeries.Add(new TrendSeries { Name = "磁盘", SeriesKey = "Series3", Values = disk });

            Legend.Add(new LegendEntry { Name = "CPU", SeriesKey = "Series1" });
            Legend.Add(new LegendEntry { Name = "内存", SeriesKey = "Series2" });
            Legend.Add(new LegendEntry { Name = "磁盘", SeriesKey = "Series3" });

            TrendLabels = labels;
            Raise("TrendLabels");

            ChartTitle = string.Format(CultureInfo.InvariantCulture,
                "{0} · {1} 当日走势（{2} 个采样点）",
                _selectedRow.ServerName, _selectedDate, samples.Count);
        }

        private void ExportCsv()
        {
            if (_selectedDate == null || Rows.Count == 0) return;

            string suggested = "服务器用量统计_" + _selectedDate + ".csv";
            string path = AskSavePath != null ? AskSavePath(suggested) : null;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var builder = new StringBuilder();
                builder.AppendLine("服务器,地址,系统,采样次数,失败次数,在线率(%)," +
                                   "CPU均值(%),CPU峰值(%),CPU最低(%)," +
                                   "内存均值(%),内存峰值(%),磁盘均值(%),磁盘峰值(%)");

                foreach (HistoryRowViewModel row in Rows)
                {
                    builder.AppendLine(string.Join(",", new[]
                    {
                        Csv(row.ServerName), Csv(row.ServerHost), Csv(row.OsLabel),
                        row.Samples.ToString(CultureInfo.InvariantCulture),
                        row.OfflineSamples.ToString(CultureInfo.InvariantCulture),
                        row.OnlineRate.ToString("0.0", CultureInfo.InvariantCulture),
                        row.CpuAvg.ToString("0.0", CultureInfo.InvariantCulture),
                        row.CpuMax.ToString("0.0", CultureInfo.InvariantCulture),
                        row.CpuMin.ToString("0.0", CultureInfo.InvariantCulture),
                        row.MemAvg.ToString("0.0", CultureInfo.InvariantCulture),
                        row.MemMax.ToString("0.0", CultureInfo.InvariantCulture),
                        row.DiskAvg.ToString("0.0", CultureInfo.InvariantCulture),
                        row.DiskMax.ToString("0.0", CultureInfo.InvariantCulture)
                    }));
                }

                // 带 BOM，否则 Excel 打开中文会乱码
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));

                if (ShowMessage != null)
                {
                    ShowMessage("导出成功", "已导出 " + Rows.Count + " 条记录到：\n" + path);
                }
            }
            catch (Exception ex)
            {
                if (ShowMessage != null)
                {
                    ShowMessage("导出失败", ex.Message);
                }
            }
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOf(',') < 0 && value.IndexOf('"') < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
