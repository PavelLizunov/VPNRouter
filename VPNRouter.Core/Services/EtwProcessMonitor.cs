#if PLATFORM_WINDOWS
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Services;

public class EtwProcessMonitor : IProcessMonitor
{
    private const string SessionName = "VPNRouterETW";
    internal const int SessionBufferSizeMB = 4;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private TraceEventSession? _session;
    private Thread? _sessionThread;
    private bool _stopRequested;
    private bool _disposed;

    // Signals that RunSession assigned _session (or exited), so a fast Start/Stop cannot skip session.Stop().
    private readonly ManualResetEventSlim _sessionReady = new(false);

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public EtwProcessMonitor(ILogger? logger = null, IProcessRunner? processRunner = null)
    {
        _logger = logger ?? Log.Logger;
        _ = processRunner;
    }

    public void Start()
    {
        if (ProcessStarted == null && ProcessStopped == null)
        {
            _logger.Information("[ETW] No process event subscribers — skipping kernel ETW session");
            return;
        }

        lock (_gate)
        {
            if (_disposed || _session != null || _sessionThread != null) return;

            _logger.Information("[ETW] Starting process monitor session (buffer={BufferMB} MiB)", SessionBufferSizeMB);
            _stopRequested = false;
            _sessionReady.Reset();

            _sessionThread = new Thread(RunSession)
            {
                Name = "VPNRouter-ETW",
                IsBackground = true
            };
            _sessionThread.Start();
        }
    }

    public void Stop() => StopCore();

    private void StopCore()
    {
        Thread? thread;
        TraceEventSession? session;
        lock (_gate)
        {
            _stopRequested = true;
            thread = _sessionThread;
            session = _session;
            if (thread == null && session == null) return;
        }

        _logger.Information("[ETW] Stopping process monitor");
        if (session == null && thread != null)
        {
            try
            {
                if (!_sessionReady.Wait(TimeSpan.FromSeconds(1)))
                {
                    _logger.Warning("[ETW] session did not become ready within 1s — flagged for immediate stop");
                }
            }
            catch (ObjectDisposedException) { }
        }

        lock (_gate)
        {
            session = _session;
            _session = null;
            _sessionThread = null;
        }

        try { session?.Stop(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ETW] session.Stop threw — best-effort, continuing");
        }

        if (thread != null && thread.IsAlive)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                _logger.Warning("[ETW] worker thread didn't exit within 2s after Stop; leaving daemon");
            }
        }
    }

    private void RunSession()
    {
        TraceEventSession? ownedSession = null;
        try
        {
            lock (_gate)
            {
                if (_stopRequested || _disposed) return;
            }

            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                _logger.Warning("[ETW] Found orphaned session '{Name}', disposing", SessionName);
                using var old = new TraceEventSession(SessionName);
                old.Stop();
            }

            using var session = new TraceEventSession(SessionName)
            {
                BufferSizeMB = SessionBufferSizeMB
            };
            ownedSession = session;

            lock (_gate)
            {
                if (_stopRequested || _disposed) return;
                _session = session;
                _sessionReady.Set();
            }

            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process,
                KernelTraceEventParser.Keywords.None);

            session.Source.Kernel.ProcessStart += data =>
            {
                var handler = ProcessStarted;
                if (handler == null) return;
                try
                {
                    var args = TranslateProcessEvent(data.ProcessID, data.ImageFileName, data.ParentID);
                    handler.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[ETW] Error in ProcessStart handler");
                }
            };

            session.Source.Kernel.ProcessStop += data =>
            {
                var handler = ProcessStopped;
                if (handler == null) return;
                try
                {
                    var args = TranslateProcessEvent(data.ProcessID, data.ImageFileName, data.ParentID);
                    handler.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[ETW] Error in ProcessStop handler");
                }
            };

            _logger.Information("[ETW] Session active, listening for process events");
            session.Source.Process();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ETW] Session failed");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_session, ownedSession))
                    _session = null;
                if (ReferenceEquals(_sessionThread, Thread.CurrentThread))
                    _sessionThread = null;
                if (!_disposed)
                    _sessionReady.Set();
            }
        }
    }

    internal static ProcessEventArgs TranslateProcessEvent(int processId, string? imageFileName, int parentProcessId)
    {
        return new ProcessEventArgs
        {
            ProcessId = processId,
            ProcessName = imageFileName ?? string.Empty,
            ParentProcessId = parentProcessId
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopRequested = true;
        }
        StopCore();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _sessionReady.Dispose(); } catch { }
        }
    }
}
#endif
