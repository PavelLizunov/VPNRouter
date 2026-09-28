#if !PLATFORM_WINDOWS
using System.Diagnostics;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Platform.macOS;

public class MacProcessMonitor : IProcessMonitor
{
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;

    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private HashSet<int> _lastPids = new();
    private bool _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public MacProcessMonitor(ILogger? logger = null, TimeSpan? pollInterval = null)
    {
        _logger = logger ?? Log.Logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        if (_pollThread != null) return;

        _logger.Information("[MacProcessMonitor] Starting polling monitor (interval: {Interval}ms)",
            _pollInterval.TotalMilliseconds);

        _cts = new CancellationTokenSource();

        _pollThread = new Thread(() => RunPolling(_cts.Token))
        {
            Name = "VPNRouter-MacPollMonitor",
            IsBackground = true
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _logger.Information("[MacProcessMonitor] Stopping");
        _cts?.Cancel();
        _pollThread = null;
        _cts = null;
    }

    private void RunPolling(CancellationToken ct)
    {
        try
        {
            _lastPids = TakeSnapshot().Keys.ToHashSet();
            _logger.Debug("[MacProcessMonitor] Initial snapshot: {Count} processes", _lastPids.Count);

            while (!ct.IsCancellationRequested)
            {
                Thread.Sleep(_pollInterval);
                if (ct.IsCancellationRequested) break;

                try
                {
                    var current = TakeSnapshot();
                    var currentPids = current.Keys.ToHashSet();

                    foreach (var pid in currentPids.Except(_lastPids))
                    {
                        if (current.TryGetValue(pid, out var info))
                        {
                            ProcessStarted?.Invoke(this, info);
                        }
                    }

                    foreach (var pid in _lastPids.Except(currentPids))
                    {
                        ProcessStopped?.Invoke(this, new ProcessEventArgs
                        {
                            ProcessId = pid,
                            ProcessName = string.Empty,
                            ParentProcessId = 0
                        });
                    }

                    _lastPids = currentPids;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.Warning(ex, "[MacProcessMonitor] Error during poll");
                }
            }
        }
        catch (ThreadInterruptedException) { }
        catch (Exception ex)
        {
            _logger.Error(ex, "[MacProcessMonitor] Poll loop failed");
        }
    }

    private Dictionary<int, ProcessEventArgs> TakeSnapshot()
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
                catch {  }
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
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
#endif
