#nullable enable
using System.Diagnostics;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Services;

/// <summary>
/// Cross-platform process monitor. A two-second snapshot interval is sufficient
/// because new process names are debounced for five seconds by HealthMonitor.
/// </summary>
public sealed class PollingProcessMonitor : IProcessMonitor
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<Dictionary<int, ProcessEventArgs>> _takeSnapshot;
    private readonly object _sync = new();

    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public PollingProcessMonitor(ILogger? logger = null, TimeSpan? pollInterval = null)
        : this(logger, pollInterval, TakeSnapshot)
    {
    }

    internal PollingProcessMonitor(
        ILogger? logger,
        TimeSpan? pollInterval,
        Func<Dictionary<int, ProcessEventArgs>> takeSnapshot)
    {
        _logger = logger ?? Log.Logger;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _takeSnapshot = takeSnapshot ?? throw new ArgumentNullException(nameof(takeSnapshot));
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pollThread is not null)
                return;

            _logger.Information("[ProcessMonitor] Starting polling monitor (interval: {Interval}ms)",
                _pollInterval.TotalMilliseconds);

            var cts = new CancellationTokenSource();
            var thread = new Thread(() => RunPolling(cts.Token))
            {
                Name = "VPNRouter-ProcessMonitor",
                IsBackground = true
            };

            _cts = cts;
            _pollThread = thread;
            thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;

        lock (_sync)
        {
            thread = _pollThread;
            cts = _cts;
            if (thread is null)
                return;

            cts?.Cancel();
        }

        var exited = thread == Thread.CurrentThread ||
                     !thread.IsAlive ||
                     thread.Join(StopTimeout);
        if (!exited)
        {
            _logger.Warning("[ProcessMonitor] Worker did not exit within {Timeout}ms", StopTimeout.TotalMilliseconds);
            return;
        }

        var ownsCleanup = false;
        lock (_sync)
        {
            if (ReferenceEquals(_pollThread, thread))
            {
                _pollThread = null;
                _cts = null;
                ownsCleanup = true;
            }
        }

        if (ownsCleanup)
            cts?.Dispose();

        _logger.Information("[ProcessMonitor] Stopped");
    }

    private void RunPolling(CancellationToken cancellationToken)
    {
        Dictionary<int, ProcessEventArgs>? previous = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = _takeSnapshot();
                if (previous is not null)
                    PublishChanges(previous, current);
                previous = current;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                _logger.Warning(ex, "[ProcessMonitor] Snapshot failed; retrying");
            }

            if (cancellationToken.IsCancellationRequested ||
                cancellationToken.WaitHandle.WaitOne(_pollInterval))
                break;
        }
    }

    private void PublishChanges(
        IReadOnlyDictionary<int, ProcessEventArgs> previous,
        IReadOnlyDictionary<int, ProcessEventArgs> current)
    {
        foreach (var (processId, process) in current)
        {
            if (!previous.ContainsKey(processId))
                Raise(ProcessStarted, process, "start");
        }

        foreach (var processId in previous.Keys)
        {
            if (!current.ContainsKey(processId))
            {
                Raise(ProcessStopped, new ProcessEventArgs { ProcessId = processId }, "stop");
            }
        }
    }

    private void Raise(
        EventHandler<ProcessEventArgs>? handler,
        ProcessEventArgs args,
        string eventName)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ProcessMonitor] Error in process {Event} handler", eventName);
        }
    }

    private static Dictionary<int, ProcessEventArgs> TakeSnapshot()
    {
        var result = new Dictionary<int, ProcessEventArgs>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    result[process.Id] = new ProcessEventArgs
                    {
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName,
                        ParentProcessId = 0
                    };
                }
                catch
                {
                    // The process may exit between enumeration and property access.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { }
            }
        }

        return result;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
    }
}
