using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace VPNRouter.App.Services;

/// <summary>
/// Single-instance enforcement for the GUI app. v2.31.7-r2.
///
/// <para>Pre-r2 we relied on <c>OrphanCleanup.KillOrphans</c> at startup to
/// kill any other <c>VPNRouter.App</c> processes. That worked, but had a
/// pathological UX: clicking the Start Menu / taskbar shortcut while the
/// app was already running (window minimized to tray, hidden behind other
/// windows, on a virtual desktop the user wasn't on) killed the existing
/// instance and started fresh. The fresh window didn't always reach
/// foreground (Windows ForegroundLockTimeout, focus-stealing prevention),
/// so the symptom was «I clicked the icon and nothing happened — and now
/// my VPN status reset». spark-wraith 2026-05-04: *«не открывается, не
/// показывается нигде, никак его не проконтролировать»*.</para>
///
/// <para>r2 replaces the brutal kill-and-restart with a Mutex + named-pipe
/// IPC pattern: the first instance acquires a system Mutex and listens on
/// a named pipe. Subsequent launches detect the held Mutex, send a
/// «show» message via the pipe, and exit silently. The first instance
/// brings its window to foreground in response. No process churn, no
/// state reset, window always reachable.</para>
///
/// <para>Cross-platform — Mutex on Windows uses kernel objects, on
/// Mac/Linux .NET 8 backs it with a file lock under <c>/tmp/.dotnet/</c>.
/// NamedPipeServerStream on Mac/Linux uses Unix domain sockets. The
/// behaviour is identical from the caller's perspective.</para>
/// </summary>
public static class SingleInstance
{
    // v2 suffix on the names — leave room to bump if we need a flag-day
    // change to the protocol later (e.g. send the requested action / args
    // through the pipe instead of a single byte).
    private const string MutexName = "Global\\VPNRouter.App.SingleInstance.v2";
    private const string PipeName = "VPNRouter.App.ShowWindow.v2";

    // 0x01 = "bring window to foreground". Reserved space for future verbs
    // (0x02 = "connect", 0x03 = "disconnect", etc.) without breaking the
    // wire protocol.
    private const byte SignalShowWindow = 0x01;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _serverCts;

    /// <summary>
    /// Fired (on the Avalonia UI thread) when a second-instance launch
    /// has signalled this process to surface the main window. Subscribe
    /// from <c>App.OnFrameworkInitializationCompleted</c>.
    /// </summary>
    public static event Action? ShowWindowRequested;

    /// <summary>
    /// Try to claim the single-instance slot. Call this BEFORE any
    /// expensive startup work so the second-instance path costs ~ms.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this is the first instance — caller should
    /// continue normal startup. <c>false</c> if another instance was
    /// already running and we signalled it; caller should exit
    /// immediately (we already disposed our Mutex handle).
    /// </returns>
    public static bool TryAcquireOrSignal(ILogger? logger = null)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);

            // createdNew=false means another process (or a previous run
            // that didn't release it) already holds the Mutex. Try to
            // acquire it without blocking — if WaitOne(0) returns false,
            // another LIVE instance is holding it.
            if (!createdNew && !_mutex.WaitOne(0))
            {
                logger?.Information("[SingleInstance] another instance detected — signalling it to surface");
                TrySignalShow(logger);
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            // We hold the Mutex. Start the pipe server so future
            // second-instance launches can reach us.
            _serverCts = new CancellationTokenSource();
            _ = Task.Run(() => RunPipeServerLoop(_serverCts.Token, logger));
            logger?.Debug("[SingleInstance] acquired single-instance slot");
            return true;
        }
        catch (Exception ex)
        {
            // Mutex creation can theoretically fail on Windows under
            // unusual SDDL configurations (e.g. some kiosk lockdowns).
            // Fall back to "this is the first instance" so the app
            // still runs — worst case is the pre-r2 OrphanCleanup
            // behaviour for THIS launch.
            logger?.Warning(ex, "[SingleInstance] mutex acquisition failed — falling back to single-instance off");
            return true;
        }
    }

    /// <summary>
    /// Release the Mutex on graceful shutdown so the next launch sees a
    /// clean slot. Idempotent.
    /// </summary>
    public static void Release()
    {
        try { _serverCts?.Cancel(); } catch { }
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // ReleaseMutex throws if we never acquired it (e.g. fallback
            // path from TryAcquireOrSignal exception). Safe to ignore.
        }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    private static void TrySignalShow(ILogger? logger)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // 2 s connect timeout — if the existing instance is utterly
            // hung (rare), we don't want to block the user's second
            // launch indefinitely. Better to exit silently than spin.
            client.Connect(2000);
            client.WriteByte(SignalShowWindow);
            client.Flush();
            logger?.Debug("[SingleInstance] sent show-window signal");
        }
        catch (Exception ex)
        {
            // Pipe doesn't exist / connect timed out / etc. The existing
            // instance might be a zombie holding the Mutex but no longer
            // running its server. Let the user's second launch exit
            // silently anyway — a third launch (or an explicit kill +
            // restart of the zombie via Task Manager) will fix it.
            logger?.Warning(ex, "[SingleInstance] failed to signal existing instance");
        }
    }

    private static void RunPipeServerLoop(CancellationToken ct, ILogger? logger)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1);

                // WaitForConnectionAsync respects the cancellation token —
                // graceful shutdown via Release().
                var connectTask = server.WaitForConnectionAsync(ct);
                connectTask.GetAwaiter().GetResult();

                if (ct.IsCancellationRequested) break;

                var verb = server.ReadByte();
                if (verb == SignalShowWindow)
                {
                    // Bounce onto the UI thread — handlers will touch
                    // Avalonia controls.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try { ShowWindowRequested?.Invoke(); }
                        catch (Exception ex) { logger?.Warning(ex, "[SingleInstance] show-window handler threw"); }
                    });
                }
                // Unknown verbs: silently ignored. Future-proofing for
                // protocol additions.
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Transient pipe error (e.g. client disconnected mid-handshake).
                // Sleep briefly then re-create the server to avoid a hot
                // error loop in the rare case the pipe layer is broken.
                logger?.Debug(ex, "[SingleInstance] pipe server iteration error");
                try { Thread.Sleep(200); } catch { }
            }
        }
    }
}
