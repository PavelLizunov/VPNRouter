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
    // v3.0 Phase 2D (2026-05-17): IProcessRunner injection prepared for
    // Phase 2G migration of any future Process-based diagnostics paths
    // (e.g. tasklist / wmic spot-checks) we might add to this class.
    // Today EtwProcessMonitor doesn't shell out, but holding the seam
    // here means future test-coverage doesn't require a second ctor
    // breaking change. See plans/phase2-2D-iprocessrunner-2026-05-17.md.
    private readonly IProcessRunner _processRunner;
    private TraceEventSession? _session;
    private Thread? _sessionThread;
    private bool _disposed;

    /// <summary>v2.31.0-r1 (CO-6 audit fix): signals when RunSession has
    /// finished assigning <see cref="_session"/>. Without it, a fast
    /// Start()→Stop() race could read <c>_session=null</c> in Stop's
    /// snapshot and skip <c>session.Stop()</c>, leaving the worker thread
    /// blocked on <c>Source.Process()</c> forever (the using-disposal
    /// won't run until Process returns, and Process returns only when
    /// the session is stopped — classic deadlock).</summary>
    private readonly ManualResetEventSlim _sessionReady = new(false);

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public EtwProcessMonitor(ILogger? logger = null, IProcessRunner? processRunner = null)
    {
        _logger = logger ?? Log.Logger;
        // v3.0 Phase 2D: default to real ProcessRunner so existing call
        // sites (`new EtwProcessMonitor(logger)`) keep working without DI.
        _processRunner = processRunner ?? new ProcessRunner();
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
        //
        // v2.31.0-r1 (CO-6 audit fix): wait briefly for RunSession to have
        // finished its `_session = session` assignment. Pre-fix, a fast
        // Stop() right after Start() could read _session=null and skip the
        // session.Stop() call entirely, leaving the worker thread blocked
        // on Source.Process() forever. The wait is bounded (1s) so we
        // don't hang if the session genuinely never came up.
        var thread = _sessionThread;
        if (!_sessionReady.Wait(TimeSpan.FromSeconds(1)))
        {
            _logger.Warning("[ETW] session never became ready within 1s — skipping Stop");
            _sessionThread = null;
            return;
        }
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
            _sessionReady.Set(); // CO-6: notify Stop() that _session is now non-null

            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process,
                KernelTraceEventParser.Keywords.None);

            session.Source.Kernel.ProcessStart += data =>
            {
                try
                {
                    var args = TranslateProcessEvent(data.ProcessID, data.ImageFileName, data.ParentID);
                    ProcessStarted?.Invoke(this, args);
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
                    var args = TranslateProcessEvent(data.ProcessID, data.ImageFileName, data.ParentID);
                    ProcessStopped?.Invoke(this, args);
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
            // v2.31.0-r1 (CO-6): signal even on failure so Stop()'s wait
            // returns immediately instead of timing out at 1s. _session
            // will be null at this point so Stop will short-circuit.
            _sessionReady.Set();
        }
    }

    /// <summary>
    /// v3.0 Phase 2G (sub-wave 7b-1, 2026-05-18): translate the raw fields
    /// from a <see cref="Microsoft.Diagnostics.Tracing.TraceEvent"/> into
    /// the cross-platform <see cref="ProcessEventArgs"/> shape. Extracted
    /// from the inline lambdas inside <see cref="RunSession"/> so the
    /// translation can be unit-tested without spinning up a real ETW
    /// session (which requires Admin + a live process emitting events).
    ///
    /// Defensive normalisation: ETW occasionally surfaces transient
    /// process slots with PID 0 (idle/system) or negative PID (deleted /
    /// pre-fork transient) — pass them through as-is so callers can
    /// decide whether to filter. ImageFileName from the kernel can be
    /// null in rare corner cases (early process slot, partial event);
    /// normalise to empty string so downstream consumers don't NRE.
    /// </summary>
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
        if (_disposed) return;
        _disposed = true;
        Stop();
        // v2.31.1-r1 (AU-9 follow-up): _sessionReady is a
        // ManualResetEventSlim and lazily allocates a kernel WaitHandle on
        // the first Wait(timeout). Pre-fix the handle leaked once per app
        // lifetime (small, but still a real leak when the monitor is
        // recycled by tests or future hot-reload paths).
        try { _sessionReady.Dispose(); } catch { /* defensive */ }
    }
}
#endif
