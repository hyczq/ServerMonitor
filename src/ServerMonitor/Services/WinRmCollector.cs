using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using Newtonsoft.Json.Linq;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 通过 WinRM（PowerShell 远程）采集 Windows 指标。
    /// 适用于关闭了 WMI/DCOM 但按安全基线开启了 WinRM 的服务器。
    ///
    /// 注意：工作组环境下用本地账号连接，需要在 <b>本机</b> 执行一次
    ///   winrm set winrm/config/client @{TrustedHosts="目标IP"}
    /// 否则会报"信任关系"错误。程序不会自动改系统配置，只会在界面上给出提示。
    /// </summary>
    internal sealed class WinRmCollector : ICollector
    {
        /// <summary>
        /// 采集脚本与结果解析统一由 WindowsMetrics 提供。
        /// SSH 通道（Windows 目标机）复用同一份，避免两处各写一套后改漏。
        /// </summary>
        private const string Script = WindowsMetrics.PowerShellScript;

        public ServerSnapshot Collect(ServerConfig cfg, ServerSnapshot previous)
        {
            // 与 WMI 同理：WinRM 也没法用指定账号连自己
            if (HostUtil.IsLocalHost(cfg.Host) && !string.IsNullOrEmpty(cfg.Username))
            {
                throw new InvalidOperationException(
                    "WinRM 不允许对「本机」连接指定用户名和口令。" +
                    "监控本机请清空用户名和口令，或改填这台机器对外的内网 IP。");
            }

            var sw = Stopwatch.StartNew();
            var snapshot = new ServerSnapshot { ServerId = cfg.Id, Online = true };

            var uri = new Uri(string.Format("http://{0}:{1}/wsman",
                cfg.Host, cfg.EffectivePort));

            const string shellUri =
                "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";

            // 用户名为空 = 用当前登录身份。此时传 null 凭证，
            // 不能传空用户名的 PSCredential（构造时就会抛异常）。
            PSCredential credential = string.IsNullOrWhiteSpace(cfg.Username)
                ? null
                : new PSCredential(cfg.Username, ToSecureString(cfg.Password));

            var connection = new WSManConnectionInfo(uri, shellUri, credential)
            {
                AuthenticationMechanism = AuthenticationMechanism.Negotiate,
                OperationTimeout = 60000
            };

            string json;
            using (Runspace runspace = RunspaceFactory.CreateRunspace(connection))
            {
                runspace.Open();

                Logger.Debug("WinRM", "已连接 " + cfg.Host + ":" + cfg.EffectivePort +
                                      " 用户=" + (string.IsNullOrEmpty(cfg.Username)
                                          ? "(当前身份)" : cfg.Username));
                using (var shell = PowerShell.Create())
                {
                    shell.Runspace = runspace;
                    shell.AddScript(Script);

                    var output = shell.Invoke();

                    if (shell.HadErrors && shell.Streams.Error.Count > 0)
                    {
                        throw new InvalidOperationException(
                            shell.Streams.Error[0].ToString());
                    }

                    if (output.Count == 0)
                    {
                        throw new InvalidOperationException("远程脚本没有返回任何数据。");
                    }

                    json = output[output.Count - 1].ToString();
                }
            }

            // 远端脚本原文只在 trace 输出：它很长，日常级别下会把日志冲垮
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.Trace("WinRM", "远端返回的 JSON（" + json.Length + " 字符）：\n" + json);
            }

            WindowsMetrics.Parse(json, snapshot, cfg.DisplayName);

            sw.Stop();
            snapshot.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return snapshot;
        }

        // 结果解析（Parse / GetString / GetDouble / Clamp）已移到
        // WindowsMetrics，SSH 通道用的是同一份。

        private static SecureString ToSecureString(string plain)
        {
            var secure = new SecureString();
            if (!string.IsNullOrEmpty(plain))
            {
                foreach (char c in plain) secure.AppendChar(c);
            }
            secure.MakeReadOnly();
            return secure;
        }
    }
}
