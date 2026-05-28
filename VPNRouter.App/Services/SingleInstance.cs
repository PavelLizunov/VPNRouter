using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
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

    // 0x02 = "route this app through VPN" (Explorer context-menu verb,
    // v2.38.0). Wire format: [0x02][int32 little-endian length][UTF-8 path].
    // The path is the "%1" the shell verb hands us (an .exe or .lnk); the
    // running instance resolves + adds it to RoutingAppsInclude + toasts.
    private const byte SignalRouteApp = 0x02;

    // Sanity cap on the path payload so a malformed/hostile client can't make
    // us allocate an arbitrary buffer. MAX_PATH-era paths are <260 chars;
    // long-path UNC can reach ~32k chars → 64 KB UTF-8 is comfortably above.
    private const int MaxRouteAppPayloadBytes = 64 * 1024;

    private static Mutex? _mutex;
    private static CancellationTokenSource? _serverCts;

    /// <summary>
    /// Fired (on the Avalonia UI thread) when a second-instance launch
    /// has signalled this process to surface the main window. Subscribe
    /// from <c>App.OnFrameworkInitializationCompleted</c>.
    /// </summary>
    public static event Action? ShowWindowRequested;

    /// <summary>
    /// Fired (on the Avalonia UI thread) when a second-instance launch
    /// invoked <c>--route-app "&lt;path&gt;"</c> (the Explorer context-menu
    /// verb). The argument is the raw <c>%1</c> path (an <c>.exe</c> or
    /// <c>.lnk</c>); the handler resolves it to a process-name, adds it to
    /// the split-tunnel list and toasts. v2.38.0.
    /// </summary>
    public static event Action<string, string?>? RouteAppRequested;

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
            // v2.31.10-r1 — fix to v2.31.7-r2 mutex-not-owned bug. Pre-r1
            // we used `initiallyOwned: false` AND only called WaitOne(0)
            // in the `!createdNew` branch. That meant the FIRST instance
            // created the mutex but NEVER acquired it. The second
            // instance saw createdNew=false, called WaitOne(0), and it
            // returned true (mutex unowned!) — second instance fell
            // through to the "first instance" path and the original got
            // killed by OrphanCleanup. F-4 night-shift 2026-05-06.
            //
            // Fix: ALWAYS call WaitOne(0). It's the atomic acquisition
            // primitive — succeeds iff no other process owns the mutex.
            // The createdNew flag becomes useful only for diagnostic
            // logging (distinguishes fresh-create from inherit-existing).
            _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);

            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died without releasing. Per .NET docs,
                // ownership transfers to us anyway. Safe for our use
                // case (process-singleton); we don't share state via
                // the mutex itself.
                logger?.Information("[SingleInstance] previous owner abandoned the mutex — claiming ownership");
                acquired = true;
            }

            if (!acquired)
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
            logger?.Debug("[SingleInstance] acquired single-instance slot (createdNew={CreatedNew})", createdNew);
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

    /// <summary>
    /// Hand a route-app request to an already-running instance via the pipe.
    /// </summary>
    /// <returns><c>true</c> if a running instance received it (caller should
    /// exit); <c>false</c> if no instance is listening (caller is/will be the
    /// first instance and must process the path itself after startup).</returns>
    public static bool TrySendRouteAppToRunningInstance(string path, string? category = null, ILogger? logger = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(path ?? string.Empty);
            client.WriteByte(SignalRouteApp);
            client.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            client.Write(bytes, 0, bytes.Length);
            // r4: optional category payload — [int32 len][UTF-8 name]. Omitted
            // when routing to the default group; the server reads it as null.
            if (!string.IsNullOrWhiteSpace(category))
            {
                var catBytes = Encoding.UTF8.GetBytes(category);
                client.Write(BitConverter.GetBytes(catBytes.Length), 0, 4);
                client.Write(catBytes, 0, catBytes.Length);
            }
            client.Flush();
            logger?.Information("[SingleInstance] route-app handed to running instance: {Path} (cat={Cat})", path, category ?? "<default>");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[SingleInstance] no running instance for route-app (processing locally)");
            return false;
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes or return false.</summary>
    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
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
                else if (verb == SignalRouteApp)
                {
                    // [0x02][int32 len][UTF-8 path] then OPTIONALLY
                    // [int32 len][UTF-8 category] (r4). No category bytes (older
                    // shell verb / default group) → the stream ends → null.
                    var lenBuf = new byte[4];
                    if (ReadExact(server, lenBuf, 4))
                    {
                        int len = BitConverter.ToInt32(lenBuf, 0);
                        if (len > 0 && len <= MaxRouteAppPayloadBytes)
                        {
                            var pathBuf = new byte[len];
                            if (ReadExact(server, pathBuf, len))
                            {
                                var path = Encoding.UTF8.GetString(pathBuf);

                                // Optional trailing category payload.
                                string? category = null;
                                var catLenBuf = new byte[4];
                                if (ReadExact(server, catLenBuf, 4))
                                {
                                    int catLen = BitConverter.ToInt32(catLenBuf, 0);
                                    if (catLen > 0 && catLen <= MaxRouteAppPayloadBytes)
                                    {
                                        var catBuf = new byte[catLen];
                                        if (ReadExact(server, catBuf, catLen))
                                            category = Encoding.UTF8.GetString(catBuf);
                                    }
                                }

                                Dispatcher.UIThread.Post(() =>
                                {
                                    try { RouteAppRequested?.Invoke(path, category); }
                                    catch (Exception ex) { logger?.Warning(ex, "[SingleInstance] route-app handler threw"); }
                                });
                            }
                        }
                        else
                        {
                            logger?.Warning("[SingleInstance] route-app payload length {Len} out of range — ignoring", len);
                        }
                    }
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
