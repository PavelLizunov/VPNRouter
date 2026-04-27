#if PLATFORM_WINDOWS
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Serilog;
using VPNRouter.Core.Interfaces;

namespace VPNRouter.Core.Services;

/// <summary>
/// Real-time process monitor using ETW (Event Tracing for Windows).
/// Detects process start/stop in under 10ms vs WMI's 500ms+.
/// Requires administrator privileges.
/// </summary>
public class EtwProcessMonitor : IProcessMonitor
{
    private const string SessionName = "VPNRouterETW";

    private readonly ILogger _logger;
    private TraceEventSession? _session;
    private Thread? _sessionThread;
    private bool _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public EtwProcessMonitor(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void Start()
    {
        if (_session != null) return;

        _logger.Information("[ETW] Starting process monitor session");

        _sessionThread = new Thread(RunSession)
        {
            Name = "VPNRouter-ETW",
            IsBackground = true
        };
        _sessionThread.Start();
    }

    public void Stop()
    {
        _logger.Information("[ETW] Stopping process monitor");
        // v2.28.5-r2: capture references first so the thread we're trying to
        // join doesn't see them re-nulled mid-shutdown. Stop() the session
        // (this is what unblocks session.Source.Process() in the worker
        // thread). Then Join with a short timeout so callers don't hang
        // forever if the kernel ETW source is wedged. Skip Dispose here —
        // RunSession's `using var session` already disposes deterministically
        // when Process() returns, and a second Dispose can throw on the
        // already-finalised session in some TraceEvent versions.
        var thread = _sessionThread;
        var session = _session;
        _session = null;
        _sessionThread = null;

        try { session?.Stop(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ETW] session.Stop threw — best-effort, continuing");
        }

        if (thread != null && thread.IsAlive)
        {
            // ~2 s is enough for kernel ETW Process() to unblock after Stop.
            // If it still hasn't exited, leave it as a daemon (IsBackground=true)
            // — the runtime will tear it down on app exit.
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                _logger.Warning("[ETW] worker thread didn't exit within 2s after Stop; leaving daemon");
            }
        }
    }

    private void RunSession()
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                _logger.Warning("[ETW] Found orphaned session '{Name}', disposing", SessionName);
                using var old = new TraceEventSession(SessionName);
                old.Stop();
            }

            using var session = new TraceEventSession(SessionName);
            _session = session;

            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process,
                KernelTraceEventParser.Keywords.None);

            session.Source.Kernel.ProcessStart += data =>
            {
                try
                {
                    ProcessStarted?.Invoke(this, new ProcessEventArgs
                    {
                        ProcessId = data.ProcessID,
                        ProcessName = data.ImageFileName,
                        ParentProcessId = data.ParentID
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[ETW] Error in ProcessStart handler");
                }
            };

            session.Source.Kernel.ProcessStop += data =>
            {
                try
                {
                    ProcessStopped?.Invoke(this, new ProcessEventArgs
                    {
                        ProcessId = data.ProcessID,
                        ProcessName = data.ImageFileName,
                        ParentProcessId = data.ParentID
                    });
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
#endif
