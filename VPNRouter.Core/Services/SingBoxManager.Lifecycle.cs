using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using Serilog;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    public void Start(SingBoxConfig config) =>
        StartWithJson(ConfigGenerator.Serialize(config));

    public void StartWithJson(string configJson)
    {
        if (State == SingBoxState.Starting || _handle is { HasExited: false })
        {
            _logger.Warning(
                "[SingBoxManager] StartWithJson ignored - sing-box already {State} (PID {Pid}); use Restart/ReloadConfigJson for reconfigure",
                State,
                Pid);
            return;
        }

        if (State == SingBoxState.Running)
        {
            _logger.Warning("[SingBoxManager] Running state without live handle before StartWithJson; cleaning up stale state first");
            Stop();
        }

        // Take exclusive ownership of the TUN adapter. If another VPNRouter
        // instance (desktop UI / Windows Service / CLI) already owns it,
        // bail out instead of fighting over the same TUN device.
        if (!_tunLock.TryAcquire())
        {
            throw new TunOwnershipException(
                "Another VPNRouter instance already owns the TUN adapter. " +
                "Stop the other instance (e.g. disable Windows Service autostart) and try again.");
        }

        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
            : AppPaths.SingBoxExePath;

        // S1 (v2.45.0): register where WE launch sing-box so ProcessOwnership
        // recognises a custom executable_path (outside the default bin dir) as
        // ours — otherwise the takeover sweep would skip a genuinely-owned
        // sing-box and the next start couldn't acquire the TUN.
        ProcessOwnership.ConfiguredExePath = exePath;

        if (!File.Exists(exePath))
        {
            _tunLock.Release();
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }

        RotateSingBoxLog();
        _currentConfigPath = WriteJsonToDisk(configJson);

        _logger.Information("[SingBoxManager] Starting sing-box with config: {Config}", _currentConfigPath);

        State = SingBoxState.Starting;
        try
        {
            LaunchProcess(exePath);
        }
        catch
        {
            State = SingBoxState.Failed;
            _tunLock.Release();
            throw;
        }
    }

    public void Stop()
    {
        // PinkuDani Fix #3 (2026-05-21): user-initiated stop clears the
        // TUN-orphan crash flag — the crash window from the previous
        // session is no longer relevant. (Restart() goes through
        // StopInternal(releaseLock:false) instead and intentionally
        // preserves the flag so HealthMonitor's restart path still sees
        // it.)
        LastCrashWasTunOrphan = false;
        LastCrashWasLinuxTunPermissionFailure = false;
        StopInternal(releaseLock: true);
    }

    /// <summary>
    /// W0.1 (true-split): kill a WEDGED sing-box (process alive but the Clash API stopped
    /// serving — the TUN no longer forwards) so the wintun adapter dies with the process
    /// and the OS restores physical-NIC routes + DNS (split-EXCLUDED apps recover instead
    /// of black-holing forever). Leaves exactly the state a real crash leaves — dead
    /// process, TUN lock still HELD — so HealthMonitor's OnSingBoxCrashed recovery
    /// relaunches without re-acquiring the lock. Same primitive Restart() uses, minus the
    /// immediate relaunch (recovery owns the backoff). NOT Stop() (that releases the lock).
    /// </summary>
    public void KillWedgedForRecovery() => StopInternal(releaseLock: false);

    private void StopInternal(bool releaseLock)
    {
        // B2 (v2.36 SingBoxManager lifecycle hardening): atomic
        // concurrent-Stop guard. Only one thread flips _stopState from 0→1
        // and proceeds; others see the non-zero value and bail. Resets to
        // 0 in finally so sequential Stop()'s re-enter normally. Closes
        // the race window where two callers (UI Disconnect + HealthMonitor
        // restart backoff + ProcessExit fallback) could all enter
        // StopInternal concurrently and race on Kill / _handle clear /
        // State flip side-effects. See `plans/singbox-lifecycle-hardening-v2.36.md`.
        if (Interlocked.CompareExchange(ref _stopState, 1, 0) != 0)
        {
            _logger.Debug("[SingBoxManager] StopInternal: concurrent call detected (releaseLock={Release}), skipping", releaseLock);
            return;
        }
        // v2.41.2-r4 (reconnect-stop false-crash suppression): mark an
        // intentional stop in flight BEFORE any Kill. A late OS Exited callback
        // that wins the race against SuppressExitedEvent (the ~14-33ms window)
        // is then recognised by OnProcessExited as the expected tail of a Stop
        // and does NOT log a false crash / fire Crashed. Covers the GUI
        // server-switch ReconnectAsync path (Stop()+Start, not Restart()) that
        // _restartInProgress alone misses. Cleared in the finally below — so
        // the flag is true only across this kill+wait+cleanup body and can never
        // mask a genuine crash in steady state. See the _stopInProgress XML doc.
        _stopInProgress = true;
        try
        {
        _logger.Information("[SingBoxManager] Stopping sing-box (PID {Pid})", Pid);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            // sing-box runs as root — killed via privilege-escalation tool.
            // macOS: sudo (NOPASSWD from sudoers, set up at first Connect).
            // Linux: pkexec (polkit GUI prompt — same path used to start it).
            //
            // v2.21.3: Linux was previously falling through to the Windows
            // path below. That path checks _process?.HasExited and returns
            // early when true — which it always is on Linux, because our
            // _process reference is the short-lived pkexec wrapper that
            // exits immediately after spawning the root sing-box child.
            // Result: Stop was a no-op and sing-box kept running. Same
            // bug macOS would have had before we routed it here.
            //
            // v2.28.0: Linux capability-mode path — if LaunchProcess spawned
            // sing-box as a plain user process (it has CAP_NET_ADMIN via
            // setcap), then `_process` points at the REAL sing-box (not a
            // short-lived pkexec wrapper), and WE OWN that PID. Just Kill()
            // it directly — no elevation needed, no password prompt.
            if (OperatingSystem.IsLinux() && !_linuxUsedPkexec)
            {
                try
                {
                    if (_handle != null)
                    {
                        // Phase 3+ (2026-05-21): explicit EnableRaisingEvents=false
                        // dropped — ProcessHandle.Dispose handles that pattern
                        // transitively (ProcessRunner.cs:288-290). The
                        // Kill→WaitForExit→Dispose sequence preserves the
                        // load-bearing intent (no spurious Crashed event for an
                        // intentional Stop).
                        if (!_handle.HasExited)
                        {
                            // v2.36.0-r4 (brat 2026-05-24): suppress Exited
                            // event BEFORE Kill — sibling fix to Windows graceful
                            // path. See ProcessRunner.SuppressExitedEvent docs.
                            _handle.SuppressExitedEvent();
                            _handle.Kill(entireProcessTree: true);
                            try
                            {
                                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                                _handle.WaitForExitAsync(killCts.Token).GetAwaiter().GetResult();
                            }
                            catch (OperationCanceledException) { /* 5s elapsed; Dispose finalises */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SingBoxManager] Linux capability-mode Stop failed, falling back to pkill");
                    // Best-effort fallback — try plain pkill as user. Will
                    // succeed because we own the process in capability mode.
                    try
                    {
                        using var pk = Process.Start(new ProcessStartInfo("/usr/bin/pkill", "-f sing-box")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        });
                        pk?.WaitForExit(3000);
                    }
                    catch { /* swallow, State will be Stopped anyway */ }
                }
                finally
                {
                    _handle?.Dispose();
                    _handle = null;
                    State = SingBoxState.Stopped;
                    if (releaseLock) _tunLock.Release();
                    _logger.Information("[SingBoxManager] sing-box stopped (Linux capability mode, no pkexec)");
                }
                return;
            }

            // pkexec path (pre-v2.28 behaviour on Linux + always on macOS).
            //
            // v2.29.0-r5: stop became unreliable on Linux. User report
            // 2026-04-29: «не могу остановить vpn ... кнопа stop не убивает
            // sing-box». Pre-r5 the code fired ONE pkexec pkill and trusted
            // its WaitForExit without checking the exit code or whether
            // sing-box actually died. Failure modes that silently presented
            // as "Stopped" in the UI:
            //   - User dismissed the pkexec password prompt → exit 126,
            //     sing-box still alive.
            //   - Polkit agent not running (minimal WMs) → exit 127.
            //   - pkill matched the wrong PID list (sing-box killed via
            //     unexpected signal handler / refused).
            // r5 escalation chain:
            //   1. Try plain user pkill (works in capability mode + .deb
            //      installs that drop us into the cgroup as the owner).
            //   2. If still alive, pkexec pkill -KILL (SIGKILL — survives
            //      most signal masks).
            //   3. If still alive, sudo pkill -KILL (NOPASSWD if the
            //      sudoers entry was set up at first connect).
            //   4. Verify after each step that sing-box is gone (no
            //      Clash API + no pgrep hit). Log each step's outcome.
            // Phase 3+ (2026-05-21): the explicit EnableRaisingEvents=false
            // on the local handle is gone — ProcessHandle.Dispose
            // (ProcessRunner.cs:288-290) sets the flag before Kill anyway
            // (load-bearing intent preserved transitively). The pkexec
            // wrapper PID exited long ago by this point, but we still need
            // to call the escalation chain to kill the real sing-box (root
            // child) via pkexec pkill / sudo -n pkill.
            try
            {
                LinuxStopEscalationChain();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "[SingBoxManager] Error stopping sing-box");
            }
            finally
            {
                _handle?.Dispose();
                _handle = null;
                State = SingBoxState.Stopped;
                if (releaseLock) _tunLock.Release();
                _logger.Information("[SingBoxManager] sing-box stopped");
            }
            return;
        }

        if (_handle == null || _handle.HasExited)
        {
            // v2.30.1-r5: log this branch — pre-r5 it was silent, which
            // made the user-reported "Stop pressed but no log lines and
            // adapter remained" problem hard to diagnose. Explicitly
            // mark that we're in the post-crash cleanup path.
            _logger.Information(
                "[SingBoxManager] Stop called but sing-box already exited (process={ProcState}) — running cleanup-only path",
                _handle == null ? "null" : "HasExited");
            State = SingBoxState.Stopped;

            // v2.30.1-r5 + hotfix 2026-05-19: belt-and-braces orphan
            // cleanup. OnProcessExited (above) already does this when
            // the Exited callback fires, but if the process was force-
            // killed AND the callback was suppressed
            // (EnableRaisingEvents=false from a prior Stop), we'd skip
            // the disable. Run it again here so the orphan can't slip
            // through. The hotfix adds queued exact-PnP removal after the
            // sync disable so the device record is gone by
            // the time any subsequent Start / Restart fires (otherwise
            // sing-box's WintunCreateAdapter would FATAL with
            // ERROR_FILE_EXISTS — alicemoren1991-2026-05-19).
            //
            // Restart queues removal and LaunchProcess joins it. A final Stop
            // waits before releasing the system-wide TUN ownership lock, so a
            // second VPNRouter process cannot acquire the name while this
            // process is still deleting its old PnP node.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    QueueTunAdapterRemoval("SingBoxManager.StopInternal.early.async");
                    if (releaseLock)
                        WaitForQueuedTunAdapterRemoval();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
                }
            }
            if (releaseLock) _tunLock.Release();
            return;
        }

        // Phase 3+ (2026-05-21): EnableRaisingEvents=false-before-Kill
        // pattern moved to ProcessHandle.Dispose (ProcessRunner.cs:288-290).
        // The ordering is now an implicit invariant of the seam — the
        // dispose chain sets the flag, then Kill, then Process.Dispose,
        // so the Exited callback is suppressed for an intentional Stop.
        try
        {
            // v2.36.0-r4 (brat 2026-05-24 — intentional-stop regression fix):
            // explicitly suppress Exited event BEFORE Kill so the OS
            // notification doesn't bubble up as a false "sing-box
            // crashed" event to HealthMonitor's Crashed handler. The
            // Phase 3+ refactor (2026-05-21) moved EnableRaisingEvents=
            // false into ProcessHandle.Dispose — but Dispose runs in
            // the finally block AFTER WaitForExit completes, by which
            // time the Exited callback has already fired. Brat's
            // 12:11:49 log showed 14ms between intentional Stop and
            // false "sing-box crashed (exit code: -1)" entry.
            // SuppressExitedEvent breaks the OS-event subscription so
            // the bubble-up never happens.
            _handle!.SuppressExitedEvent();
            _handle.Kill(entireProcessTree: true);
            try
            {
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _handle.WaitForExitAsync(killCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { /* 5s elapsed; Dispose finalises */ }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Error while stopping process");
        }
        finally
        {
            _handle?.Dispose();
            _handle = null;
            State = SingBoxState.Stopped;
            _logger.Information("[SingBoxManager] sing-box stopped");

            // Hotfix 2026-05-19: also clean up the wintun adapter on
            // graceful Stop. The graceful path above sets
            // EnableRaisingEvents=false before Kill(), which intentionally
            // suppresses the Exited callback (and its
            // OnProcessExited-driven cleanup). Without an explicit
            // call here, every graceful Stop leaves the VPNRouter-TUN
            // device record alive, and the next Start hits ERROR_FILE_EXISTS
            // when WintunCreateAdapter runs. LaunchProcess's
            // PreStartCleanupAsync would catch it, but doing it here
            // starts removal moments after Stop returns. Restart() gives it a
            // short head start, then LaunchProcess joins the queue and verifies
            // exact PnP absence before spawning sing-box.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    QueueTunAdapterRemoval("SingBoxManager.StopInternal.killed.async");
                    if (releaseLock)
                        WaitForQueuedTunAdapterRemoval();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "[SingBoxManager] Orphan adapter cleanup failed (non-fatal)");
                }
            }

            // Task #53 (2026-05-21): Restart keeps the ownership lock. A final
            // Windows Stop releases it only after the PnP removal queue above
            // has settled, preventing another process from launching into our
            // outstanding cleanup.
            if (releaseLock) _tunLock.Release();
        }
        }
        finally
        {
            // v2.41.2-r4: clear the intentional-stop flag FIRST (before the
            // _stopState reset) so the suppression window stays open across the
            // entire kill+wait+cleanup body above and closes only once the stop
            // is fully done. Restart() keeps its own wider _restartInProgress
            // window across the subsequent Sleep+LaunchProcess, so clearing here
            // leaves no gap for the Restart case.
            _stopInProgress = false;
            // B2 (v2.36): reset _stopState so subsequent (sequential)
            // Stop() callers can re-enter normally. Concurrent callers
            // saw 1 above and bailed; this releases the guard for the
            // next legitimate Stop().
            Volatile.Write(ref _stopState, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private void QueueTunAdapterRemoval(string context)
    {
        lock (s_tunRemovalGate)
        {
            var previous = s_pendingTunRemoval;
            s_pendingTunRemoval = Task.Run(async () =>
            {
                var previousFailure = await previous.ConfigureAwait(false);
                try
                {
                    await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                        _logger, DefaultTunInterfaceName, context).ConfigureAwait(false);
                    // A later "already absent" result cannot prove that an
                    // earlier exact-InstanceId settle timeout recovered.
                    return previousFailure;
                }
                catch (TunAdapterNotReadyException ex)
                {
                    _logger.Warning(ex,
                        "[SingBoxManager] Queued orphan adapter removal did not settle");
                    return previousFailure ?? ex;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "[SingBoxManager] Queued orphan adapter removal failed");
                    // Other best-effort failures get one final synchronous
                    // attempt in LaunchProcess.
                    return previousFailure;
                }
            });
        }
    }

    [SupportedOSPlatform("windows")]
    private void WaitForQueuedTunAdapterRemoval()
    {
        while (true)
        {
            Task<TunAdapterNotReadyException?> pending;
            lock (s_tunRemovalGate)
                pending = s_pendingTunRemoval;

            var failure = pending.GetAwaiter().GetResult();

            // Keep the exact failed InstanceId and retry the strict gate. This
            // preserves fail-closed behavior without permanently disabling
            // HealthMonitor recovery after one transient scan/query failure.
            if (failure != null)
            {
                if (string.IsNullOrWhiteSpace(failure.InstanceId))
                    throw failure;

                TunAdapterDiagnostics.WaitForExactPnpRemovalSettledAsync(
                        _logger, failure.InstanceId, DefaultTunInterfaceName,
                        "SingBoxManager.LaunchProcess.queued")
                    .GetAwaiter().GetResult();
            }

            lock (s_tunRemovalGate)
            {
                // A cleanup may have been appended while this tail was
                // awaited. Join the new tail too; otherwise launch could still
                // race a later pnputil removal.
                if (!ReferenceEquals(s_pendingTunRemoval, pending))
                    continue;

                if (failure != null)
                    s_pendingTunRemoval = Task.FromResult<TunAdapterNotReadyException?>(null);
                return;
            }
        }
    }

    public void Restart()
    {
        // v2.44.3-r2 (concurrency audit): a HealthMonitor AttemptRestart
        // continuation can reach Restart() AFTER TeardownInternal disposed this
        // manager — the lifecycle gate that serialises the failover restart does
        // NOT extend to that threadpool continuation. Relaunching sing-box on a
        // disposed manager spawns a second process that contends with the new
        // manager for the wintun adapter (orphan TUN). A disposed manager must
        // never relaunch.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _logger.Debug("[SingBoxManager] Restart ignored — manager already disposed");
            return;
        }
        _logger.Information("[SingBoxManager] Restarting sing-box");
        State = SingBoxState.Restarting;

        // v2.37.0-r52 (ekko 2026-05-25): set the intentional-restart flag
        // BEFORE StopInternal so any late OS Exited event that wins the race
        // against SuppressExitedEvent gets caught by OnProcessExited's
        // flag-check and doesn't propagate as a false Crashed event. Clear
        // it in finally so genuine crashes during LaunchProcess (e.g. TUN
        // init FATAL) still surface normally.
        _restartInProgress = true;
        try
        {
            // Keep the TUN lock across restart so another instance can't slip in
            // during the brief window between Stop and LaunchProcess.
            StopInternal(releaseLock: false);

            // v2.31.9-r4 — give Windows a beat to tear down the wintun handle
            // before the next sing-box tries to open it. brat-2026-05-05 logged
            // a FATAL "configure tun interface: The device is not ready for
            // use" 16 seconds after a Restart() launched the new process; the
            // crash tail showed `inbound/tun[tun-in]: open interface take too
            // much time to finish!` — a kernel-level wintun teardown that
            // hadn't settled by the time the new process tried to claim the
            // device. The pre-existing
            // <see cref="TunAdapterDiagnostics.DisableOrphanedAdapter"/> r5
            // commentary cites a ~22 s lag between netsh disable and Windows
            // releasing the handle. The short pause lets the queued removal
            // advance; LaunchProcess then joins it and runs the bounded exact-
            // InstanceId settle gate before any new sing-box process is created.
            // Linux/macOS: no wintun, no race.
            if (OperatingSystem.IsWindows())
            {
                try { Thread.Sleep(750); } catch { }
            }

            var exePath = OperatingSystem.IsWindows()
                ? Environment.ExpandEnvironmentVariables(_settings.ExecutablePath)
                : AppPaths.SingBoxExePath;
            LaunchProcess(exePath);
        }
        finally
        {
            _restartInProgress = false;
        }
    }

    private void RotateSingBoxLog()
    {
        try
        {
            var logPath = AppPaths.SingBoxLogPath;

            if (!File.Exists(logPath))
                return;

            var fileInfo = new FileInfo(logPath);
            if (fileInfo.Length <= MaxLogSizeBytes)
                return;

            var oldPath = Path.ChangeExtension(logPath, ".old.log");
            if (File.Exists(oldPath))
                File.Delete(oldPath);

            File.Move(logPath, oldPath);
            _logger.Information("[SingBoxManager] Rotated singbox.log ({Size:F1} MB → singbox.old.log)",
                fileInfo.Length / 1024.0 / 1024.0);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Failed to rotate singbox.log");
        }
    }

    /// <summary>
    /// Linux-only: does the binary at <paramref name="exePath"/> have
    /// <c>cap_net_admin</c> + <c>cap_net_bind_service</c> set via setcap?
    /// If yes, we can launch it as a plain user process instead of through
    /// pkexec. Parses <c>getcap</c> output defensively — any unexpected
    /// format or missing tool returns false (safe fallback to pkexec).
    ///
    /// <para>Expected matching <c>getcap</c> output looks like:
    /// <c>/opt/vpnrouter/sing-box cap_net_admin,cap_net_bind_service=eip</c>
    /// (spacing and flag order can vary between libcap versions).</para>
    /// </summary>
    private bool HasNetCapability(string exePath)
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;

        try
        {
            var psi = new ProcessStartInfo("getcap", $"\"{exePath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            if (!p.HasExited || p.ExitCode != 0) return false;

            // getcap prints nothing if no capabilities are set.
            // When set, we need BOTH cap_net_admin and cap_net_bind_service
            // active ('e' = effective) so sing-box can actually use them.
            // Be lenient on order / whitespace.
            if (string.IsNullOrWhiteSpace(output)) return false;
            if (output.IndexOf("cap_net_admin", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (output.IndexOf("cap_net_bind_service", StringComparison.OrdinalIgnoreCase) < 0) return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] getcap probe failed — falling back to pkexec path");
            return false;
        }
    }

    /// <summary>
    /// Copies <c>libcronet.{dll,so}</c> next to the runtime sing-box binary so
    /// its NaiveProxy outbound — which dlopens Cronet from the executable's
    /// directory — works. sing-box is provisioned to
    /// <c>%ProgramData%\VPNRouter\bin\</c> but the Cronet lib ships in the app
    /// directory (<see cref="AppContext.BaseDirectory"/>). Without this, naive
    /// servers FATAL <c>cronet: library not found</c> (brat-reported on
    /// v2.41.1-r2). Idempotent (copies only when missing or a different size);
    /// no-op on macOS (no upstream Cronet) and when the lib isn't bundled.
    /// Failures are swallowed + logged — naive is the only feature that needs
    /// it, so a copy failure must not block sing-box launch.
    /// </summary>
    internal static bool TryColocateCronet(string singBoxExePath, string bundledDir, ILogger? logger)
    {
        var libName = OperatingSystem.IsWindows() ? "libcronet.dll"
                    : OperatingSystem.IsLinux()   ? "libcronet.so"
                    : null;
        if (libName == null) return false; // macOS / other: no Cronet upstream
        try
        {
            var src = Path.Combine(bundledDir, libName);
            if (!File.Exists(src)) return false; // not bundled (shouldn't happen on Win/Linux)
            var destDir = Path.GetDirectoryName(singBoxExePath);
            if (string.IsNullOrEmpty(destDir)) return false;
            var dest = Path.Combine(destDir, libName);
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                return true; // sing-box runs from the bundled dir — lib already beside it
            if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(src).Length)
                return true; // already co-located, same size
            File.Copy(src, dest, overwrite: true);
            logger?.Information("[SingBoxManager] Co-located {Lib} next to sing-box at {Dest}", libName, dest);
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[SingBoxManager] Could not co-locate {Lib} next to sing-box — NaiveProxy may fail to start", libName);
            return false;
        }
    }

    private void LaunchProcess(string exePath)
    {
        // PinkuDani Fix #3 (2026-05-21): reset the TUN-orphan crash flag +
        // stderr ring buffer at the launch chokepoint. EVERY start path
        // passes through here (user Start via StartWithJson, HealthMonitor
        // Restart, manual Restart), so this guarantees the new lifecycle
        // doesn't inherit a stale flag/buffer from the previous sing-box
        // session.
        //
        // Without this reset: HealthMonitor reads the flag, fires netsh
        // disable, calls Restart() (which goes StopInternal → LaunchProcess
        // — NO buffer/flag reset before). Old "Cannot create a file" lines
        // linger in the ring buffer; if the new sing-box crashes with
        // unrelated stderr that doesn't fill the 50-slot buffer, the
        // scanner would re-match the OLD lines and false-positive on the
        // next OnProcessExited.
        LastCrashWasTunOrphan = false;
        LastCrashWasLinuxTunPermissionFailure = false;
        lock (_capturedStderrLock)
        {
            _capturedStderrCount = 0;
            Array.Clear(_capturedStderr, 0, _capturedStderr.Length);
        }

        // v2.41.1-r3: guarantee libcronet sits next to the runtime sing-box so
        // NaiveProxy outbounds don't FATAL "cronet: library not found". Single
        // launch chokepoint → covers Start / Restart / HealthMonitor recovery.
        TryColocateCronet(exePath, AppContext.BaseDirectory, _logger);

        // Hotfix 2026-05-19 (v2.35.0) — pre-launch TUN adapter cleanup
        // for Windows. EVERY start path passes through here (user Start,
        // Apply hot-reload-fallback restart, HealthMonitor crash recovery,
        // manual Restart), so this is the single chokepoint where we
        // ensure the wintun adapter is in a state sing-box's
        // WintunCreateAdapter can succeed.
        //
        // sing-box 1.13.x doesn't OPEN existing adapters, it CREATES
        // them. If a prior session left a VPNRouter-TUN device record
        // behind (even disabled), the next WintunCreateAdapter call
        // refuses with ERROR_FILE_EXISTS:
        //   FATAL configure tun interface: Cannot create a file when
        //   that file already exists.
        // The pre-v2.35 workaround (pre-enable via netsh) only
        // restored the name reservation — the device record stayed
        // and the FATAL still fired. PreStartCleanupAsync does the
        // right dance: disable, remove the exact PnP device, then prove
        // it stayed absent before the next create call. It also has a
        // defence-in-depth
        // direct-by-name pass on the well-known VPNRouter-TUN name
        // so locale-dependent netsh enumeration quirks can't slip
        // an adapter past us.
        //
        // Sync-over-async via GetAwaiter().GetResult() is safe here:
        // LaunchProcess is itself a sync void method called from sync
        // sites (Start / Restart / Stop's escalation chain). The async
        // work inside PreStartCleanupAsync uses bounded subprocess calls and
        // a bounded PnP settle loop. Linux/macOS bypass this Windows-only gate.
        if (OperatingSystem.IsWindows())
        {
            WaitForQueuedTunAdapterRemoval();
            TunAdapterDiagnostics
                .PreStartCleanupAsync(_logger, "SingBoxManager.LaunchProcess")
                .GetAwaiter().GetResult();
        }

        // Phase 3+ (2026-05-21): IProcessRunner adoption — build the
        // ProcessRequest (executable + argv tokens) per-platform. The
        // elevation path (sudo / pkexec) is encoded as argv structure,
        // not as a separate spawn code branch — the seam executes the
        // request verbatim. ArgumentList (used inside ProcessRunner) gives
        // us shell-quote-free argument passing, so the legacy
        // `"\"{exePath}\" run -c \"{path}\""` single-string forms collapse
        // to a clean string[].
        string spawnExe;
        IReadOnlyList<string> spawnArgs;

        if (OperatingSystem.IsMacOS())
        {
            // sudo with NOPASSWD — sudoers configured by UI on first Connect
            spawnExe = "/usr/bin/sudo";
            spawnArgs = new[] { exePath, "run", "-c", _currentConfigPath };
        }
        else if (OperatingSystem.IsLinux())
        {
            var blocker = LinuxRuntimeEnvironment.GetTunPrivilegeBlocker();
            if (blocker != null)
            {
                _linuxUsedPkexec = false;
                throw new InvalidOperationException(
                    $"{Strings.LinuxTunSandboxUnsupported} ({blocker})");
            }

            // v2.28.0: Capability-first launch path (passwordless-by-default).
            //
            // If the sing-box binary has CAP_NET_ADMIN + CAP_NET_BIND_SERVICE
            // set via `setcap` (.deb postinst does this automatically; manual
            // for AppImage / tar.gz), we can spawn it as a plain user process:
            // it'll still be able to create a TUN adapter and bind low
            // ports, but everything else runs at normal user privilege.
            // No pkexec → no password prompt → same UX as Windows Service
            // or macOS after first-run sudoers setup.
            //
            // If the capability is missing (AppImage first run, broken
            // install, or xattr-less filesystem), fall back to the
            // v2.21-era pkexec path, which pops a polkit GUI prompt in
            // desktop sessions. Headless / no-agent systems get a clear
            // exit-127 error and a one-time hint from the UI about running
            // `sudo setcap ...` themselves.
            //
            // See plans/vpnrouter-v2.28-linux-passwordless.md for rationale
            // vs. polkit-policy-based alternative (we picked capabilities
            // because it's strictly least-privilege — sing-box runs as the
            // user, not root — and doesn't require a polkit agent at all).
            if (HasNetCapability(exePath))
            {
                _logger.Information("[SingBoxManager] Linux: launching as user (CAP_NET_ADMIN present, no pkexec needed)");
                _linuxUsedPkexec = false;
                spawnExe = exePath;
                spawnArgs = new[] { "run", "-c", _currentConfigPath };
            }
            else
            {
                _logger.Information("[SingBoxManager] Linux: falling back to pkexec (sing-box lacks CAP_NET_ADMIN — install via .deb or run 'sudo setcap cap_net_admin,cap_net_bind_service=+eip {Exe}' once)",
                    exePath);
                _linuxUsedPkexec = true;
                spawnExe = LinuxRuntimeEnvironment.ResolvePkexec()
                    ?? throw new InvalidOperationException(Strings.LinuxPkexecUnavailable);
                spawnArgs = new[] { exePath, "run", "-c", _currentConfigPath };
            }
        }
        else
        {
            spawnExe = exePath;
            spawnArgs = new[] { "run", "-c", _currentConfigPath };
        }

        var request = new ProcessRequest(
            ExecutablePath: spawnExe,
            Arguments: spawnArgs,
            CaptureStdout: true,
            CaptureStderr: true);

        // v2.44.3-r2 (concurrency audit): last-moment disposed re-check at the
        // spawn chokepoint. The top-of-Restart/ReloadConfigJson guard catches a
        // dispose that PRECEDES entry, but a stale HealthMonitor AttemptRestart
        // continuation can pass that guard and then race a Dispose during
        // StopInternal + the Windows Thread.Sleep(750) settle inside Restart().
        // Re-checking here — immediately before _runner.Start — shrinks that
        // window to ~0 so a disposed manager never spawns a second sing-box that
        // would contend with the freshly-built manager for the wintun adapter
        // (orphan TUN). EVERY launch path (initial StartWithJson, Restart,
        // HealthMonitor recovery) funnels through here; a non-disposed manager
        // (_disposed==0) is unaffected.
        if (Volatile.Read(ref _disposed) != 0)
        {
            _logger.Debug("[SingBoxManager] LaunchProcess aborted — manager disposed before spawn");
            return;
        }

        _handle = _runner.Start(request);

        // Capture the handle in a local so the lambda sees the right instance
        // even if Stop() nulls _handle mid-flight (the Exited callback fires
        // on a threadpool thread). Mirrors TgProxyManager Phase 3+ pattern.
        var startedHandle = _handle;
        startedHandle.OutputLine += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line))
                _logger.Debug("[sing-box] {Line}", line);
        };
        startedHandle.ErrorLine += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line))
            {
                _logger.Warning("[sing-box] {Line}", line);
                // PinkuDani Fix #3 (2026-05-21): stash the stderr line into
                // the bounded ring buffer so OnProcessExited can scan it
                // for the TUN-orphan crash signature. The lock is cheap
                // here (line frequency is low — sing-box stderr is FATAL/
                // WARN tier, not the chatty stdout debug stream); buffer
                // is fixed-size 50 so memory is bounded regardless of
                // sing-box behaviour.
                lock (_capturedStderrLock)
                {
                    _capturedStderr[_capturedStderrCount % StderrBufferSize] = line;
                    _capturedStderrCount++;
                }
            }
        };
        startedHandle.Exited += (_, _) => OnProcessExited();

        State = SingBoxState.Running;
        _logger.Information("[SingBoxManager] sing-box started (PID {Pid})", startedHandle.Pid);
        Started?.Invoke(startedHandle.Pid);
    }

    private static string WriteJsonToDisk(string json)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);

        var path = AppPaths.CurrentConfigPath;
        File.WriteAllText(path, json);
        return path;
    }

}
