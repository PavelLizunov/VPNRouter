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
    private static readonly SemaphoreSlim KernelSessionGate = new(1);

    private readonly ILogger _logger;
    private readonly Func<IProcessTrace> _startTrace;
    private readonly object _gate = new();
    private IProcessTrace? _session;
    private Thread? _sessionThread;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;

    public EtwProcessMonitor(ILogger? logger = null, IProcessRunner? processRunner = null)
    {
        _logger = logger ?? Log.Logger;
        _startTrace = StartTrace;
        _ = processRunner;
    }

    internal EtwProcessMonitor(Func<IProcessTrace> startTrace)
    {
        _logger = Log.Logger;
        _startTrace = startTrace;
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
            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _sessionThread = new Thread(() => RunSession(lifetime))
            {
                Name = "VPNRouter-ETW",
                IsBackground = true
            };
            _sessionThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        IProcessTrace? session;
        lock (_gate)
        {
            _lifetime?.Cancel();
            thread = _sessionThread;
            session = _session;
            _session = null;
        }
        if (thread == null) return;

        _logger.Information("[ETW] Stopping process monitor");
        try { session?.Stop(); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[ETW] session.Stop threw — best-effort, continuing");
        }

        if (thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(2)))
            _logger.Warning("[ETW] worker still stopping after 2 seconds");
    }

    private void RunSession(CancellationTokenSource lifetime)
    {
        var ownsKernelSession = false;
        try
        {
            // The kernel session name is shared by old and replacement monitors during failover.
            KernelSessionGate.Wait(lifetime.Token);
            ownsKernelSession = true;
            if (lifetime.IsCancellationRequested) return;

            using var session = _startTrace();
            lock (_gate)
            {
                // Provider setup owns the session until it can actually be stopped.
                if (lifetime.IsCancellationRequested || _disposed) return;
                _session = session;
            }

            _logger.Information("[ETW] Session active, listening for process events");
            session.Process();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error(ex, "[ETW] Session failed");
        }
        finally
        {
            if (ownsKernelSession)
                KernelSessionGate.Release();
            lock (_gate)
            {
                lifetime.Dispose();
                _lifetime = null;
                _session = null;
                _sessionThread = null;
            }
        }
    }

    private IProcessTrace StartTrace()
    {
        if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
        {
            _logger.Warning("[ETW] Found orphaned session '{Name}', disposing", SessionName);
            using var old = new TraceEventSession(SessionName);
            old.Stop();
        }

        var session = new TraceEventSession(SessionName) { BufferSizeMB = SessionBufferSizeMB };
        try
        {
            session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.Process,
                KernelTraceEventParser.Keywords.None);

            var source = session.Source;
            source.Kernel.ProcessStart += data =>
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

            source.Kernel.ProcessStop += data =>
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

            return new KernelProcessTrace(session, source);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal interface IProcessTrace : IDisposable
    {
        void Process();
        void Stop();
    }

    private sealed class KernelProcessTrace(TraceEventSession session, ETWTraceEventSource source) : IProcessTrace
    {
        public void Process() => source.Process();
        public void Stop() => session.Stop();
        public void Dispose() => session.Dispose();
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
            _disposed = true;
        }
        Stop();
    }
}
#endif
