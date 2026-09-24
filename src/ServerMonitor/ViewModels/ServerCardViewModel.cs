using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ServerMonitor.Converters;
using ServerMonitor.Models;
using ServerMonitor.Services;

namespace ServerMonitor.ViewModels
{
    /// <summary>磁盘列表中的一行。</summary>
    public class DiskRowViewModel
    {
        public string Mount { get; set; }
        public string FileSystem { get; set; }
        public double Percent { get; set; }
        public HealthLevel Level { get; set; }
        public double TotalBytes { get; set; }
        public double UsedBytes { get; set; }

        /// <summary>
        /// 容量趋势标注（"按当前增速约 25 天后写满"）。
        ///
        /// 空串表示这个分区没有预测，模板里由 StringToVis 收起整行，
        /// 因此无预测的行高度与从前完全一致。
        /// </summary>
        public string ForecastText { get; set; }

        public string UsageText
        {
            get
            {
                return Formats.Bytes(UsedBytes) + " / " + Formats.Bytes(TotalBytes);
            }
        }

        public string FreeText
        {
            get { return "可用 " + Formats.Bytes(Math.Max(0, TotalBytes - UsedBytes)); }
        }
    }

    /// <summary>总览页的一张服务器卡片。</summary>
    public class ServerCardViewModel : ObservableObject
    {
        private readonly AppSettings _settings;

        private bool _isOnline;
        private string _errorText;
        private DateTime? _lastUpdated;
        private double _cpuPercent;
        private double _memPercent;
        private double _diskPercent;
        private HealthLevel _overallLevel = HealthLevel.Unknown;
        private string _hostname;
        private string _kernel;
        private string _cpuModel;
        private string _uptimeText = "--";
        private string _loadText = "--";
        private string _memText = "--";
        private string _diskText = "--";

        /// <summary>
        /// 归一化挂载点 -> 趋势标注文案（"按当前增速约 25 天后写满"）。
        ///
        /// 不能在算完之后把文案推进 DiskRowViewModel：Apply 每个采集周期都
        /// Disks.Clear() 重建，推进去的东西下一轮就没了。改成在 Apply 里查表填写。
        ///
        /// 只在界面线程整体换引用（SetForecasts 紧挨着 Apply 调用），
        /// 不存在跨线程改 WPF 绑定对象的问题。
        /// </summary>
        private Dictionary<string, string> _forecastByMount;

        public ServerConfig Config { get; private set; }

        /// <summary>
        /// 卡片上直接展示的磁盘数量，多出来的收进"还有 N 个"提示里。
        /// 取 4 是因为 Windows 服务器常见 C:/D:/E:/F: 四个分区，
        /// 而 3 个会让第四块盘被迫藏进提示里。
        /// </summary>
        public const int VisibleDiskCount = 4;

        public ServerCardViewModel(ServerConfig config, AppSettings settings)
        {
            Config = config;
            _settings = settings;
            CpuHistory = new ObservableCollection<double>();
            MemHistory = new ObservableCollection<double>();
            DiskHistory = new ObservableCollection<double>();
            Disks = new ObservableCollection<DiskRowViewModel>();
            TopDisks = new ObservableCollection<DiskRowViewModel>();
        }

        public ObservableCollection<double> CpuHistory { get; private set; }
        public ObservableCollection<double> MemHistory { get; private set; }
        public ObservableCollection<double> DiskHistory { get; private set; }

        /// <summary>全部挂载点，按用量降序。</summary>
        public ObservableCollection<DiskRowViewModel> Disks { get; private set; }

        /// <summary>卡片上实际渲染的前几个挂载点。</summary>
        public ObservableCollection<DiskRowViewModel> TopDisks { get; private set; }

        public bool HasMoreDisks { get { return Disks.Count > VisibleDiskCount; } }

        public string MoreDisksText
        {
            get
            {
                int hidden = Disks.Count - VisibleDiskCount;
                return hidden > 0 ? "还有 " + hidden + " 个挂载点" : string.Empty;
            }
        }

        public string MoreDisksTooltip
        {
            get
            {
                if (Disks.Count <= VisibleDiskCount) return null;

                var lines = new List<string>();
                for (int i = VisibleDiskCount; i < Disks.Count; i++)
                {
                    DiskRowViewModel disk = Disks[i];
                    lines.Add(disk.Mount + "  " + Formats.PercentShort(disk.Percent) +
                              "  (" + disk.UsageText + ")");
                }
                return string.Join("\n", lines);
            }
        }

        public string Id { get { return Config.Id; } }
        public string Name { get { return Config.DisplayName; } }
        public string Host { get { return Config.Host; } }

        public string OsLabel
        {
            get { return Config.Os == OsType.Windows ? "Windows" : "Linux"; }
        }

        public string ProtocolLabel { get { return Config.ProtocolLabel; } }

        public string AddressText
        {
            get
            {
                int port = Config.EffectivePort;
                return port > 0 ? Config.Host + ":" + port : Config.Host;
            }
        }

        public bool IsOnline
        {
            get { return _isOnline; }
            private set { SetProperty(ref _isOnline, value); }
        }

        public string ErrorText
        {
            get { return _errorText; }
            private set { SetProperty(ref _errorText, value); }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorText); }
        }

        public string LastUpdatedText
        {
            get
            {
                if (!_lastUpdated.HasValue) return "尚未采集";
                return _lastUpdated.Value.ToString("HH:mm:ss") + " 更新";
            }
        }

        public string Hostname
        {
            get { return string.IsNullOrWhiteSpace(_hostname) ? Config.Host : _hostname; }
            private set { _hostname = value; Raise(); Raise("Hostname"); }
        }

        public string Kernel
        {
            get { return _kernel; }
            private set { SetProperty(ref _kernel, value); }
        }

        public string CpuModel
        {
            get { return _cpuModel; }
            private set { SetProperty(ref _cpuModel, value); }
        }

        public string UptimeText
        {
            get { return _uptimeText; }
            private set { SetProperty(ref _uptimeText, value); }
        }

        public string LoadText
        {
            get { return _loadText; }
            private set { SetProperty(ref _loadText, value); }
        }

        public string MemText
        {
            get { return _memText; }
            private set { SetProperty(ref _memText, value); }
        }

        public string DiskText
        {
            get { return _diskText; }
            private set { SetProperty(ref _diskText, value); }
        }

        // ---- 三项指标的数值与等级 ----

        public double CpuPercent
        {
            get { return _cpuPercent; }
            private set { SetProperty(ref _cpuPercent, value); }
        }

        public double MemPercent
        {
            get { return _memPercent; }
            private set { SetProperty(ref _memPercent, value); }
        }

        public double DiskPercent
        {
            get { return _diskPercent; }
            private set { SetProperty(ref _diskPercent, value); }
        }

        public HealthLevel CpuLevel { get { return Classify(_cpuPercent); } }
        public HealthLevel MemLevel { get { return Classify(_memPercent); } }
        public HealthLevel DiskLevel { get { return Classify(_diskPercent); } }

        public HealthLevel OverallLevel
        {
            get { return _overallLevel; }
            private set { SetProperty(ref _overallLevel, value); }
        }

        public string CpuLevelText { get { return Formats.LevelText(CpuLevel); } }
        public string MemLevelText { get { return Formats.LevelText(MemLevel); } }
        public string DiskLevelText { get { return Formats.LevelText(DiskLevel); } }
        public string OverallLevelText { get { return Formats.LevelText(OverallLevel); } }

        private HealthLevel Classify(double percent)
        {
            if (!_isOnline) return HealthLevel.Offline;
            return Formats.Classify(percent, _settings);
        }

        /// <summary>
        /// Config 是普通 POCO，自身不发变更通知。启用状态、分组等字段被改动后
        /// 必须调用本方法，否则绑定在 Config.Enabled 上的按钮文字不会更新。
        /// </summary>
        public void NotifyConfigChanged()
        {
            Raise("Config");
        }

        /// <summary>阈值或主题变动后重新计算派生属性。</summary>
        public void RefreshDerived()
        {
            // 编辑对话框可能改过分组等字段，一并刷新
            Raise("Config");

            Raise("CpuLevel");
            Raise("MemLevel");
            Raise("DiskLevel");
            Raise("CpuLevelText");
            Raise("MemLevelText");
            Raise("DiskLevelText");
            Raise("OverallLevelText");
            Raise("Name");
            Raise("Host");
            Raise("AddressText");
            Raise("OsLabel");
            Raise("ProtocolLabel");
        }

        /// <summary>
        /// 下发容量趋势标注。键是归一化挂载点（DiskTrendStore.NormalizeMount），
        /// 值是要显示的一行字；表里没有的挂载点不标注。
        ///
        /// 整份换引用，下一次 Apply 重建磁盘行时生效（调用方紧挨在 Apply 前调用）。
        /// </summary>
        public void SetForecasts(Dictionary<string, string> map)
        {
            _forecastByMount = map;
        }

        /// <summary>查某个挂载点的标注，没有就返回空串（模板据此收起整行）。</summary>
        private string LookupForecast(string mount)
        {
            Dictionary<string, string> map = _forecastByMount;
            if (map == null || map.Count == 0) return string.Empty;

            string text;
            if (map.TryGetValue(DiskTrendStore.NormalizeMount(mount), out text)) return text;
            return string.Empty;
        }

        public void Apply(ServerSnapshot snapshot)
        {
            if (snapshot == null) return;

            IsOnline = snapshot.Online;
            ErrorText = snapshot.Error;
            Raise("HasError");
            _lastUpdated = snapshot.TimeUtc.ToLocalTime();
            Raise("LastUpdatedText");

            if (!snapshot.Online)
            {
                OverallLevel = HealthLevel.Offline;
                Raise("OverallLevelText");
                Raise("CpuLevel");
                Raise("MemLevel");
                Raise("DiskLevel");
                Raise("CpuLevelText");
                Raise("MemLevelText");
                Raise("DiskLevelText");
                // 离线时保留上一次的数值展示，让用户还能看到"掉线前是什么状态"
                return;
            }

            CpuPercent = snapshot.CpuPercent;
            MemPercent = snapshot.MemPercent;
            DiskPercent = snapshot.DiskPercent;

            Hostname = snapshot.Hostname;
            Kernel = snapshot.Kernel;
            CpuModel = snapshot.CpuModel != null ? snapshot.CpuModel.Trim() : null;
            UptimeText = Formats.Uptime(snapshot.Uptime);
            LoadText = snapshot.LoadAvg1 > 0
                ? snapshot.LoadAvg1.ToString("0.00")
                : "--";

            MemText = Formats.Bytes(snapshot.MemUsedBytes) + " / " + Formats.Bytes(snapshot.MemTotalBytes);
            DiskText = Formats.Bytes(snapshot.DiskUsedBytes) + " / " + Formats.Bytes(snapshot.DiskTotalBytes);

            Disks.Clear();
            TopDisks.Clear();
            foreach (DiskUsage disk in snapshot.Disks)
            {
                var row = new DiskRowViewModel
                {
                    Mount = disk.Mount,
                    FileSystem = disk.FileSystem,
                    Percent = disk.UsedPercent,
                    Level = Formats.Classify(disk.UsedPercent, _settings),
                    TotalBytes = disk.TotalBytes,
                    UsedBytes = disk.UsedBytes,
                    ForecastText = LookupForecast(disk.Mount)
                };
                Disks.Add(row);
                if (TopDisks.Count < VisibleDiskCount) TopDisks.Add(row);
            }

            Raise("MoreDisksText");
            Raise("MoreDisksTooltip");
            Raise("HasMoreDisks");

            AppendHistory(CpuHistory, snapshot.CpuPercent);
            AppendHistory(MemHistory, snapshot.MemPercent);
            AppendHistory(DiskHistory, snapshot.DiskPercent);

            OverallLevel = Formats.Worst(CpuLevel, MemLevel, DiskLevel);

            Raise("CpuLevel");
            Raise("MemLevel");
            Raise("DiskLevel");
            Raise("CpuLevelText");
            Raise("MemLevelText");
            Raise("DiskLevelText");
            Raise("OverallLevelText");
        }

        private void AppendHistory(ObservableCollection<double> target, double value)
        {
            target.Add(Math.Round(value, 1));

            int limit = _settings.SparklinePoints;
            while (target.Count > limit) target.RemoveAt(0);
        }
    }
}
