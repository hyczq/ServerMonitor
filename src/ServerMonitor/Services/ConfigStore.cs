using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 负责 servers.json / settings.json 的读写，以及口令的加密存储。
    /// 口令使用 Windows DPAPI（当前用户范围）加密，配置文件被拷走后无法解密。
    /// </summary>
    public sealed class ConfigStore
    {
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("ServerMonitor.v1.entropy");

        private readonly string _dataDirectory;
        private readonly string _serversPath;
        private readonly string _settingsPath;

        public List<ServerConfig> Servers { get; private set; }
        public AppSettings Settings { get; private set; }

        public string DataDirectory { get { return _dataDirectory; } }

        public ConfigStore(string dataDirectory)
        {
            _dataDirectory = dataDirectory;
            _serversPath = Path.Combine(dataDirectory, "servers.json");
            _settingsPath = Path.Combine(dataDirectory, "settings.json");
            Servers = new List<ServerConfig>();
            Settings = new AppSettings();
        }

        public void Load()
        {
            Directory.CreateDirectory(_dataDirectory);

            Servers = ReadJson<List<ServerConfig>>(_serversPath) ?? new List<ServerConfig>();
            Settings = ReadJson<AppSettings>(_settingsPath) ?? new AppSettings();
            Settings.Normalize();

            Logger.Debug("配置", "已读取 " + _serversPath + "（" + Servers.Count + " 台服务器）");
            Logger.Debug("配置", "已读取 " + _settingsPath);

            bool needsResave = false;
            foreach (ServerConfig server in Servers)
            {
                if (string.IsNullOrEmpty(server.Id))
                {
                    server.Id = Guid.NewGuid().ToString("N");
                    needsResave = true;
                }
                // 落盘的是密文，读出来要还原成明文供连接使用
                if (!string.IsNullOrEmpty(server.PasswordEncrypted))
                {
                    server.Password = Unprotect(server.PasswordEncrypted);
                }
            }

            if (needsResave) SaveServers();
        }

        public void SaveServers()
        {
            foreach (ServerConfig server in Servers)
            {
                server.PasswordEncrypted = Protect(server.Password);
            }

            try
            {
                WriteJson(_serversPath, Servers);
                Logger.Info("配置", "服务器列表已保存（" + Servers.Count + " 台）");
            }
            catch (Exception ex)
            {
                Logger.Error("配置", "服务器列表保存失败：" + ex.Message, ex);
                throw;
            }
        }

        public void SaveSettings()
        {
            Settings.Normalize();
            WriteJson(_settingsPath, Settings);
        }

        // ---------- DPAPI ----------

        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return null;
            try
            {
                byte[] cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch
            {
                // DPAPI 异常时宁可不保存口令，也不要明文落盘
                return null;
            }
        }

        private static string Unprotect(string cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64)) return null;
            try
            {
                byte[] cipher = Convert.FromBase64String(cipherBase64);
                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // 换了机器或换了用户，旧密文解不开——提示用户重新填写口令
                return null;
            }
        }

        // ---------- 文件读写 ----------

        private static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return null;
                return JsonConvert.DeserializeObject<T>(text);
            }
            catch
            {
                // 配置损坏时退回默认值，不阻断程序启动
                return null;
            }
        }

        private static void WriteJson(string path, object value)
        {
            string json = JsonConvert.SerializeObject(value, Formatting.Indented);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 先写临时文件再替换，避免断电/崩溃留下半截文件
            string temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
