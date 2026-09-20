using System;
using ServerMonitor.Models;
using ServerMonitor.Services;

namespace ServerMonitor.ViewModels
{
    /// <summary>新增 / 编辑服务器对话框的数据上下文。</summary>
    public class ServerEditViewModel : ObservableObject
    {
        private readonly ServerConfig _target;
        private readonly bool _isNew;

        private string _name;
        private string _host;
        private string _username;
        private string _password;
        private OsType _os;
        private ProtocolType _protocol;
        private int _port;
        private string _group;
        private bool _enabled;
        private string _validationMessage;
        private string _testResult;
        private bool _testSucceeded;
        private bool _isTesting;

        public ServerEditViewModel(ServerConfig config, bool isNew)
        {
            _target = config;
            _isNew = isNew;

            _name = config.Name;
            _host = config.Host;
            _username = config.Username;
            _password = config.Password;
            _os = config.Os;
            _protocol = config.Protocol;
            _port = config.Port;
            _group = config.Group;
            _enabled = config.Enabled;
        }

        public string Title { get { return _isNew ? "添加服务器" : "编辑服务器"; } }

        public string Name
        {
            get { return _name; }
            set { SetProperty(ref _name, value); }
        }

        public string Host
        {
            get { return _host; }
            set { if (SetProperty(ref _host, value)) Raise("LocalHostWarning"); }
        }

        public string Username
        {
            get { return _username; }
            set { if (SetProperty(ref _username, value)) Raise("LocalHostWarning"); }
        }

        /// <summary>
        /// 保存前的预警：地址是本机、却又填了用户名口令 —— 这个组合必然连不上。
        /// WMI 和 WinRM 都禁止对本地连接指定凭证，与其保存后对着
        /// 「没有足够的内存」这类误导报错发呆，不如现在就讲清楚。
        /// </summary>
        public string LocalHostWarning
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_host)) return null;
                if (string.IsNullOrWhiteSpace(_username)) return null;
                if (!HostUtil.IsLocalHost(_host)) return null;

                return "地址填的是本机，同时又指定了用户名和口令 —— 这个组合连不上。"
                     + "WMI 与 WinRM 都禁止对本地连接使用指定账号。"
                     + "监控本机请清空用户名和口令；要监控别的服务器，请填对方的内网 IP。";
            }
        }

        public string Password
        {
            get { return _password; }
            set { SetProperty(ref _password, value); }
        }

        /// <summary>选中的是第 0/1 项，供 ComboBox 直接双向绑定。</summary>
        public int OsIndex
        {
            get { return _os == OsType.Windows ? 1 : 0; }
            set
            {
                OsType next = value == 1 ? OsType.Windows : OsType.Linux;
                if (_os == next) return;
                _os = next;
                Raise();
                Raise("OsHint");
                Raise("ProtocolHint");
            }
        }

        public OsType Os
        {
            get { return _os; }
            set { OsIndex = value == OsType.Windows ? 1 : 0; }
        }

        public int ProtocolIndex
        {
            get { return (int)_protocol; }
            set
            {
                var next = (ProtocolType)value;
                if (_protocol == next) return;
                _protocol = next;
                Raise();
                Raise("ProtocolHint");
                Raise("PortHint");
            }
        }

        public ProtocolType Protocol
        {
            get { return _protocol; }
            set { ProtocolIndex = (int)value; }
        }

        public int Port
        {
            get { return _port; }
            set { SetProperty(ref _port, value); Raise("PortHint"); }
        }

        public string Group
        {
            get { return _group; }
            set { SetProperty(ref _group, value); }
        }

        public bool Enabled
        {
            get { return _enabled; }
            set { SetProperty(ref _enabled, value); }
        }

        public string ValidationMessage
        {
            get { return _validationMessage; }
            private set { SetProperty(ref _validationMessage, value); Raise("HasValidationMessage"); }
        }

        public bool HasValidationMessage
        {
            get { return !string.IsNullOrEmpty(_validationMessage); }
        }

        public string TestResult
        {
            get { return _testResult; }
            private set { SetProperty(ref _testResult, value); Raise("HasTestResult"); }
        }

        public bool HasTestResult
        {
            get { return !string.IsNullOrEmpty(_testResult); }
        }

        public bool TestSucceeded
        {
            get { return _testSucceeded; }
            private set { SetProperty(ref _testSucceeded, value); }
        }

        public bool IsTesting
        {
            get { return _isTesting; }
            set { SetProperty(ref _isTesting, value); Raise("IsNotTesting"); }
        }

        public bool IsNotTesting { get { return !_isTesting; } }

        public string OsHint
        {
            get
            {
                return _os == OsType.Windows
                    ? "Windows 服务器：默认走 WMI（需放行 135 端口与动态端口），也可改选 WinRM 或 SSH。"
                    : "Linux 服务器：走 SSH，需要目标开启 sshd 并允许口令登录。";
            }
        }

        public string ProtocolHint
        {
            get
            {
                switch (Protocol)
                {
                    case ProtocolType.Ssh:
                        return "SSH：通用性最好；Windows 需要已安装 OpenSSH 服务端。";
                    case ProtocolType.Wmi:
                        return "WMI：Windows 原生通道；账号需在目标机管理员组，且防火墙放行 WMI。";
                    case ProtocolType.WinRm:
                        return "WinRM：需目标执行过 Enable-PSRemoting；工作组环境还要在本机配置 TrustedHosts。";
                    default:
                        return _os == OsType.Windows
                            ? "自动：Windows 使用 WMI 通道。"
                            : "自动：Linux 使用 SSH 通道。";
                }
            }
        }

        public string PortHint
        {
            get
            {
                if (_port > 0) return "留空（填 0）表示使用默认端口。";
                switch (Protocol)
                {
                    case ProtocolType.Ssh: return "默认端口 22。";
                    case ProtocolType.WinRm: return "默认端口 5985。";
                    case ProtocolType.Wmi: return "WMI 走 DCOM，无需指定端口。";
                    default: return "留空表示使用默认端口。";
                }
            }
        }

        /// <summary>把界面上的值写回配置对象。校验不通过时返回 false 并给出原因。</summary>
        public bool TryCommit(out string error)
        {
            error = null;
            ValidationMessage = null;

            string host = (_host ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(host))
            {
                error = "请填写服务器的内网 IP 或主机名。";
                ValidationMessage = error;
                return false;
            }

            if (host.IndexOf(' ') >= 0)
            {
                error = "地址中不能包含空格。";
                ValidationMessage = error;
                return false;
            }

            if (_port < 0 || _port > 65535)
            {
                error = "端口号必须在 0 - 65535 之间。";
                ValidationMessage = error;
                return false;
            }

            string user = (_username ?? string.Empty).Trim();

            // WMI 需要"机器名\\账号"或"域\\账号"格式，本地账号直接用 .\ 前缀最省事
            if (_protocol == ProtocolType.Wmi && !string.IsNullOrEmpty(user) &&
                user.IndexOf('\\') < 0)
            {
                user = ".\\" + user;
            }

            _target.Name = string.IsNullOrWhiteSpace(_name) ? host : _name.Trim();
            _target.Host = host;
            // 留空写成 null 而不是空串：只有 null 才表示"用当前登录身份"
            _target.Username = string.IsNullOrWhiteSpace(user) ? null : user;
            _target.Password = _password;
            _target.Os = _os;
            _target.Protocol = _protocol;
            _target.Port = _port;
            _target.Group = string.IsNullOrWhiteSpace(_group) ? "默认" : _group.Trim();
            _target.Enabled = _enabled;

            return true;
        }

        /// <summary>用当前界面上的值构造一份临时配置，用于"测试连接"。</summary>
        public ServerConfig BuildProbe()
        {
            ServerConfig probe = _target.Clone();
            string user = (_username ?? string.Empty).Trim();
            if (_protocol == ProtocolType.Wmi && !string.IsNullOrEmpty(user) && user.IndexOf('\\') < 0)
            {
                user = ".\\" + user;
            }

            probe.Name = _name;
            probe.Host = (_host ?? string.Empty).Trim();
            probe.Username = user;
            probe.Password = _password;
            probe.Os = _os;
            probe.Protocol = _protocol;
            probe.Port = _port;
            return probe;
        }

        public void SetTestResult(bool success, string message)
        {
            TestSucceeded = success;
            TestResult = message;
        }
    }
}
