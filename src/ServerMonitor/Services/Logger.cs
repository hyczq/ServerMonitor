using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>一条日志，供界面展示用。</summary>
    public class LogEntry
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Scope { get; set; }
        public string Message { get; set; }

        public string TimeText
        {
            get { return Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture); }
        }

        public string LevelText
        {
            get { return Logger.LevelName(Level); }
        }

        /// <summary>形如 "web-01" 或空串，供界面按模块筛选。</summary>
        public string ScopeText
        {
            get { return Scope ?? string.Empty; }
        }
    }

    /// <summary>
    /// 应用诊断日志。
    ///
    /// 设计取舍：
    ///   · 静态类 + 无参调用。日志要能塞进任何地方，构造注入会把调用点搞得很啰嗦。
    ///   · 落盘即时 flush（AutoFlush）。崩溃时才不会丢掉最后几条——而那几条往往最关键。
    ///   · 同时在内存里保留一份环形缓冲，供界面实时查看，避免反复读文件。
    ///   · 记录口令长度而不是口令本身。这是刻意的：既够诊断（长度为 0 就是没传进去），
    ///     又不会把凭据写进日志文件。
    /// </summary>
    public static class Logger
    {
        private const int MemoryBufferSize = 2000;

        private static readonly object Gate = new object();
        private static readonly Queue<LogEntry> Buffer = new Queue<LogEntry>(MemoryBufferSize);

        private static string _directory;
        private static StreamWriter _writer;
        private static DateTime _writerDate = DateTime.MinValue;
        private static int _retentionDays = 14;
        private static volatile LogLevel _level = LogLevel.Info;
        private static volatile bool _started;

        /// <summary>有新日志时触发（可能来自任意线程，界面需自行调度回 UI 线程）。</summary>
        public static event EventHandler<LogEntry> EntryWritten;

        public static LogLevel Level
        {
            get { return _level; }
            set { _level = value; }
        }

        public static bool IsEnabled(LogLevel level)
        {
            return level <= _level;
        }

        public static string Directory
        {
            get { return _directory; }
        }

        public static string LevelName(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "ERROR";
                case LogLevel.Warn: return "WARN ";
                case LogLevel.Info: return "INFO ";
                case LogLevel.Debug: return "DEBUG";
                default: return "TRACE";
            }
        }

        public static LogLevel ParseLevel(string text, LogLevel fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            try
            {
                return (LogLevel)Enum.Parse(typeof(LogLevel), text.Trim(), true);
            }
            catch
            {
                return fallback;
            }
        }

        // ---------------------------------------------------------------
        // 初始化
        // ---------------------------------------------------------------

        /// <summary>在数据目录下建 logs 子目录并打开当天的日志文件。</summary>
        public static void Start(string dataDirectory, LogLevel level, int retentionDays)
        {
            _level = level;
            _retentionDays = retentionDays < 1 ? 1 : retentionDays;

            try
            {
                _directory = Path.Combine(dataDirectory, "logs");
                System.IO.Directory.CreateDirectory(_directory);
                _started = true;

                CleanupOldFiles();

                // 启动这条要能落盘，所以放在 Start 之后
                Info("日志系统已启动，级别=" + LevelName(_level) +
                     "，目录=" + _directory + "，保留 " + _retentionDays + " 天");
            }
            catch
            {
                // 日志系统起不来也不能影响主程序
                _started = false;
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_writer != null)
                {
                    try { _writer.Flush(); _writer.Dispose(); }
                    catch { }
                    _writer = null;
                }
                _started = false;
            }
        }

        // ---------------------------------------------------------------
        // 写日志
        // ---------------------------------------------------------------

        public static void Error(string message) { Write(LogLevel.Error, null, message, null); }
        public static void Error(string scope, string message) { Write(LogLevel.Error, scope, message, null); }
        public static void Error(string scope, string message, Exception ex) { Write(LogLevel.Error, scope, message, ex); }

        public static void Warn(string message) { Write(LogLevel.Warn, null, message, null); }
        public static void Warn(string scope, string message) { Write(LogLevel.Warn, scope, message, null); }
        public static void Warn(string scope, string message, Exception ex) { Write(LogLevel.Warn, scope, message, ex); }

        public static void Info(string message) { Write(LogLevel.Info, null, message, null); }
        public static void Info(string scope, string message) { Write(LogLevel.Info, scope, message, null); }

        public static void Debug(string message) { Write(LogLevel.Debug, null, message, null); }
        public static void Debug(string scope, string message) { Write(LogLevel.Debug, scope, message, null); }

        public static void Trace(string message) { Write(LogLevel.Trace, null, message, null); }
        public static void Trace(string scope, string message) { Write(LogLevel.Trace, scope, message, null); }

        /// <summary>
        /// 记录口令等敏感信息的安全写法：只记长度，不记内容。
        /// 长度就能判断"有没有传进去"——这正是排查认证类问题最需要的信息。
        /// </summary>
        public static string DescribeSecret(string secret)
        {
            if (secret == null) return "null";
            return secret.Length == 0 ? "空(0)" : "已设置(" + secret.Length + "位)";
        }

        private static void Write(LogLevel level, string scope, string message, Exception ex)
        {
            if (!IsEnabled(level)) return;

            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Scope = string.IsNullOrWhiteSpace(scope) ? null : scope,
                Message = message ?? string.Empty
            };

            // 异常堆栈的输出策略：
            //   Error —— 无条件输出。这是程序自身出问题，堆栈是定位缺陷的必需信息，
            //            不该因为用户把级别调成 info 就丢掉。
            //   其它级别 —— 只在 debug 及以上输出，否则持续性的采集失败（每轮一次）
            //            会用重复堆栈把日志冲垮。
            string stack = null;
            if (ex != null && (level == LogLevel.Error || IsEnabled(LogLevel.Debug)))
            {
                stack = ex.ToString();
            }

            AppendToBuffer(entry);
            AppendToFile(entry, stack);

            EventHandler<LogEntry> handler = EntryWritten;
            if (handler != null)
            {
                try { handler(null, entry); }
                catch { }
            }
        }

        private static void AppendToBuffer(LogEntry entry)
        {
            lock (Gate)
            {
                if (Buffer.Count >= MemoryBufferSize) Buffer.Dequeue();
                Buffer.Enqueue(entry);
            }
        }

        /// <summary>取当前内存中的日志快照，供界面展示。</summary>
        public static List<LogEntry> Snapshot()
        {
            lock (Gate)
            {
                return new List<LogEntry>(Buffer);
            }
        }

        /// <summary>
        /// 清空内存中的日志缓冲。
        /// 只影响界面显示，磁盘上的日志文件不动——那里是排查问题的依据，不该被界面操作抹掉。
        /// </summary>
        public static void ClearBuffer()
        {
            lock (Gate)
            {
                Buffer.Clear();
            }
        }

        private static void AppendToFile(LogEntry entry, string stack)
        {
            if (!_started) return;

            lock (Gate)
            {
                try
                {
                    EnsureWriter(entry.Time);

                    var line = new StringBuilder();
                    line.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    line.Append(" [").Append(LevelName(entry.Level)).Append(']');
                    line.Append(" [T").Append(
                        Thread.CurrentThread.ManagedThreadId.ToString("00", CultureInfo.InvariantCulture)).Append(']');

                    if (entry.Scope != null)
                    {
                        line.Append(" [").Append(entry.Scope).Append(']');
                    }

                    line.Append(' ').Append(entry.Message);
                    _writer.WriteLine(line.ToString());

                    if (!string.IsNullOrEmpty(stack))
                    {
                        // 堆栈按行缩进，和上一条日志在视觉上区分开
                        foreach (string raw in stack.Replace("\r\n", "\n").Split('\n'))
                        {
                            _writer.WriteLine("        " + raw);
                        }
                    }
                }
                catch
                {
                    // 写日志失败不能反过来把程序搞崩
                }
            }
        }

        /// <summary>跨天时换文件。</summary>
        private static void EnsureWriter(DateTime now)
        {
            if (_writer != null && _writerDate.Date == now.Date) return;

            if (_writer != null)
            {
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }

            string path = Path.Combine(_directory, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

            // UTF-8 带 BOM：记事本直接双击打开中文不乱码
            _writer = new StreamWriter(path, true, new UTF8Encoding(true))
            {
                AutoFlush = true
            };
            _writerDate = now;
        }

        /// <summary>删除超过保留期的日志文件。</summary>
        private static void CleanupOldFiles()
        {
            if (string.IsNullOrEmpty(_directory)) return;

            try
            {
                DateTime cutoff = DateTime.Today.AddDays(-_retentionDays);
                foreach (string file in System.IO.Directory.GetFiles(_directory, "*.log"))
                {
                    DateTime parsed;
                    if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file),
                            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    {
                        continue;
                    }
                    if (parsed < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>列出已有的日志文件，最新在前。</summary>
        public static List<string> ListFiles()
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(_directory) || !System.IO.Directory.Exists(_directory)) return result;

            try
            {
                var files = new List<string>(System.IO.Directory.GetFiles(_directory, "*.log"));
                files.Sort();
                files.Reverse();
                result.AddRange(files);
            }
            catch
            {
            }
            return result;
        }
    }
}
