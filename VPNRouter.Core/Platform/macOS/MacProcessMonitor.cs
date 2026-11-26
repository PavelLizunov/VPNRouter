#if !PLATFORM_WINDOWS
using System.Diagnostics;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Platform.macOS;

public class MacProcessMonitor : IProcessMonitor
{
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<Dictionary<int, ProcessEventArgs>> _takeSnapshot;
    private readonly object _gate = new();

    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public MacProcessMonitor(ILogger? logger = null, TimeSpan? pollInterval = null)
        : this(logger, pollInterval, TakeSnapshot)
    {
    }

    internal MacProcessMonitor(ILogger? logger, TimeSpan? pollInterval,
        Func<Dictionary<int, ProcessEventArgs>> takeSnapshot)
    {
        _logger = logger ?? Log.Logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _takeSnapshot = takeSnapshot;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _pollThread != null || (ProcessStarted == null && ProcessStopped == null)) return;

            _logger.Information("[MacProcessMonitor] Starting polling monitor (interval: {Interval}ms)",
                _pollInterval.TotalMilliseconds);

            var lifetime = new CancellationTokenSource();
            _cts = lifetime;
            _pollThread = new Thread(() => RunWorker(lifetime))
            {
                Name = "VPNRouter-MacPollMonitor",
                IsBackground = true
            };
            _pollThread.Start();
        }
    }

    public void Stop()
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _pollThread;
            _cts?.Cancel();
        }

        if (worker != null && worker != Thread.CurrentThread && !worker.Join(TimeSpan.FromSeconds(2)))
            _logger.Warning("[MacProcessMonitor] worker still stopping after 2 seconds");
    }

    private void RunWorker(CancellationTokenSource lifetime)
    {
        try
        {
            RunPolling(lifetime.Token);
        }
        catch (ThreadInterruptedException) { }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MacProcessMonitor] Poll loop failed");
        }
        finally
        {
            lock (_gate)
            {
                lifetime.Dispose();
                _cts = null;
                _pollThread = null;
            }
        }
    }

    private void RunPolling(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var lastPids = _takeSnapshot().Keys.ToHashSet();
        _logger.Debug("[MacProcessMonitor] Initial snapshot: {Count} processes", lastPids.Count);

        while (!ct.WaitHandle.WaitOne(_pollInterval))
        {
            try
            {
                var current = _takeSnapshot();
                if (ct.IsCancellationRequested) break;
                var currentPids = current.Keys.ToHashSet();

                if (ProcessStarted is { } onStart)
                {
                    foreach (var pid in currentPids.Except(lastPids))
                        onStart(this, current[pid]);
                }

                if (ProcessStopped is { } onStop)
                {
                    foreach (var pid in lastPids.Except(currentPids))
                    {
                        onStop(this, new ProcessEventArgs
                        {
                            ProcessId = pid,
                            ProcessName = string.Empty,
                            ParentProcessId = 0
                        });
                    }

                }

                lastPids = currentPids;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not ThreadInterruptedException)
            {
                _logger.Warning(ex, "[MacProcessMonitor] Error during poll");
            }
        }
    }

    private static Dictionary<int, ProcessEventArgs> TakeSnapshot()
    {
        var result = new Dictionary<int, ProcessEventArgs>();
        var procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    result[p.Id] = new ProcessEventArgs
                    {
                        ProcessId = p.Id,
                        ProcessName = p.ProcessName,
                        ParentProcessId = 0
                    };
                }
                catch { }
            }
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
#endif
