using System;
using System.Net;

namespace ServerMonitor.Services
{
    internal static class HostUtil
    {
        private static string _cachedMachineName;
        private static string[] _cachedLocalAddresses;

        /// <summary>
        /// 判断目标地址是否就是本机。
        ///
        /// 这件事必须提前判断，因为 WMI 有一条硬性限制：
        /// 连接本机时不允许指定用户名和口令，会直接返回
        /// WBEM_E_LOCAL_CREDENTIALS (0x80041064)。
        /// 与其让用户看到一个莫名的系统错误，不如提前说清楚。
        /// </summary>
        public static bool IsLocalHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            string value = host.Trim().ToLowerInvariant();

            // 去掉可能的端口后缀
            int colon = value.LastIndexOf(':');
            if (colon > 0 && value.IndexOf(']') < colon)
            {
                value = value.Substring(0, colon);
            }

            if (value == "127.0.0.1" || value == "localhost" ||
                value == "::1" || value == "." || value == "(local)")
            {
                return true;
            }

            if (value == GetMachineName()) return true;

            foreach (string address in GetLocalAddresses())
            {
                if (string.Equals(address, value, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string GetMachineName()
        {
            if (_cachedMachineName == null)
            {
                try { _cachedMachineName = Environment.MachineName.ToLowerInvariant(); }
                catch { _cachedMachineName = string.Empty; }
            }
            return _cachedMachineName;
        }

        private static string[] GetLocalAddresses()
        {
            if (_cachedLocalAddresses != null) return _cachedLocalAddresses;

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
                var list = new string[addresses.Length];
                for (int i = 0; i < addresses.Length; i++)
                {
                    list[i] = addresses[i].ToString();
                }
                _cachedLocalAddresses = list;
            }
            catch
            {
                // 取不到本机 IP 不影响判断，靠主机名和回环地址兜底
                _cachedLocalAddresses = new string[0];
            }

            return _cachedLocalAddresses;
        }
    }
}
