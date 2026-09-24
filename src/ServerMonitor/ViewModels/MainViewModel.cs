using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using ServerMonitor.Converters;
using ServerMonitor.Models;
using ServerMonitor.Services;
using ServerMonitor.Themes;

namespace ServerMonitor.ViewModels
{
    public class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ConfigStore _config;
        private readonly HistoryStore _history;
        private readonly DiskTrendStore _diskTrend;
        private readonly MonitorEngine _engine;

        private string _searchText = string.Empty;
        private bool _showOnlyProblems;
        private bool _isCollecting;
        private string _lastCycleText = "等待首次采集";
        private ServerCardViewModel _selectedServer;
        private DateTime _lastSummaryUtc = DateTime.MinValue;

        // 概览指标
        private int _totalCount;
        private int _onlineCount;
        private int _warningCount;
        private int _criticalCount;
        private double _avgCpu;
        private double _avgMem;
        private double _avgDisk;
        private HealthLevel _fleetLevel = HealthLevel.Unknown;

        /// <summary>由 View 层注入：弹出服务器编辑对话框，返回 true 表示已保存。</summary>
        public Func<ServerConfig, bool> ShowServerEditor { get; set; }

        /// <summary>由 View 层注入：弹出确认框。</summary>
        public Func<string, string, bool> Confirm { get; set; }

        /// <summary>由 View 层注入：弹出提示。</summary>
        public Action<string, string> ShowMessage { get; set; }

        /// <summary>由 View 层注入：弹出保存文件对话框（巡检报告用）。</summary>
        public Func<string, string> AskSavePath { get; set; }

        private string _webhookStatus = "尚未推送";
        private bool _webhookBusy;

        /// <summary>最近一次 Webhook 推送的结果，显示在设置页。</summary>
        public string WebhookStatus
        {
            get { return _webhookStatus; }
            private set { SetProperty(ref _webhookStatus, value); }
        }

        public MainViewModel(ConfigStore config, HistoryStore history, DiskTrendStore diskTrend)
        {
            _config = config;
            _history = history;
            _diskTrend = diskTrend;

            Servers = new ObservableCollection<ServerCardViewModel>();
            foreach (ServerConfig server in config.Servers)
            {
                Servers.Add(new ServerCardViewModel(server, config.Settings));
            }

            ServersView = CollectionViewSource.GetDefaultView(Servers);
            ServersView.Filter = FilterServer;

            History = new HistoryViewModel(config, history);
            Logs = new LogViewModel();

            _engine = new MonitorEngine(config, history, diskTrend);
            _engine.SnapshotReady += OnSnapshotReady;
            _engine.CycleCompleted += OnCycleCompleted;

            AddServerCommand = new RelayCommand(AddServer);
            EditServerCommand = new RelayCommand(EditServer, p => p is ServerCardViewModel);
            DeleteServerCommand = new RelayCommand(DeleteServer, p => p is ServerCardViewModel);
            TestConnectionCommand = new RelayCommand(TestConnection, p => p is ServerCardViewModel);
            RefreshNowCommand = new RelayCommand(() => { _engine.RefreshNow(); IsCollecting = true; });
            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            ToggleEnabledCommand = new RelayCommand(ToggleEnabled, p => p is ServerCardViewModel);
            OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
            ExportReportCommand = new RelayCommand(ExportReport);
            TestWebhookCommand = new RelayCommand(TestWebhook, () => !_webhookBusy);
            OpenLogDirectoryCommand = new RelayCommand(OpenLogDirectory);
            TestAiCommand = new RelayCommand(TestAi);
            FetchAiModelsCommand = new RelayCommand(FetchAiModels);
            AiModels = new ObservableCollection<string>();

            RecomputeSummary();
        }

        // ---------- 集合与筛选 ----------

        public ObservableCollection<ServerCardViewModel> Servers { get; private set; }
        public ICollectionView ServersView { get; private set; }

        /// <summary>历史统计页的数据上下文。</summary>
        public HistoryViewModel History { get; private set; }

        /// <summary>诊断日志页的数据上下文。</summary>
        public LogViewModel Logs { get; private set; }

        public string SearchText
        {
            get { return _searchText; }
            set { if (SetProperty(ref _searchText, value)) ServersView.Refresh(); }
        }

        public bool ShowOnlyProblems
        {
            get { return _showOnlyProblems; }
            set { if (SetProperty(ref _showOnlyProblems, value)) ServersView.Refresh(); }
        }

        private bool FilterServer(object item)
        {
            var card = item as ServerCardViewModel;
            if (card == null) return false;

            if (_showOnlyProblems && card.OverallLevel != HealthLevel.Warning &&
                card.OverallLevel != HealthLevel.Critical)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_searchText)) return true;

            string needle = _searchText.Trim();
            return Contains(card.Name, needle)
                   || Contains(card.Host, needle)
                   || Contains(card.Config.Group, needle)
                   || Contains(card.OsLabel, needle);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------- 概览指标 ----------

        public int TotalCount { get { return _totalCount; } private set { SetProperty(ref _totalCount, value); } }
        public int OnlineCount { get { return _onlineCount; } private set { SetProperty(ref _onlineCount, value); } }
        public int WarningCount { get { return _warningCount; } private set { SetProperty(ref _warningCount, value); } }
        public int CriticalCount { get { return _criticalCount; } private set { SetProperty(ref _criticalCount, value); } }
        public double AvgCpu { get { return _avgCpu; } private set { SetProperty(ref _avgCpu, value); } }
        public double AvgMem { get { return _avgMem; } private set { SetProperty(ref _avgMem, value); } }
        public double AvgDisk { get { return _avgDisk; } private set { SetProperty(ref _avgDisk, value); } }

        public HealthLevel FleetLevel { get { return _fleetLevel; } private set { SetProperty(ref _fleetLevel, value); } }

        public string AvgCpuText { get { return Formats.PercentShort(_avgCpu); } }
        public string AvgMemText { get { return Formats.PercentShort(_avgMem); } }
        public string AvgDiskText { get { return Formats.PercentShort(_avgDisk); } }

        public string OfflineCountText
        {
            get
            {
                int offline = _totalCount - _onlineCount;
                return offline > 0 ? offline.ToString() : "0";
            }
        }

        public bool IsCollecting
        {
            get { return _isCollecting; }
            private set { SetProperty(ref _isCollecting, value); }
        }

        public string LastCycleText
        {
            get { return _lastCycleText; }
            private set { SetProperty(ref _lastCycleText, value); }
        }

        public ServerCardViewModel SelectedServer
        {
            get { return _selectedServer; }
            set { SetProperty(ref _selectedServer, value); }
        }

        // ---------- 设置 ----------

        public int RefreshSeconds
        {
            get { return _config.Settings.RefreshSeconds; }
            set
            {
                if (_config.Settings.RefreshSeconds == value) return;
                _config.Settings.RefreshSeconds = value;
                _config.SaveSettings();
                Raise();
                Raise("RefreshIntervalText");
            }
        }

        public string RefreshIntervalText
        {
            get
            {
                int seconds = _config.Settings.RefreshSeconds;
                return seconds >= 60 ? (seconds / 60) + " 分钟/次" : seconds + " 秒/次";
            }
        }

        public double WarnThreshold
        {
            get { return _config.Settings.WarnThreshold; }
            set
            {
                if (Math.Abs(_config.Settings.WarnThreshold - value) < 0.01) return;
                _config.Settings.WarnThreshold = value;
                _config.SaveSettings();
                Raise();
                OnThresholdsChanged();
            }
        }

        public double CriticalThreshold
        {
            get { return _config.Settings.CriticalThreshold; }
            set
            {
                if (Math.Abs(_config.Settings.CriticalThreshold - value) < 0.01) return;
                _config.Settings.CriticalThreshold = value;
                _config.SaveSettings();
                Raise();
                OnThresholdsChanged();
            }
        }

        public int RawRetentionDays
        {
            get { return _config.Settings.RawRetentionDays; }
            set
            {
                if (_config.Settings.RawRetentionDays == value) return;
                _config.Settings.RawRetentionDays = value;
                _config.SaveSettings();
                Raise();
                _history.PruneRawFiles(value);
            }
        }

        public int MaxConcurrency
        {
            get { return _config.Settings.MaxConcurrency; }
            set
            {
                if (_config.Settings.MaxConcurrency == value) return;
                _config.Settings.MaxConcurrency = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public bool DarkTheme
        {
            get { return _config.Settings.DarkTheme; }
            set
            {
                if (_config.Settings.DarkTheme == value) return;
                _config.Settings.DarkTheme = value;
                _config.SaveSettings();
                ThemeManager.Apply(value);
                Raise();
                Raise("ThemeIconKey");
            }
        }

        /// <summary>主题按钮显示的是"切换到另一个主题"的图标。</summary>
        public string ThemeIconKey
        {
            get { return _config.Settings.DarkTheme ? "IconSun" : "IconMoon"; }
        }

        public string DataDirectory { get { return _config.DataDirectory; } }

        // ---------- 命令 ----------

        public RelayCommand AddServerCommand { get; private set; }
        public RelayCommand EditServerCommand { get; private set; }
        public RelayCommand DeleteServerCommand { get; private set; }
        public RelayCommand TestConnectionCommand { get; private set; }
        public RelayCommand RefreshNowCommand { get; private set; }
        public RelayCommand ToggleThemeCommand { get; private set; }
        public RelayCommand ToggleEnabledCommand { get; private set; }
        public RelayCommand OpenDataDirectoryCommand { get; private set; }
        public RelayCommand ExportReportCommand { get; private set; }
        public RelayCommand TestWebhookCommand { get; private set; }
        public RelayCommand OpenLogDirectoryCommand { get; private set; }
        public RelayCommand TestAiCommand { get; private set; }
        public RelayCommand FetchAiModelsCommand { get; private set; }

        /// <summary>阈值示意图中"偏高"档的宽度占比（相对 100%）。</summary>
        public double WarnBandWidth
        {
            get { return Math.Max(0.5, _config.Settings.WarnThreshold); }
        }

        /// <summary>阈值示意图中"告警"档的宽度占比。</summary>
        public double CriticalBandWidth
        {
            get
            {
                return Math.Max(0.5, _config.Settings.CriticalThreshold -
                                      _config.Settings.WarnThreshold);
            }
        }

        /// <summary>阈值示意图中"严重"档的宽度占比。</summary>
        public double SevereBandWidth
        {
            get { return Math.Max(0.5, 100 - _config.Settings.CriticalThreshold); }
        }

        private void OpenDataDirectory()
        {
            try
            {
                System.Diagnostics.Process.Start(
                    "explorer.exe", "\"" + _config.DataDirectory + "\"");
            }
            catch (Exception ex)
            {
                if (ShowMessage != null) ShowMessage("无法打开目录", ex.Message);
            }
        }

        // ---------- AI ----------

        private string _aiStatus = "尚未测试";

        public bool AiEnabled
        {
            get { return _config.Settings.AiEnabled; }
            set
            {
                if (_config.Settings.AiEnabled == value) return;
                _config.Settings.AiEnabled = value;
                _config.SaveSettings();

                // Normalize 会顺手把摘要开关一起关掉，这里得通知界面
                // 重新取值，否则复选框还停在勾选状态，与配置不一致。
                Raise();
                Raise("AiSummaryEnabled");
                Logger.Info("AI", value ? "已启用" : "已停用");
            }
        }

        public string AiEndpoint
        {
            get { return _config.Settings.AiEndpoint; }
            set
            {
                if (_config.Settings.AiEndpoint == value) return;
                _config.Settings.AiEndpoint = value;
                _config.SaveSettings();
                Raise();
            }
        }

        /// <summary>
        /// 模型名。用可编辑下拉框：列表来自接口实际返回，也允许手填。
        /// 刻意不写死可选值——服务商改名的频率比程序发版高得多。
        ///
        /// 只更新内存不落盘：绑定是 PropertyChanged 的，每敲一个字符写一次磁盘太浪费。
        /// 落盘交给设置页的失焦与离开页面（PersistSettings）。
        /// </summary>
        public string AiModel
        {
            get { return _config.Settings.AiModel; }
            set
            {
                if (_config.Settings.AiModel == value) return;
                _config.Settings.AiModel = value;
                Raise();
            }
        }

        /// <summary>从接口拉到的可用模型名，供下拉框选择。</summary>
        public ObservableCollection<string> AiModels { get; private set; }

        public int AiTimeoutSeconds
        {
            get { return _config.Settings.AiTimeoutSeconds; }
            set
            {
                if (_config.Settings.AiTimeoutSeconds == value) return;
                _config.Settings.AiTimeoutSeconds = value;
                _config.SaveSettings();
                Raise();
            }
        }

        /// <summary>
        /// API 密钥。只存在内存里，落盘时由 ConfigStore 用 DPAPI 加密。
        /// 这里不直接存盘——PasswordBox 每敲一个字符都会触发同步，
        /// 每个字符写一次磁盘代价太大。由界面在失焦时调用 <see cref="PersistSettings"/>。
        /// </summary>
        public string AiApiKey
        {
            get { return _config.Settings.AiApiKey; }
            set
            {
                if (_config.Settings.AiApiKey == value) return;
                _config.Settings.AiApiKey = value;
                Raise();
            }
        }

        /// <summary>
        /// 用 AI 生成 Webhook 推送的正文。
        ///
        /// 只换文案，不参与告警判断：发不发仍由 Threshold 和 WebhookOnlyOnProblem 决定，
        /// 生成失败自动退回模板正文。所以这个开关最坏只会让消息难看，不会漏推。
        /// </summary>
        public bool AiSummaryEnabled
        {
            get { return _config.Settings.AiSummaryEnabled; }
            set
            {
                if (_config.Settings.AiSummaryEnabled == value) return;
                _config.Settings.AiSummaryEnabled = value;
                _config.SaveSettings();
                Raise();
                Logger.Info("AI", value ? "已开启告警摘要" : "已关闭告警摘要");
            }
        }

        /// <summary>最近一次连接测试的结果，显示在设置页。</summary>
        public string AiStatus
        {
            get { return _aiStatus; }
            private set { SetProperty(ref _aiStatus, value); }
        }

        /// <summary>把当前设置落盘（含 DPAPI 加密密钥）。由界面在失焦 / 离开页面时调用。</summary>
        public void PersistSettings()
        {
            _config.SaveSettings();
        }

        private async void TestAi()
        {
            AiStatus = "正在测试…";

            AppSettings settings = _config.Settings;

            if (string.IsNullOrWhiteSpace(settings.AiApiKey))
            {
                AiStatus = "请先填写 API 密钥";
                if (ShowMessage != null) ShowMessage("无法测试", "请先填写 API 密钥。");
                return;
            }

            AiReply reply = await Task.Run(() => AiClient.TestConnection(settings));

            if (reply.Success)
            {
                AiStatus = "连接正常（" + reply.ElapsedMs + "ms，返回：" +
                           Trim(reply.Content, 20) + "）";
                if (ShowMessage != null)
                {
                    ShowMessage("连接成功",
                        "接口、密钥、模型名三项均正常。\n\n" +
                        "模型：" + settings.AiModel + "\n" +
                        "耗时：" + reply.ElapsedMs + "ms\n" +
                        "Token：输入 " + reply.PromptTokens + " / 输出 " + reply.CompletionTokens +
                        "\n模型回复：" + Trim(reply.Content, 60));
                }
            }
            else
            {
                AiStatus = "连接失败：" + reply.Error;
                if (ShowMessage != null) ShowMessage("连接失败", reply.Error);
            }
        }

        /// <summary>Task.Run 里不好用 out 参数，用这个小结构带出来。</summary>
        private sealed class ModelListResult
        {
            public List<string> Models;
            public string Error;
        }

        private static ModelListResult FetchModels(AppSettings settings)
        {
            string error;
            List<string> models = AiClient.ListModels(settings, out error);
            return new ModelListResult { Models = models, Error = error };
        }

        private async void FetchAiModels()
        {
            AppSettings settings = _config.Settings;

            if (string.IsNullOrWhiteSpace(settings.AiApiKey))
            {
                AiStatus = "请先填写 API 密钥";
                if (ShowMessage != null) ShowMessage("无法获取", "请先填写 API 密钥。");
                return;
            }

            AiStatus = "正在获取模型列表…";

            ModelListResult result = await Task.Run(() => FetchModels(settings));

            if (result.Error != null)
            {
                AiStatus = "获取模型列表失败：" + result.Error;
                if (ShowMessage != null) ShowMessage("获取失败", result.Error);
                return;
            }

            AiModels.Clear();
            foreach (string model in result.Models) AiModels.Add(model);

            if (result.Models.Count == 0)
            {
                AiStatus = "接口没有返回任何模型";
                return;
            }

            // 当前填的模型名不在列表里时提示一下——多半是对方改名了，或者拼错了
            bool currentValid = result.Models.Exists(
                m => string.Equals(m, settings.AiModel, StringComparison.OrdinalIgnoreCase));

            AiStatus = currentValid
                ? "已获取 " + result.Models.Count + " 个模型，当前选择有效"
                : "已获取 " + result.Models.Count + " 个模型，当前填写的「" + settings.AiModel + "」不在其中";

            if (!currentValid && ShowMessage != null)
            {
                ShowMessage("模型列表已更新",
                    "接口返回 " + result.Models.Count + " 个可用模型：\n\n" +
                    string.Join("\n", result.Models.ToArray()) +
                    "\n\n当前填写的「" + settings.AiModel + "」不在其中，请从下拉框重新选择。");
            }
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(空)";
            text = text.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        // ---------- 日志 ----------

        /// <summary>供 ComboBox 绑定：0=Error 1=Warn 2=Info 3=Debug 4=Trace。</summary>
        public int LogLevelIndex
        {
            get { return (int)Logger.ParseLevel(_config.Settings.LogLevel, LogLevel.Info); }
            set
            {
                var next = (LogLevel)value;
                string name = next.ToString();
                if (_config.Settings.LogLevel == name) return;

                _config.Settings.LogLevel = name;
                _config.SaveSettings();
                Logger.Level = next;
                Raise();
                Raise("LogLevelDescription");

                // 改完级别立刻记一条：既确认已生效，也在日志里留下变更痕迹
                Logger.Info("设置", "日志级别已改为 " + Logger.LevelName(next).Trim() +
                                    "（" + LogLevelDescription + "）");
            }
        }

        /// <summary>当前级别的含义说明，直接显示在设置页，省得用户去猜。</summary>
        public string LogLevelDescription
        {
            get
            {
                switch ((LogLevel)LogLevelIndex)
                {
                    case LogLevel.Error: return "仅记录严重错误";
                    case LogLevel.Warn: return "错误 + 采集失败等警告";
                    case LogLevel.Info: return "再加上启动退出、服务器增删改等一般操作（推荐）";
                    case LogLevel.Debug: return "再加上每台服务器的采集耗时与结果";
                    default: return "全部日志，含远程命令原文与原始输出（排查疑难问题时用，日志量很大）";
                }
            }
        }

        public int LogRetentionDays
        {
            get { return _config.Settings.LogRetentionDays; }
            set
            {
                if (_config.Settings.LogRetentionDays == value) return;
                _config.Settings.LogRetentionDays = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public string LogDirectory
        {
            get { return Logger.Directory ?? "（日志系统未启动）"; }
        }

        private void OpenLogDirectory()
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

        // ---------- 巡检报告 ----------

        /// <summary>
        /// 把界面上的卡片状态拍成一份快照给报告/推送使用。
        ///
        /// 让报告层依赖这个中间结构而不是 ViewModel，是为了让报告格式与界面
        /// 各自独立演进——改界面不用动报告，改报告也不会碰界面代码。
        /// </summary>
        private List<ServerCardSnapshot> BuildSnapshots()
        {
            var list = new List<ServerCardSnapshot>();

            foreach (ServerCardViewModel card in Servers)
            {
                var snapshot = new ServerCardSnapshot
                {
                    Name = card.Name,
                    Host = card.AddressText,
                    OsLabel = card.OsLabel,
                    ProtocolLabel = card.ProtocolLabel,
                    Online = card.IsOnline,
                    Error = card.ErrorText,
                    Level = card.OverallLevel,
                    CpuPercent = card.CpuPercent,
                    MemPercent = card.MemPercent,
                    DiskPercent = card.DiskPercent,
                    MemText = card.MemText,
                    DiskText = card.DiskText,
                    UptimeText = card.UptimeText,
                    UpdatedText = card.LastUpdatedText
                };

                foreach (DiskRowViewModel disk in card.Disks)
                {
                    snapshot.Disks.Add(new DiskSnapshot
                    {
                        Mount = disk.Mount,
                        Percent = disk.Percent,
                        UsageText = disk.UsageText
                    });
                }

                list.Add(snapshot);
            }

            return list;
        }

        private void ExportReport()
        {
            if (Servers.Count == 0)
            {
                if (ShowMessage != null) ShowMessage("无法生成报告", "还没有配置任何服务器。");
                return;
            }

            string suggested = "服务器巡检报告_" +
                               DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt";
            string path = AskSavePath != null ? AskSavePath(suggested) : null;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                string text = InspectionReport.Build(BuildSnapshots(), _config.Settings,
                                                     _config.DataDirectory);

                // 带 BOM，记事本打开才不会把中文认成乱码
                File.WriteAllText(path, text, new UTF8Encoding(true));

                if (ShowMessage != null)
                {
                    ShowMessage("报告已生成",
                        "已生成 " + Servers.Count + " 台服务器的巡检报告：\n" + path);
                }
            }
            catch (Exception ex)
            {
                if (ShowMessage != null) ShowMessage("生成报告失败", ex.Message);
            }
        }

        // ---------- Webhook ----------

        public bool WebhookEnabled
        {
            get { return _config.Settings.WebhookEnabled; }
            set
            {
                if (_config.Settings.WebhookEnabled == value) return;
                _config.Settings.WebhookEnabled = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public string WebhookUrl
        {
            get { return _config.Settings.WebhookUrl; }
            set
            {
                if (_config.Settings.WebhookUrl == value) return;
                _config.Settings.WebhookUrl = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public string WebhookToken
        {
            get { return _config.Settings.WebhookToken; }
            set
            {
                if (_config.Settings.WebhookToken == value) return;
                _config.Settings.WebhookToken = value;
                _config.SaveSettings();
                Raise();
            }
        }

        /// <summary>PushPlus 的接口地址，切换渠道时用于自动填默认值。</summary>
        private const string PushPlusEndpoint = "https://www.pushplus.plus/send";

        /// <summary>供 ComboBox 绑定：0 = 通用 JSON，1 = PushPlus。</summary>
        public int WebhookProviderIndex
        {
            get { return (int)_config.Settings.WebhookProvider; }
            set
            {
                var next = (WebhookProvider)value;
                if (_config.Settings.WebhookProvider == next) return;
                _config.Settings.WebhookProvider = next;

                // 切到 PushPlus 且地址还空着时，自动填上官方的接口地址，
                // 省得用户去翻文档。已有内容不动。
                if (next == WebhookProvider.PushPlus &&
                    string.IsNullOrWhiteSpace(_config.Settings.WebhookUrl))
                {
                    _config.Settings.WebhookUrl = PushPlusEndpoint;
                    Raise("WebhookUrl");
                }

                _config.SaveSettings();
                Raise();
                Raise("IsPushPlus");
                Raise("WebhookTokenLabel");
                Raise("WebhookTokenHint");
                Raise("WebhookUrlHint");
            }
        }

        public bool IsPushPlus
        {
            get { return _config.Settings.WebhookProvider == WebhookProvider.PushPlus; }
        }

        public string WebhookTokenLabel
        {
            get { return IsPushPlus ? "PushPlus 令牌（token）" : "鉴权令牌（可留空）"; }
        }

        public string WebhookUrlHint
        {
            get
            {
                return IsPushPlus
                    ? "PushPlus 的接口地址。私有部署或走代理时改成自己的地址。"
                    : "例如 http://10.0.0.5:8080/api/server-status";
            }
        }

        public string WebhookTokenHint
        {
            get
            {
                return IsPushPlus
                    ? "在 pushplus 后台的「一对一消息」页面可以查到令牌。注意：该令牌以明文保存在 settings.json 中。"
                    : "会同时放在 Authorization 和 X-Monitor-Token 两个请求头里，接口用哪种都能取到。注意：该令牌以明文保存在 settings.json 中。";
            }
        }

        public string WebhookTitle
        {
            get { return _config.Settings.WebhookTitle; }
            set
            {
                if (_config.Settings.WebhookTitle == value) return;
                _config.Settings.WebhookTitle = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public int WebhookTemplateIndex
        {
            get
            {
                switch ((_config.Settings.WebhookTemplate ?? "txt").ToLowerInvariant())
                {
                    case "html": return 1;
                    case "markdown": return 2;
                    case "json": return 3;
                    default: return 0;
                }
            }
            set
            {
                string next;
                switch (value)
                {
                    case 1: next = "html"; break;
                    case 2: next = "markdown"; break;
                    case 3: next = "json"; break;
                    default: next = "txt"; break;
                }

                if (_config.Settings.WebhookTemplate == next) return;
                _config.Settings.WebhookTemplate = next;
                _config.SaveSettings();
                Raise();
            }
        }

        public string WebhookTopic
        {
            get { return _config.Settings.WebhookTopic; }
            set
            {
                if (_config.Settings.WebhookTopic == value) return;
                _config.Settings.WebhookTopic = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public bool WebhookOnlyOnProblem
        {
            get { return _config.Settings.WebhookOnlyOnProblem; }
            set
            {
                if (_config.Settings.WebhookOnlyOnProblem == value) return;
                _config.Settings.WebhookOnlyOnProblem = value;
                _config.SaveSettings();
                Raise();
            }
        }

        public bool WebhookIgnoreCertErrors
        {
            get { return _config.Settings.WebhookIgnoreCertErrors; }
            set
            {
                if (_config.Settings.WebhookIgnoreCertErrors == value) return;
                _config.Settings.WebhookIgnoreCertErrors = value;
                _config.SaveSettings();
                Raise();
            }
        }

        /// <summary>手动推一次，用于配置后立即验证接口是否通。</summary>
        private async void TestWebhook()
        {
            var snapshots = BuildSnapshots();
            string skip = WebhookNotifier.ShouldSkip(snapshots, _config.Settings);
            if (skip != null)
            {
                if (ShowMessage != null) ShowMessage("未推送", skip);
                return;
            }

            _webhookBusy = true;
            WebhookStatus = "正在推送…";

            string result = await SendWebhookAsync(snapshots);

            _webhookBusy = false;
            WebhookStatus = result;

            if (ShowMessage != null)
            {
                ShowMessage(result.StartsWith("推送成功", StringComparison.Ordinal)
                                ? "推送成功" : "推送失败",
                            result);
            }
        }

        /// <summary>采集完一轮后自动推送。失败只记录，不弹窗打扰。</summary>
        private void TrySendWebhook()
        {
            var snapshots = BuildSnapshots();
            if (WebhookNotifier.ShouldSkip(snapshots, _config.Settings) != null) return;

            // 网络请求放后台，别卡住界面。
            // 用弃元接住 Task：这里的"不等待"是刻意的——推送慢或失败都不该
            // 影响采集节奏，结果只写进 WebhookStatus 供设置页查看。
            _ = Task.Run(async () =>
            {
                string result = await SendWebhookAsync(snapshots);
                Application app = Application.Current;

                // BeginInvoke 的返回值是可等待对象，在 async 上下文里不接住会告警；
                // 这里本来就是投递到 UI 线程即返回，不需要等。
                if (app != null)
                {
                    _ = app.Dispatcher.BeginInvoke(new Action(() => WebhookStatus = result));
                }
            });
        }

        private Task<string> SendWebhookAsync(List<ServerCardSnapshot> snapshots)
        {
            return Task.Run(() =>
            {
                // 取配置的副本，避免推送过程中用户改设置导致读到半新半旧的值
                AppSettings settings = _config.Settings;

                // AI 摘要只是换一种写法：发不发上面已经判过了，这里失败也照样推送。
                // Compose 返回 null 表示生成失败（原因已写日志），退回模板正文。
                string aiSummary = settings.AiSummaryEnabled
                    ? AiSummary.Compose(snapshots, settings)
                    : null;

                string json = WebhookNotifier.BuildPayload(snapshots, settings, aiSummary);

                string error;
                bool ok = WebhookNotifier.Send(settings.WebhookUrl, json, settings, out error);

                string stamp = DateTime.Now.ToString("HH:mm:ss");

                // 把正文来源写进结果里，否则"AI 没生效"和"AI 生效了但内容差"
                // 这两种情况从界面上分不出来
                string source = aiSummary != null ? "AI 摘要" : "模板";

                if (ok)
                {
                    Logger.Info("Webhook", "推送成功 " + settings.WebhookProvider +
                                           " 渠道 正文=" + source +
                                           " 目标=" + settings.WebhookUrl +
                                           " 服务器数=" + snapshots.Count);
                    return "推送成功（" + stamp + "，" + snapshots.Count +
                           " 台服务器，正文：" + source + "）";
                }

                Logger.Warn("Webhook", "推送失败 目标=" + settings.WebhookUrl +
                                       " 原因=" + error);
                return "推送失败（" + stamp + "）：" + error;
            });
        }

        public void Start()
        {
            _engine.Start();
            IsCollecting = true;
            _history.PruneRawFiles(_config.Settings.RawRetentionDays);
        }

        private void ToggleTheme()
        {
            DarkTheme = !DarkTheme;
        }

        private void AddServer()
        {
            var draft = new ServerConfig { Name = "新服务器", Os = OsType.Linux };
            if (ShowServerEditor == null || !ShowServerEditor(draft)) return;

            _config.Servers.Add(draft);
            _config.SaveServers();
            var card = new ServerCardViewModel(draft, _config.Settings);
            Servers.Add(card);
            RecomputeSummary();
            _engine.RefreshNow();

            Logger.Info("服务器", "已添加 " + draft.DisplayName +
                                  " " + draft.Host +
                                  " 系统=" + draft.Os +
                                  " 通道=" + draft.ProtocolLabel);
        }

        private void EditServer(object parameter)
        {
            var card = parameter as ServerCardViewModel;
            if (card == null || ShowServerEditor == null) return;

            ServerConfig draft = card.Config.Clone();
            if (!ShowServerEditor(draft)) return;

            bool endpointChanged =
                !string.Equals(draft.Host, card.Config.Host, StringComparison.OrdinalIgnoreCase) ||
                draft.Port != card.Config.Port ||
                draft.Protocol != card.Config.Protocol ||
                draft.Os != card.Config.Os ||
                !string.Equals(draft.Username, card.Config.Username, StringComparison.Ordinal);

            // 就地覆盖原对象，避免引用错位
            card.Config.Name = draft.Name;
            card.Config.Host = draft.Host;
            card.Config.Port = draft.Port;
            card.Config.Username = draft.Username;
            card.Config.Password = draft.Password;
            card.Config.Os = draft.Os;
            card.Config.Protocol = draft.Protocol;
            card.Config.Group = draft.Group;
            card.Config.Enabled = draft.Enabled;

            _config.SaveServers();
            card.RefreshDerived();

            Logger.Info("服务器", "已修改 " + card.Config.DisplayName +
                                  " " + card.Config.Host +
                                  " 系统=" + card.Config.Os +
                                  " 通道=" + card.Config.ProtocolLabel +
                                  (endpointChanged ? "（连接参数有变动，已重新采集）" : string.Empty));

            if (endpointChanged)
            {
                _engine.Forget(card.Id);
                _engine.RefreshNow();
            }
        }

        private void DeleteServer(object parameter)
        {
            var card = parameter as ServerCardViewModel;
            if (card == null) return;

            if (Confirm != null &&
                !Confirm("删除服务器",
                         "确定要删除「" + card.Name + "」吗？\n该服务器的历史统计数据也会一并删除，且无法恢复。"))
            {
                return;
            }

            Logger.Info("服务器", "已删除 " + card.Config.DisplayName + " " + card.Config.Host);

            _engine.Forget(card.Id);
            _history.RemoveServer(card.Id);
            _diskTrend.RemoveServer(card.Id);
            _config.Servers.Remove(card.Config);
            _config.SaveServers();
            Servers.Remove(card);
            RecomputeSummary();
        }

        private void ToggleEnabled(object parameter)
        {
            var card = parameter as ServerCardViewModel;
            if (card == null) return;

            card.Config.Enabled = !card.Config.Enabled;
            _config.SaveServers();

            // Config 不会自己发通知，必须显式告诉界面重新读取
            card.NotifyConfigChanged();

            if (!card.Config.Enabled)
            {
                _engine.Forget(card.Id);
                card.Apply(ServerSnapshot.Offline(card.Id, "已停用", 0));
            }
            else
            {
                _engine.RefreshNow();
            }

            RecomputeSummary();
        }

        private async void TestConnection(object parameter)
        {
            var card = parameter as ServerCardViewModel;
            if (card == null) return;

            if (ShowMessage != null)
            {
                ShowMessage("连接测试", "正在连接「" + card.Name + "」…");
            }

            ServerConfig snapshotOfConfig = card.Config.Clone();
            string error = await TestAsync(snapshotOfConfig);

            if (ShowMessage != null)
            {
                if (error == null)
                {
                    ShowMessage("连接测试成功",
                        "已成功连接到 " + card.AddressText + "\n采集通道：" + card.ProtocolLabel);
                }
                else
                {
                    ShowMessage("连接测试失败", error);
                }
            }
        }

        public Task<string> TestAsync(ServerConfig cfg)
        {
            return Task.Run(() =>
            {
                string target = cfg.Host + (cfg.EffectivePort > 0
                    ? ":" + cfg.EffectivePort
                    : string.Empty);

                // 这行是排查认证类问题的关键：口令只记长度不记内容，
                // 但"空(0)"一眼就能看出是没传进去，而不是口令填错了。
                Logger.Info("测试连接",
                    "开始 " + target +
                    " 系统=" + cfg.Os +
                    " 通道=" + cfg.ProtocolLabel +
                    " 用户=" + (string.IsNullOrEmpty(cfg.Username) ? "(空)" : cfg.Username) +
                    " 口令=" + Logger.DescribeSecret(cfg.Password));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    CollectorFactory.Create(cfg).Collect(cfg, null);
                    sw.Stop();
                    Logger.Info("测试连接",
                        "成功 " + target + " 耗时 " + sw.ElapsedMilliseconds + "ms");
                    return null;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    string friendly = ErrorDescriber.Describe(ex);
                    Logger.Warn("测试连接",
                        "失败 " + target + " 耗时 " + sw.ElapsedMilliseconds + "ms 原因=" + friendly, ex);
                    return friendly;
                }
            });
        }

        // ---------- 引擎回调 ----------

        private void OnSnapshotReady(object sender, ServerSnapshot snapshot)
        {
            Application app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                ServerCardViewModel card = Servers.FirstOrDefault(
                    s => string.Equals(s.Id, snapshot.ServerId, StringComparison.Ordinal));
                if (card == null) return;

                // 趋势标注要在 Apply 之前下发：Apply 会重建磁盘行，行里的
                // ForecastText 只在重建那一刻查表，晚一步就要再等一轮采集
                card.SetForecasts(BuildForecastMap(card, snapshot));
                card.Apply(snapshot);

                // 汇总指标节流到每 2 秒算一次，避免几十台服务器时频繁全量重算
                if ((DateTime.UtcNow - _lastSummaryUtc).TotalSeconds >= 2)
                {
                    _lastSummaryUtc = DateTime.UtcNow;
                    RecomputeSummary();
                }
            }));
        }

        /// <summary>
        /// 算出某台服务器每个挂载点的趋势标注文案。
        ///
        /// 外推基准用实时快照的当前用量，拟合只用已完成的天——今天的值还在涨，
        /// 放进拟合会让斜率全天抬升、午夜归零（见 DiskTrendStore.GetServerSeries）。
        /// </summary>
        private Dictionary<string, string> BuildForecastMap(ServerCardViewModel card, ServerSnapshot snapshot)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!_config.Settings.DiskForecastEnabled) return map;
            if (snapshot == null || !snapshot.Online || snapshot.Disks == null) return map;

            Dictionary<string, List<DiskTrendPoint>> series = _diskTrend.GetServerSeries(card.Id);
            if (series.Count == 0) return map;

            foreach (DiskUsage disk in snapshot.Disks)
            {
                string key = DiskTrendStore.NormalizeMount(disk.Mount);
                if (key.Length == 0) continue;

                List<DiskTrendPoint> points;
                if (!series.TryGetValue(key, out points)) continue;

                DiskForecastResult result = DiskForecast.Evaluate(
                    points, disk.UsedBytes, disk.TotalBytes, _config.Settings);

                // 每一步判断都留痕：这既是"为什么说 25 天"的唯一答案，
                // 也是排查"为什么这个分区没有标注"的第一手线索。
                Logger.Debug("磁盘预测",
                    "服务器=" + card.Name +
                    " 分区=" + key +
                    " 有效天=" + result.DaysUsed +
                    " 增速=" + result.BytesPerDay.ToString("0", CultureInfo.InvariantCulture) + "B/天" +
                    " R2=" + result.R2.ToString("0.000", CultureInfo.InvariantCulture) +
                    " 预计=" + (double.IsNaN(result.DaysToFull)
                        ? "-"
                        : result.DaysToFull.ToString("0.0", CultureInfo.InvariantCulture) + "天") +
                    " 级别=" + result.Level +
                    " 原因=" + (result.Reason.Length == 0 ? "采用" : result.Reason));

                if (result.Text.Length > 0) map[key] = result.Text;
            }

            return map;
        }

        private void OnCycleCompleted(object sender, EventArgs e)
        {
            Application app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsCollecting = false;
                LastCycleText = DateTime.Now.ToString("HH:mm:ss") + " 完成一轮采集";
                RecomputeSummary();
                ServersView.Refresh();

                // 一轮采集结束后按配置推送，失败只在设置页留痕，不弹窗打扰
                TrySendWebhook();
            }));
        }

        private void OnThresholdsChanged()
        {
            foreach (ServerCardViewModel card in Servers)
            {
                card.RefreshDerived();

                // 磁盘行的等级也要跟着阈值重算
                foreach (DiskRowViewModel disk in card.Disks)
                {
                    disk.Level = Formats.Classify(disk.Percent, _config.Settings);
                }
            }
            Raise("WarnBandWidth");
            Raise("CriticalBandWidth");
            Raise("SevereBandWidth");

            ServersView.Refresh();
            RecomputeSummary();
        }

        public void RecomputeSummary()
        {
            TotalCount = Servers.Count;
            OnlineCount = Servers.Count(s => s.IsOnline);
            WarningCount = Servers.Count(s => s.OverallLevel == HealthLevel.Warning);
            CriticalCount = Servers.Count(s => s.OverallLevel == HealthLevel.Critical);

            List<ServerCardViewModel> online = Servers.Where(s => s.IsOnline).ToList();
            if (online.Count > 0)
            {
                AvgCpu = online.Average(s => s.CpuPercent);
                AvgMem = online.Average(s => s.MemPercent);
                AvgDisk = online.Average(s => s.DiskPercent);
            }
            else
            {
                AvgCpu = AvgMem = AvgDisk = 0;
            }

            HealthLevel fleet = HealthLevel.Good;
            if (TotalCount == 0) fleet = HealthLevel.Unknown;
            else if (CriticalCount > 0) fleet = HealthLevel.Critical;
            else if (WarningCount > 0) fleet = HealthLevel.Warning;
            else if (OnlineCount < TotalCount) fleet = HealthLevel.Warning;

            FleetLevel = fleet;

            Raise("AvgCpuText");
            Raise("AvgMemText");
            Raise("AvgDiskText");
            Raise("OfflineCountText");
            Raise("FleetLevelText");
        }

        public string FleetLevelText
        {
            get
            {
                switch (_fleetLevel)
                {
                    case HealthLevel.Good: return "全部正常";
                    case HealthLevel.Warning: return "存在偏高项";
                    case HealthLevel.Critical: return "存在告警项";
                    default: return "尚未采集";
                }
            }
        }

        public void Dispose()
        {
            _engine.SnapshotReady -= OnSnapshotReady;
            _engine.CycleCompleted -= OnCycleCompleted;
            _engine.Dispose();
            _diskTrend.Flush();
            _history.Flush();
        }
    }
}
