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

    private readonly ILogger _logger;
    private readonly IProcessRunner _processRunner;
    private TraceEventSession? _session;
    private Thread? _sessionThread;
    private bool _disposed;

    // Signals that RunSession assigned _session, so a fast Start/Stop cannot skip session.Stop() and leave the worker blocked.
    private readonly ManualResetEventSlim _sessionReady = new(false);

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public EtwProcessMonitor(ILogger? logger = null, IProcessRunner? processRunner = null)
    {
        _logger = logger ?? Log.Logger;
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public void Start()
    {
        if (_session != null) return;

        _logger.Information("[ETW] Starting process monitor session");
        _sessionReady.Reset();

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
        // Capture references first; stopping the session unblocks Process(), then join with a bounded timeout.
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
            _sessionReady.Set();

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
            _sessionReady.Set();
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
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _sessionReady.Dispose(); } catch { }
    }
}
#endif
