using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerMonitor.Models;

namespace ServerMonitor.Services
{
    /// <summary>
    /// 采集调度器：按设定间隔并发轮询所有启用的服务器，
    /// 每次采完一台就立刻通过 <see cref="SnapshotReady"/> 推给界面，无需等整轮结束。
    /// </summary>
    internal sealed class MonitorEngine : IDisposable
    {
        private readonly ConfigStore _config;
        private readonly HistoryStore _history;
        private readonly ConcurrentDictionary<string, ServerSnapshot> _latest =
            new ConcurrentDictionary<string, ServerSnapshot>(StringComparer.Ordinal);

        private CancellationTokenSource _cts;
        private Task _loop;
        private SemaphoreSlim _throttle;

        /// <summary>当前信号量对应的并发数，用于判断配置是否变过。</summary>
        private int _currentConcurrency;

        /// <summary>用于"立即刷新"，在等待间隔时提前唤醒。</summary>
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);

        /// <summary>
        /// 每台服务器上一次的失败原因，用于抑制重复堆栈。
        /// 采集成功后清除，这样故障再次出现时能重新拿到完整堆栈。
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _lastError =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public event EventHandler<ServerSnapshot> SnapshotReady;
        public event EventHandler CycleCompleted;

        public bool IsRunning { get { return _loop != null && !_loop.IsCompleted; } }

        public MonitorEngine(ConfigStore config, HistoryStore history)
        {
            _config = config;
            _history = history;
        }

        public ServerSnapshot GetLatest(string serverId)
        {
            ServerSnapshot snapshot;
            return _latest.TryGetValue(serverId, out snapshot) ? snapshot : null;
        }

        public void Start()
        {
            if (IsRunning) return;

            Logger.Info("采集调度", "启动 间隔=" + _config.Settings.RefreshSeconds +
                                    "秒 并发=" + _config.Settings.MaxConcurrency);
            _cts = new CancellationTokenSource();
            _currentConcurrency = _config.Settings.MaxConcurrency;
            _throttle = new SemaphoreSlim(_currentConcurrency, _currentConcurrency);
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (_cts == null) return;

            Logger.Info("采集调度", "正在停止");
            _cts.Cancel();
            _wake.Set();

            try
            {
                if (_loop != null && !_loop.Wait(TimeSpan.FromSeconds(10)))
                {
                    // 采集中途连不上目标时会拖很久，这里给出提示而不是无声等待
                    Logger.Warn("采集调度",
                        "等待采集循环结束超时（10 秒），可能有服务器仍在连接中");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("采集调度", "等待采集循环结束时出错：" + ex.Message);
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;

            // 下一次 Start 重建信号量，避免沿用可能已被打乱的计数
            if (_throttle != null)
            {
                _throttle.Dispose();
                _throttle = null;
                _currentConcurrency = 0;
            }
        }

        /// <summary>跳过剩余等待时间，立刻开始下一轮采集。</summary>
        public void RefreshNow()
        {
            _wake.Set();
        }

        /// <summary>丢弃某台服务器的缓存快照（删除或编辑服务器后调用）。</summary>
        public void Forget(string serverId)
        {
            ServerSnapshot removed;
            _latest.TryRemove(serverId, out removed);
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    List<ServerConfig> targets = _config.Servers
                        .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Host))
                        .ToList();

                    if (targets.Count > 0)
                    {
                        // 并发数变化时重建信号量（用户在设置里改过）
                        EnsureThrottleCapacity();

                        Logger.Debug("采集调度", "开始一轮采集，目标 " + targets.Count + " 台" +
                                                " 并发上限 " + _currentConcurrency);

                        Task[] tasks = targets
                            .Select(s => CollectOneAsync(s, token))
                            .ToArray();

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.Debug("采集调度", "没有启用的服务器，本轮跳过");
                    }

                    EventHandler completed = CycleCompleted;
                    if (completed != null) completed(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮异常不能让调度器停摆。但必须记下来——之前这里是空的
                    // catch，调度器每轮都失败也无人知晓。
                    Logger.Error("采集调度", "本轮采集出现未预期异常，调度器将继续运行", ex);
                }

                if (token.IsCancellationRequested) break;

                int intervalSeconds = _config.Settings.RefreshSeconds;
                _wake.Wait(TimeSpan.FromSeconds(intervalSeconds), token);
                _wake.Reset();
            }
        }

        /// <summary>
        /// 按当前配置准备并发信号量。
        ///
        /// 并发数改小/改大后必须真的换掉信号量——早期版本只在 null 时创建，
        /// 结果在设置里改「最大并发采集数」完全不生效（注释还写着会重建）。
        /// 这里替换是安全的：LoopAsync 已经 await 过上一轮的全部任务。
        /// </summary>
        private void EnsureThrottleCapacity()
        {
            int desired = _config.Settings.MaxConcurrency;
            if (_throttle != null && _currentConcurrency == desired) return;

            SemaphoreSlim previous = _throttle;
            _throttle = new SemaphoreSlim(desired, desired);
            _currentConcurrency = desired;

            if (previous != null)
            {
                previous.Dispose();
                Logger.Info("采集调度", "并发数已调整为 " + desired);
            }
        }

        private async Task CollectOneAsync(ServerConfig cfg, CancellationToken token)
        {
            SemaphoreSlim throttle = _throttle;
            if (throttle != null) await throttle.WaitAsync(token).ConfigureAwait(false);

            try
            {
                ServerSnapshot previous = GetLatest(cfg.Id);

                ServerSnapshot snapshot = await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        ServerSnapshot result = CollectorFactory.Create(cfg).Collect(cfg, previous);
                        sw.Stop();

                        // 恢复正常，清掉失败记录：故障再次出现时要能重新拿到完整堆栈
                        string ignored;
                        _lastError.TryRemove(cfg.Id, out ignored);

                        Logger.Debug(cfg.DisplayName,
                            "采集成功 耗时 " + sw.ElapsedMilliseconds + "ms" +
                            " CPU " + result.CpuPercent.ToString("0.0") + "%" +
                            " 内存 " + result.MemPercent.ToString("0.0") + "%" +
                            " 磁盘 " + result.DiskPercent.ToString("0.0") + "%");
                        return result;
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        string reason = ErrorDescriber.Describe(ex);

                        // 采集失败记 Warn 而不是 Error：这属于运维事实（目标下线、
                        // 口令过期、防火墙变了），不是程序自身出故障。真正的程序
                        // 缺陷走未处理异常那条路，记 Error。
                        //
                        // 同一个错误连续发生时只写一行摘要，不重复堆栈——
                        // 一台持续失败的机器每轮都写十几行堆栈，一天能刷出几万行，
                        // 真正的线索反而被淹掉。错误内容变了才重新输出堆栈。
                        string lastReason;
                        bool repeated = _lastError.TryGetValue(cfg.Id, out lastReason)
                                        && string.Equals(lastReason, reason, StringComparison.Ordinal);
                        _lastError[cfg.Id] = reason;

                        if (repeated)
                        {
                            Logger.Warn(cfg.DisplayName,
                                "采集失败 " + cfg.Host + " 耗时 " + sw.ElapsedMilliseconds +
                                "ms 原因=" + reason + "（与前次相同，堆栈已省略）");
                        }
                        else
                        {
                            Logger.Warn(cfg.DisplayName,
                                "采集失败 " + cfg.Host + " 耗时 " + sw.ElapsedMilliseconds +
                                "ms 原因=" + reason, ex);
                        }

                        return ServerSnapshot.Offline(cfg.Id, reason, 0);
                    }
                }, token).ConfigureAwait(false);

                // 端口/协议可能在采集期间被用户改过，丢弃过期结果
                if (cfg.Id != snapshot.ServerId) return;

                _latest[cfg.Id] = snapshot;
                _history.Record(snapshot);

                EventHandler<ServerSnapshot> handler = SnapshotReady;
                if (handler != null) handler(this, snapshot);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (throttle != null) throttle.Release();
            }
        }

        public void Dispose()
        {
            Stop();
            if (_throttle != null)
            {
                _throttle.Dispose();
                _throttle = null;
            }
            _wake.Dispose();
        }
    }
}
