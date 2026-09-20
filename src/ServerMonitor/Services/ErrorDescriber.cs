using System;
using System.Management;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet.Common;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 把底层异常翻译成能直接照着排查的中文提示。
    /// 采集失败时用户看到的应该是"该做什么"，而不是一串英文堆栈。
    /// </summary>
    internal static class ErrorDescriber
    {
        public static string Describe(Exception ex)
        {
            if (ex == null) return "未知错误";

            // 内层异常往往才是真正的原因（SSH.NET / WMI 都会层层包装）
            Exception root = ex;
            int guard = 0;
            while (root.InnerException != null && guard++ < 6)
            {
                root = root.InnerException;
            }

            string message = root.Message ?? string.Empty;

            // ---- WMI/DCOM 的两条经典误导性报错 ----
            //
            // 它们的原文跟真实原因毫无关系，必须先于类型判断拦下来，
            // 否则用户会照着字面意思去查内存、查网络，白费半天功夫。

            // "没有足够的内存继续执行程序" / E_OUTOFMEMORY —— 实际是权限问题
            if (Contains(message, "没有足够的内存") ||
                Contains(message, "Not enough memory") ||
                Contains(message, "insufficient memory") ||
                Contains(message, "内存不足"))
            {
                return "WMI 拒绝了这次连接。\n"
                     + "注意：这个「没有足够的内存」与内存无关，是 WMI/DCOM 权限问题的典型误导报错。\n"
                     + "请依次检查：\n"
                     + "1) 账号是否在目标机的管理员组内；\n"
                     + "2) 目标机是否已放行 WMI 防火墙（135 端口 + RPC 动态端口）；\n"
                     + "3) 目标机的 DCOM 是否授予该账号「远程访问」和「远程激活」权限；\n"
                     + "4) 若监控的就是本机，用户名和口令必须留空。";
            }

            // WBEM_E_LOCAL_CREDENTIALS / "用户凭据不能用于本地连接"
            if (Contains(message, "本地连接") || Contains(message, "local connection"))
            {
                return "WMI 不允许对「本机」连接指定用户名和口令。\n"
                     + "监控这台机器自己：请把用户名和口令都留空（用当前登录身份采集）；\n"
                     + "监控别的服务器：请在地址里填写那台机器的内网 IP，而不是 127.0.0.1。";
            }

            // ---- SSH ----
            if (root is SshAuthenticationException)
                return "认证失败：用户名或口令不正确（也可能是目标禁用了口令登录）";

            if (root is SshConnectionException)
            {
                string sshText = message;

                if (Contains(sshText, "protocol version exchange") ||
                    Contains(sshText, "connection was closed"))
                {
                    return "目标端口不是 SSH 服务：连接建立后被立即关闭。"
                         + "请确认这个地址的端口上跑的确实是 sshd。";
                }
                if (Contains(sshText, "refused"))
                {
                    return "连接被拒绝：目标没有开放该端口，或 SSH 服务未启动。";
                }
                return "SSH 连接失败：" + Clean(sshText);
            }

            if (root is SocketException)
                return DescribeSocket((SocketException)root);

            // ---- WMI / DCOM ----
            if (root is UnauthorizedAccessException)
            {
                return "拒绝访问：账号口令错误，或该账号没有远程管理权限（需加入管理员组）";
            }

            var managementException = root as ManagementException;
            if (managementException != null)
            {
                string wmiMessage = message;
                int code = (int)managementException.ErrorCode;

                // WBEM_E_LOCAL_CREDENTIALS：本机连接不能带凭证
                if (code == unchecked((int)0x80041064))
                {
                    return "WMI 不允许对「本机」连接指定用户名和口令。\n"
                         + "监控这台机器自己请清空用户名和口令；监控别的机器请填对方的内网 IP。";
                }

                // WBEM_E_ACCESS_DENIED：WMI 自己的命名空间 ACL 拒绝了。
                // 这与 DCOM 层的 E_ACCESSDENIED 是两回事，修法也完全不同——
                // 这个要去 wmimgmt.msc 里给 Root\CIMV2 加权限。
                if (code == unchecked((int)0x80041003))
                {
                    return "WMI 命名空间权限不足（WBEM_E_ACCESS_DENIED 0x80041003）。\n"
                         + "认证已经通过，是 Root\\CIMV2 命名空间的安全描述符拒绝了该账号。\n"
                         + "请在目标机上运行 wmimgmt.msc → 右键「WMI 控制(本地)」→ 属性 → 安全 →\n"
                         + "展开 Root 选中 CIMV2 → 安全 → 添加该账号并勾选「远程启用」。";
                }

                // E_OUTOFMEMORY 出现在 WMI 上基本都意味着权限被拒
                if (code == unchecked((int)0x8007000E))
                {
                    return "WMI 拒绝了这次连接（报错文字会写「没有足够的内存」，但那与内存无关，"
                         + "是 DCOM 权限问题的典型误导信息）。请检查账号权限与目标机 WMI 防火墙。";
                }

                if (wmiMessage.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0 ||
                    wmiMessage.IndexOf("Access is denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code == unchecked((int)0x80070005))
                {
                    // 附带 HRESULT，便于排查时区分到底是哪一类拒绝
                    return "WMI 拒绝访问（HRESULT 0x" + code.ToString("X8") + "）。"
                         + "请依次确认：账号在目标机管理员组内、目标机已设置 "
                         + "LocalAccountTokenFilterPolicy=1、DCOM 授予了远程访问/激活权限、"
                         + "以及账号未被锁定。";
                }
                if (wmiMessage.IndexOf("RPC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "RPC 服务器不可用：目标防火墙可能拦截了 WMI（需放行 135 端口及动态端口）";
                }
                return "WMI 查询失败：" + wmiMessage;
            }

            if (ex is TimeoutException || root is TimeoutException)
                return "连接超时：请检查内网连通性与防火墙规则";

            // 参数类异常多半是配置没填全（典型：口令为空），给出可操作的提示
            if (root is ArgumentNullException || root is ArgumentException)
            {
                return "连接参数不完整：" + Trim(message, 120) +
                       "。请检查该服务器的用户名与口令是否已填写。";
            }

            // ---- 网络 ----
            if (message.IndexOf("No such host", StringComparison.OrdinalIgnoreCase) >= 0)
                return "无法解析主机名：请改用内网 IP 填写";

            if (message.IndexOf("TrustedHosts", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("信任", StringComparison.Ordinal) >= 0)
            {
                return "WinRM 信任关系未建立：请在本机管理员命令行执行 " +
                       "winrm set winrm/config/client @{TrustedHosts=\"目标IP\"}";
            }

            return Trim(message, 160);
        }

        private static string DescribeSocket(SocketException ex)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return "端口未开放：目标未运行对应服务（SSH 22 / WinRM 5985）";
                case SocketError.TimedOut:
                    return "连接超时：目标不可达，或被防火墙丢包";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return "网络不可达：请确认内网路由与 IP 是否正确";
                case SocketError.ConnectionReset:
                    return "连接被重置：可能被防火墙拦截，或服务未就绪";
                default:
                    return "网络错误：" + ex.Message;
            }
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 去掉异常原文里的链接和"More information on…"尾巴。
        /// 这些内容对排查没帮助，只会把卡片上的提示撑得很长。
        /// </summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "未知错误";

            int cut = text.IndexOf("More information", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) text = text.Substring(0, cut);

            cut = text.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) text = text.Substring(0, cut);

            cut = text.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (cut > 0) text = text.Substring(0, cut);

            return Trim(text, 120);
        }

        private static string Trim(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "未知错误";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "…";
        }
    }
}
