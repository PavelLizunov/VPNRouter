using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    private void OnProcessExited()
    {
        // v2.31.0-r1 (CO-8 audit fix): the previous catch { } empty
        // block swallowed any failure to read ExitCode — but the
        // failure cause (process handle disposed, race with Stop, etc.)
        // never reached the log. Worse, `exitCode == 0` and "couldn't
        // read" both fell into the same null-display branch on the
        // user-visible error path. Now we log the cause so post-mortems
        // can distinguish "exited cleanly" vs "exit info unavailable".
        //
        // Phase 3+ (2026-05-21): IProcessHandle.Exited fires with the int
        // code directly; we still attempt a snapshot-style read here for
        // backcompat with the legacy log shape, but the WaitForExitAsync
        // path (used by the immediate kill-then-wait sequences) already
        // surfaces the code through its return value. Since this callback
        // doesn't receive the exit code as a parameter (we wired the
        // adapter as `(_, _) => OnProcessExited()` to preserve the
        // legacy signature), we re-fetch from the handle.
        int? exitCode = null;
        Exception? exitCodeError = null;
        try
        {
            if (_handle is { HasExited: true } h)
            {
                // WaitForExitAsync on an already-exited handle returns
                // synchronously with the cached exit code.
                exitCode = h.WaitForExitAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            exitCodeError = ex;
        }

        // v2.37.0-r52 (ekko 2026-05-25 routing-flip suppression) + v2.41.2-r4
        // (2026-06-09 reconnect-stop suppression): if an intentional teardown
        // is in flight — either a Restart (_restartInProgress) OR a plain Stop
        // such as the GUI server-switch ReconnectAsync path (_stopInProgress) —
        // AND the exit code is the Windows-Kill signal (-1), SIGKILL (137) or
        // SIGTERM (143), then the OS Exited callback just lost its race against
        // SuppressExitedEvent. Don't fire Crashed — that would trigger
        // HealthMonitor's backoff restart loop on top of the teardown we're
        // already doing (ekko's "10-15s no internet on routing_mode flip";
        // Pavel's 2026-06-09 redundant-restart on every server switch). Log as
        // INF so the suppression is auditable.
        //
        // Genuine sing-box FATALs (TUN-orphan, bad config) exit with code
        // 1, NOT -1/137/143 — those still flow through the Crashed event
        // normally and get the HealthMonitor recovery treatment (see ekko
        // log 2026-05-26 08:37 where exit code 1 + AutoFailover did its job
        // correctly). And the Kill-signal codes only suppress WHEN a teardown
        // is in flight: a Task-Manager kill (-1) or OOM-kill (137) during a
        // steady-state run leaves both flags false → still a crash → recover.
        if ((_restartInProgress || _stopInProgress) && (exitCode == -1 || exitCode == 137 || exitCode == 143))
        {
            _logger.Information(
                "[SingBoxManager] Expected exit during intentional {Phase:l} (exit code: {Code}) — suppressing Crashed event, late OS callback after SuppressExitedEvent",
                _restartInProgress ? "restart" : "stop",
                exitCode);
            // Still need to clean up handle state — fall through to the
            // existing _capturedStderr scan + TunOrphan detection (those
            // are safe no-ops on intentional exit), but skip Crashed.Invoke
            // and the post-crash adapter cleanup below: Restart() does its own
            // LaunchProcess (PreStartCleanupAsync) and Stop()'s StopInternal
            // finally runs its own DisableOrphanedAdapter — so the adapter is
            // covered either way.
            LogSingBoxCrashTail();
            DetectTunOrphanCrashSignature();
            return;
        }

        if (exitCode == 0)
        {
            _logger.Warning("[SingBoxManager] sing-box exited unexpectedly (exit code 0) — will attempt restart");
        }
        else if (exitCode.HasValue)
        {
            _logger.Error("[SingBoxManager] sing-box crashed (exit code: {Code})", exitCode.Value);
        }
        else
        {
            _logger.Error(exitCodeError,
                "[SingBoxManager] sing-box exited but ExitCode could not be read ({ErrType})",
                exitCodeError?.GetType().Name ?? "no exception");
        }

        // v2.31.6-r20 — self-diagnosing crash. Pre-r20 we had to ask the
        // user to copy %ProgramData%\VPNRouter\logs\singbox.log every time
        // a crash happened on their machine, then root-cause from there.
        // Now we read the tail of singbox.log into vpnrouter.log right at
        // the crash boundary so the next log dump the user sends already
        // contains the relevant sing-box context. Best-effort; never throws.
        LogSingBoxCrashTail();

        // PinkuDani Fix #3 (2026-05-21): scan the captured stderr ring
        // buffer for the TUN-orphan crash signature. Set BEFORE the
        // Crashed event fires so HealthMonitor's auto-restart loop (which
        // subscribes to Crashed) observes the flag in time for its
        // AttemptRestart continuation. Best-effort; never throws — buffer
        // is small, scan is O(50 lines × small constant).
        DetectTunOrphanCrashSignature();

        State = SingBoxState.Failed;
        Crashed?.Invoke(this, EventArgs.Empty);

        // v2.30.1-r5 + hotfix 2026-05-19: aggressive cleanup of the
        // orphaned wintun adapter after silent crash. User report
        // 2026-05-01: "у пользователя периодически не убивается сетевой
        // интерфейс и ему приходится перезагружать Windows". When
        // sing-box dies via Windows TerminateProcess (e.g. on
        // wake-from-sleep), it doesn't get a chance to release the
        // wintun handle cleanly. The adapter hangs around in netsh
        // inventory holding the default routes and DNS settings, so
        // the user's network stays "stuck".
        //
        // Step 1 (sync): disable via netsh — frees the kernel handle
        // so Windows drops the routes immediately.
        // Step 2 (fire-and-forget): kick off Remove-NetAdapter on a
        // background Task so the device record itself goes away. By
        // the time HealthMonitor.AttemptRestart fires its
        // SingBoxManager.Restart() call (5-10 s of exponential backoff
        // later), the device record should be gone — NOT just disabled.
        // Pre-hotfix, only the disable ran; the next sing-box
        // WintunCreateAdapter then hit ERROR_FILE_EXISTS and FATAL'd
        // (alicemoren1991 log 2026-05-19, restart-loop reproduction).
        //
        // OnProcessExited is a sync void called from the Process.Exited
        // event on a threadpool thread, so we can't await directly.
        // Task.Run( ... .ContinueWith( ... )) gives us the fire-and-
        // forget pattern without blocking the event callback, and the
        // exception-swallowing ContinueWith ensures an async failure
        // can never crash the host (Process.Exited handler exceptions
        // would propagate to AppDomain.UnhandledException otherwise).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // The interface name is set in ConfigGenerator from
                // settings.Tun.InterfaceName which defaults to
                // "VPNRouter-TUN". Hard-coding the default here keeps
                // the SingBoxManager API surface unchanged (it knows
                // only SingBoxSettings, not AppSettings.Tun); on the
                // off-chance a user customised it, the netsh disable
                // simply returns "not found" and we skip the cleanup
                // — the worst case is the same orphan-adapter problem
                // the user already sees today.
                TunAdapterDiagnostics.DisableOrphanedAdapter(
                    _logger, DefaultTunInterfaceName, "SingBoxManager.OnProcessExited");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                            _logger, DefaultTunInterfaceName,
                            "SingBoxManager.OnProcessExited.async");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex,
                            "[SingBoxManager] Async orphan adapter remove failed (non-fatal)");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
            }
        }
    }

    /// <summary>
    /// Read the tail of singbox.log and emit it line-by-line into the
    /// vpnrouter.log so a single log dump contains both engine state and
    /// sing-box's last words before the crash. Best-effort: returns
    /// silently on any I/O error. Tail is bounded to keep vpnrouter.log
    /// readable.
    /// </summary>
    private void LogSingBoxCrashTail()
    {
        try
        {
            var path = AppPaths.SingBoxLogPath;
            if (!File.Exists(path)) return;

            // Open with full sharing in case sing-box (or the OS) hasn't
            // released the write handle yet on a hard kill.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);

            // Bounded ring buffer — last 50 lines is enough to catch the
            // typical sing-box panic + a handful of preceding INFO lines
            // for context, without flooding vpnrouter.log on every crash.
            const int TailLines = 50;
            var buffer = new string[TailLines];
            var count = 0;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                buffer[count % TailLines] = line;
                count++;
            }

            if (count == 0)
            {
                _logger.Warning("[SingBoxManager] singbox.log was empty — no crash context to capture");
                return;
            }

            var keep = Math.Min(count, TailLines);
            var start = count >= TailLines ? count % TailLines : 0;
            _logger.Warning("[SingBoxManager] === sing-box crash tail (last {Keep} of {Total} lines) ===", keep, count);
            for (var i = 0; i < keep; i++)
            {
                var idx = (start + i) % TailLines;
                _logger.Warning("[singbox] {Line}", buffer[idx]);
            }
            _logger.Warning("[SingBoxManager] === end sing-box crash tail ===");
        }
        catch (Exception ex)
        {
            // Diagnostics layer must never break crash handling itself.
            _logger.Debug(ex, "[SingBoxManager] Failed to capture sing-box crash tail");
        }
    }

    /// <summary>
    /// PinkuDani Fix #3 (2026-05-21): scan the captured stderr ring buffer
    /// for substrings that identify the "TUN orphan" crash class — when
    /// sing-box's <c>WintunCreateAdapter</c> refuses with
    /// ERROR_FILE_EXISTS because a previous-session adapter record is
    /// still alive in the kernel.
    ///
    /// <para>Sets <see cref="LastCrashWasTunOrphan"/> true when any of
    /// three patterns is found in the captured stderr lines. Patterns are
    /// English-locale because sing-box emits its logs in English regardless
    /// of OS UI language (verified via PinkuDani log line 124 — Russian
    /// Windows still shows the English FATAL).</para>
    ///
    /// <para>Best-effort — never throws. Buffer is small (50 lines) so
    /// scan cost is negligible (≤50 IndexOf calls per crash). Reads the
    /// buffer under the same lock as the writer in the ErrorLine handler
    /// so we don't tear a mid-write line.</para>
    /// </summary>
    private void DetectTunOrphanCrashSignature()
    {
        try
        {
            // Snapshot the buffer under the lock so the writer can't tear
            // a mid-write line. The snapshot is cheap — 50 string refs.
            string[] snapshot;
            int count;
            lock (_capturedStderrLock)
            {
                snapshot = (string[])_capturedStderr.Clone();
                count = _capturedStderrCount;
            }

            if (count == 0)
            {
                LastCrashWasTunOrphan = false;
                LastCrashWasLinuxTunPermissionFailure = false;
                return;
            }

            LastCrashWasTunOrphan = false;
            LastCrashWasLinuxTunPermissionFailure = false;

            // Walk the bounded snapshot. The ring buffer wraps around
            // when count > buffer length; either way, every slot we
            // examine is either a captured line or null (slot never
            // touched). null is safe — IndexOf would NRE so check first.
            var keep = Math.Min(count, StderrBufferSize);
            for (var i = 0; i < keep; i++)
            {
                var line = snapshot[i];
                if (string.IsNullOrEmpty(line)) continue;

                if (OperatingSystem.IsLinux() && IsLinuxTunPermissionFailure(line))
                {
                    LastCrashWasLinuxTunPermissionFailure = true;
                    _logger.Error(
                        "[SingBoxManager] Linux denied TUNSETIFF. Automatic restart is disabled until VPNRouter is launched outside the restricting sandbox or receives host TUN privileges.");
                    return;
                }

                // Three signature patterns:
                // 1. The FATAL itself — the strongest signal.
                // 2. The broader `configure tun interface:` prefix — catches
                //    other TUN-config-failure modes that share the
                //    orphan-handle root cause.
                // 3. The `open interface take too much time to finish`
                //    warning that precedes the FATAL on network-interface-
                //    change races (per PinkuDani 2026-05-21 log line 165).
                if (OperatingSystem.IsWindows() &&
                    (line.IndexOf("Cannot create a file when that file already exists",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("configure tun interface:",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("open interface take too much time to finish",
                        StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    LastCrashWasTunOrphan = true;
                    _logger.Warning(
                        "[SingBoxManager] Detected TUN-orphan crash signature in stderr — " +
                        "HealthMonitor will fire netsh disable on VPNRouter-TUN before restart.");
                    return;
                }
            }

        }
        catch (Exception ex)
        {
            _logger.Debug(ex,
                "[SingBoxManager] DetectTunOrphanCrashSignature scan threw (non-fatal)");
            LastCrashWasTunOrphan = false;
            LastCrashWasLinuxTunPermissionFailure = false;
        }
    }

    internal static bool IsLinuxTunPermissionFailure(string? line) =>
        !string.IsNullOrEmpty(line) &&
        line.Contains("TUNSETIFF", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase);

}
