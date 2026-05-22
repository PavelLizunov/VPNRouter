using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum SingBoxState { Stopped, Starting, Running, Restarting, Failed }

public class SingBoxManager : IDisposable
{
    private readonly SingBoxSettings _settings;
    private readonly ILogger _logger;

    // Phase 3+ (2026-05-21): IProcessRunner adoption — the LAST long-lived
    // spawn target in Core. The legacy `Process? _process` field is replaced
    // by `IProcessHandle? _handle`; the handle owns Process lifetime, stream
    // wiring, and the load-bearing EnableRaisingEvents=false-before-Kill
    // pattern (ProcessHandle.Dispose, ProcessRunner.cs:288-290).
    //
    // What's NOT migrated through this seam: pkexec / sudo helper spawns
    // inside LinuxStopEscalationChain + TrySpawnAndWait + IsSingBoxAlive
    // (pgrep). Those are short-lived stop-side fire-and-forgets; their
    // migration is a follow-up batch and won't affect this brief's surface.
    // The Linux/macOS sing-box-as-root chain (sudo / pkexec wrapping the
    // sing-box exec) IS migrated — argv just differs by platform; the
    // ProcessRequest construction below selects sudo / pkexec / direct as
    // command-line tokens, not as a separate spawn path.
    private readonly IProcessRunner _runner;
    private IProcessHandle? _handle;
    private string _currentConfigPath = string.Empty;
    // B1 (v2.36 SingBoxManager lifecycle hardening): widened from `bool`
    // to `int` so Dispose can use Interlocked.CompareExchange for
    // atomic single-execution. Read by the ProcessExit ApplyDomain
    // handler (via Volatile.Read) to decide whether to run fallback
    // cleanup. 0 = alive, 1 = disposed.
    // See plans/singbox-lifecycle-hardening-v2.36.md.
    private int _disposed;
    private TunOwnershipLock _tunLock;

    // B2 (v2.36 SingBoxManager lifecycle hardening, see
    // plans/singbox-lifecycle-hardening-v2.36.md): atomic guard so only
    // one thread at a time progresses through StopInternal's body.
    // Concurrent Stop() callers (UI Disconnect + HealthMonitor restart
    // backoff + ProcessExit fallback) used to all reach the four Release
    // sites; TunOwnershipLock's _owned guard prevented the SemaphoreFullException,
    // but other StopInternal side-effects (Kill, _handle clear, State flip)
    // could still race. CompareExchange returns previous value — only the
    // thread that flips 0→1 wins entry; others see 1 and return early.
    // Resets to 0 in finally so sequential Stop()'s re-enter normally.
    // 0 = idle, 1 = stopping.
    private int _stopState;

    /// <summary>Test-only seam: swap in a fake for the long-lived sing-box
    /// spawn. Production paths use the default <see cref="ProcessRunner"/>.
    /// Mirrors TgProxyManager.Runner / VlessDeepVerifier.Runner. Not
    /// thread-safe — assumes serial xUnit execution within the fixture;
    /// tests reset in try/finally (or use the per-instance ctor injection
    /// below).</summary>
    internal static IProcessRunner Runner { get; set; } = new ProcessRunner();

    /// <summary>
    /// Linux-only: did <see cref="LaunchProcess"/> elevate via pkexec?
    /// If false, sing-box was spawned as a plain user process (possible when
    /// the binary has CAP_NET_ADMIN + CAP_NET_BIND_SERVICE from the .deb
    /// postinst / update-helper's setcap call). In that case
    /// <see cref="Stop"/> can just kill the tracked PID directly —
    /// no pkexec round-trip, no password prompt.
    /// </summary>
    private bool _linuxUsedPkexec;

    // 3G-2 (v3.0 refactor): replaced the per-class `static readonly HttpClient`
    // with the shared IHttpClient seam — consolidated retry policy, shared
    // DNS-refresh pool (PolicyHttpClient.Shared), test-injectable.
    // Roadmap: plans/v3.0-refactor-roadmap.md §3G-2.
    private readonly IHttpClient _http;

    public SingBoxState State { get; private set; } = SingBoxState.Stopped;
    public int? Pid => _handle != null && !_handle.HasExited ? _handle.Pid : null;
    public event EventHandler? Crashed;
    /// <summary>Fires after every successful LaunchProcess — initial start
    /// AND restart after crash. Listeners (e.g. CLI StateFile writer) use
    /// this to keep their persisted PID in sync with the live process.</summary>
    public event Action<int>? Started;

    // PinkuDani Fix #3 (2026-05-21): bounded stderr ring buffer for crash
    // diagnostics. Scanned in OnProcessExited to detect the specific
    // "TUN orphan" crash class (Cannot create a file when that file
    // already exists — sing-box's WintunCreateAdapter ERROR_FILE_EXISTS).
    // When the scan matches, LastCrashWasTunOrphan flips true so
    // HealthMonitor.AttemptRestart can fire a netsh-disable cleanup
    // before the next launch attempt — closes the gap where Fix #1+#4's
    // PreStartCleanupAsync netsh-enumeration didn't list the orphan
    // adapter on PinkuDani-class machines.
    //
    // Bounded at 50 lines so a chatty sing-box (10k+ debug lines / sec
    // under heavy load) doesn't blow memory; we only need the FATAL
    // and the warning that precedes it for the signature match.
    private const int StderrBufferSize = 50;
    private readonly string[] _capturedStderr = new string[StderrBufferSize];
    private int _capturedStderrCount;
    private readonly object _capturedStderrLock = new();

    /// <summary>
    /// PinkuDani Fix #3 (2026-05-21): true if the most recent sing-box exit
    /// was caused by a TUN configuration conflict — specifically the
    /// <c>configure tun interface: Cannot create a file when that file
    /// already exists</c> FATAL that fires when wintun's kernel state still
    /// holds a `VPNRouter-TUN` device record from a previous session that
    /// our standard `PreStartCleanupAsync` cleanup didn't remove (typically
    /// because the netsh enumeration step missed the orphan or
    /// `Remove-NetAdapter` was unavailable).
    ///
    /// <para>Reset to <c>false</c> on every successful <see cref="StartWithJson"/>,
    /// <see cref="Stop"/>, and <see cref="Dispose"/> — only the immediately-
    /// preceding crash's signature controls the flag. <see cref="HealthMonitor"/>
    /// reads this in its <c>AttemptRestart</c> continuation to fire a
    /// netsh-based force-disable on `VPNRouter-TUN` before the next
    /// <see cref="Restart"/> call.</para>
    ///
    /// <para>Detection covers three substring patterns observed in field
    /// logs (PinkuDani 2026-05-21, alicemoren1991 2026-05-19): the FATAL
    /// itself, the broader `configure tun interface:` prefix (catches
    /// localised variants and future TUN-config-failure modes), and the
    /// `open interface take too much time to finish` warning that precedes
    /// the FATAL in network-interface-change races.</para>
    /// </summary>
    public bool LastCrashWasTunOrphan { get; private set; }

    /// <param name="http">3G-2 (v3.0 refactor): HTTP seam used for Clash API
    /// hot-reload + liveness probe. Defaults to <see cref="PolicyHttpClient.Shared"/>;
    /// tests inject <c>FakeHttpClient</c> to stub the 127.0.0.1 Clash API
    /// without a real sing-box process.</param>
    /// <param name="runner">Phase 3+ (2026-05-21): IProcessRunner seam for
    /// the long-lived sing-box spawn. Defaults to the static
    /// <see cref="Runner"/> (production <see cref="ProcessRunner"/>); tests
    /// inject <c>FakeProcessRunner</c> to drive lifecycle without real
    /// sing-box. The Linux pkexec chain + macOS sudo chain construct the
    /// argv via tokens — the IProcessRunner just executes; the elevation
    /// path is not a separate code branch in the seam.</param>
    public SingBoxManager(SingBoxSettings settings, ILogger? logger = null, IHttpClient? http = null, IProcessRunner? runner = null)
    {
        _settings = settings;
        _logger = logger ?? Log.Logger;
        _http = http ?? PolicyHttpClient.Shared;
        _runner = runner ?? Runner;
        _tunLock = TunOwnershipLock.Instance(_logger);

        // B1 (v2.36 SingBoxManager lifecycle hardening): ProcessExit
        // fallback runs ONLY if Dispose() hasn't already executed.
        //   Normal shutdown: Dispose() sets _disposed=1 first → this
        //     lambda reads 1 and no-ops. Cleanup goes through the
        //     explicit Dispose path (which calls _tunLock.Dispose() too).
        //   Abrupt termination (Environment.Exit, OOM-kill, Ctrl+C
        //     without graceful shutdown): _disposed stays 0 → this
        //     lambda runs as the last-resort cleanup, releasing the
        //     TUN lock so the next instance can acquire it.
        // Pre-B1 the lambda always ran AND Dispose did its cleanup —
        // TunOwnershipLock.Dispose is idempotent so no crash, but the
        // dual-path pattern was muddled. See
        // plans/singbox-lifecycle-hardening-v2.36.md.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                _tunLock.Dispose();
        };
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

    public void Start(SingBoxConfig config) =>
        StartWithJson(ConfigGenerator.Serialize(config));

    public void StartWithJson(string configJson)
    {
        if (State == SingBoxState.Running)
        {
            _logger.Warning("[SingBoxManager] Already running (PID {Pid}), stopping first", Pid);
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
        StopInternal(releaseLock: true);
    }

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
            if (releaseLock) _tunLock.Release();

            // v2.30.1-r5 + hotfix 2026-05-19: belt-and-braces orphan
            // cleanup. OnProcessExited (above) already does this when
            // the Exited callback fires, but if the process was force-
            // killed AND the callback was suppressed
            // (EnableRaisingEvents=false from a prior Stop), we'd skip
            // the disable. Run it again here so the orphan can't slip
            // through. The hotfix adds an async Remove-NetAdapter
            // after the sync disable so the device record is gone by
            // the time any subsequent Start / Restart fires (otherwise
            // sing-box's WintunCreateAdapter would FATAL with
            // ERROR_FILE_EXISTS — alicemoren1991-2026-05-19).
            //
            // Fire-and-forget on a background Task: StopInternal is
            // called from sync paths (Stop, Restart, Dispose) and we
            // don't want to block UI/CLI returns on the ~150 ms
            // PowerShell spawn cost. The defence-in-depth
            // PreStartCleanupAsync in LaunchProcess will catch
            // whatever this misses anyway.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    TunAdapterDiagnostics.DisableOrphanedAdapter(
                        _logger, DefaultTunInterfaceName, "SingBoxManager.StopInternal.early");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                                _logger, DefaultTunInterfaceName,
                                "SingBoxManager.StopInternal.early.async");
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
            return;
        }

        // Phase 3+ (2026-05-21): EnableRaisingEvents=false-before-Kill
        // pattern moved to ProcessHandle.Dispose (ProcessRunner.cs:288-290).
        // The ordering is now an implicit invariant of the seam — the
        // dispose chain sets the flag, then Kill, then Process.Dispose,
        // so the Exited callback is suppressed for an intentional Stop.
        try
        {
            _handle!.Kill(entireProcessTree: true);
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
            // Task #53 (2026-05-21): gate on `releaseLock` to match the
            // 3 sibling paths above (Linux capability mode line 267,
            // pkexec/macOS line 315, Windows post-crash cleanup line
            // 331). Pre-Task-#53 this release was unconditional, which
            // meant Restart()'s `StopInternal(releaseLock: false)` call
            // STILL dropped the TUN lock — recreating the cross-instance
            // race the named semaphore was designed to prevent. See
            // plans/task53-singboxmanager-restart-tunlock-2026-05-21.md.
            if (releaseLock) _tunLock.Release();
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
            // means the device is gone moments after Stop returns —
            // important for Restart() which goes Stop → Sleep(750) →
            // LaunchProcess: any slack between disable + remove is
            // covered by the settle delay. Fire-and-forget because
            // StopInternal is sync and callers don't await.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    TunAdapterDiagnostics.DisableOrphanedAdapter(
                        _logger, DefaultTunInterfaceName, "SingBoxManager.StopInternal.killed");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                                _logger, DefaultTunInterfaceName,
                                "SingBoxManager.StopInternal.killed.async");
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
        }
        finally
        {
            // B2 (v2.36): reset _stopState so subsequent (sequential)
            // Stop() callers can re-enter normally. Concurrent callers
            // saw 1 above and bailed; this releases the guard for the
            // next legitimate Stop().
            Volatile.Write(ref _stopState, 0);
        }
    }

    public void Restart()
    {
        _logger.Information("[SingBoxManager] Restarting sing-box");
        State = SingBoxState.Restarting;
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
        // releasing the handle. We don't wait that long here (would
        // freeze the UI on every restart) but a small settle delay +
        // the LaunchProcess pre-enable below cover the common case.
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

    public void ReloadConfig(SingBoxConfig config, bool forceRestart = false) =>
        ReloadConfigJson(ConfigGenerator.Serialize(config), forceRestart);

    /// <summary>
    /// Apply a new sing-box config. By default tries hot-reload first
    /// (Clash API <c>PUT /configs</c>) and only falls back to a full
    /// kill+restart if hot-reload is unavailable.
    ///
    /// <para>v2.31.7-r1: <paramref name="forceRestart"/> bypasses the
    /// hot-reload attempt entirely and goes straight to kill+restart.
    /// Required for structural changes — namely RoutingMode and TUN
    /// fingerprint flips — where hot-reload swaps the in-memory config
    /// successfully (HTTP 204) but does NOT re-lay TUN routes / DNS
    /// settings, so the OS-level routing keeps the old behaviour even
    /// though sing-box reports the new config. Brat-2026-05-04 logs
    /// caught the silent failure on a split → full mode switch:
    /// VpnEngine logged «Forced full restart» but PID stayed the same
    /// because <c>ReloadConfigJson</c> ran <c>TryHotReload</c> first
    /// regardless of caller intent.</para>
    /// </summary>
    public void ReloadConfigJson(string configJson, bool forceRestart = false)
    {
        _logger.Information("[SingBoxManager] Reloading config{Mode}",
            forceRestart ? " (force restart, no hot-reload attempt)" : "");
        _currentConfigPath = WriteJsonToDisk(configJson);

        if (!forceRestart && TryHotReload())
            return;

        if (!forceRestart)
            _logger.Warning("[SingBoxManager] Hot-reload unavailable — restarting sing-box");
        Restart();
    }

    public bool TryReloadConfig(SingBoxConfig config) =>
        TryReloadConfigJson(ConfigGenerator.Serialize(config));

    public bool TryReloadConfigJson(string configJson)
    {
        _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
        _currentConfigPath = WriteJsonToDisk(configJson);
        return TryHotReload();
    }

    /// <summary>
    /// Phase 2D-4 (2026-05-17): public seam for the
    /// <see cref="ISingBoxApi"/> hot-reload path. Writes
    /// <paramref name="configJson"/> to disk (rotating the current path)
    /// and returns the absolute path. The caller is then expected to
    /// invoke <see cref="ISingBoxApi.ReloadConfigAsync"/> with the
    /// returned path — splitting the "write JSON" concern from the
    /// "talk to Clash API" concern that pre-2D-4 lived together inside
    /// <see cref="TryReloadConfigJson"/>.
    ///
    /// <para>Used by <see cref="HealthMonitor"/>. <see cref="VpnEngine"/>
    /// still uses the thicker <see cref="TryReloadConfigJson"/> /
    /// <see cref="ReloadConfigJson"/> entry points because its
    /// callsites depend on the bundled write+reload+restart-fallback
    /// behaviour and aren't part of the 2D-4 POC scope.</para>
    /// </summary>
    /// <param name="configJson">Generated sing-box JSON.</param>
    /// <returns>Absolute path the JSON was written to (currently always
    /// <c>%ProgramData%\VPNRouter\config\current.json</c> — same path
    /// every existing reload path writes to).</returns>
    public string WriteConfigToDisk(string configJson)
    {
        _currentConfigPath = WriteJsonToDisk(configJson);
        return _currentConfigPath;
    }

    public bool IsRunning()
    {
        // v2.21.5: on Unix (macOS + Linux) the Clash API is the authoritative
        // signal. Previously we short-circuited on State != Running, which
        // forced false when:
        //   • The app was restarted and a sing-box from a previous session
        //     is still alive (no process tracked by this VM instance).
        //   • sing-box was started by the Windows Service / external
        //     autostart path and our local _process reference was never
        //     populated.
        //   • Linux pkexec wrapper exited after spawning the root child —
        //     _process.HasExited=true even though sing-box is alive.
        // In all three cases Clash API still answers if the tunnel is up,
        // which is what the UI actually cares about. Drop the State gate
        // on Unix and trust the HTTP probe.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return IsClashApiAlive();

        if (State != SingBoxState.Running) return false;
        return _handle?.HasExited == false;
    }

    public bool IsHealthy()
    {
        if (OperatingSystem.IsMacOS())
            return State == SingBoxState.Running && IsClashApiAlive();

        if (_handle == null || _handle.HasExited)
            return false;

        // Phase 3+ (2026-05-21): metric introspection via the IProcessHandle
        // snapshot — the seam refreshes the underlying Process internally.
        var snapshot = _handle.TryGetSnapshot();
        if (snapshot == null)
            return false;

        var memoryMb = snapshot.WorkingSetBytes / 1024 / 1024;
        if (memoryMb > 500)
            _logger.Warning("[SingBoxManager] sing-box memory usage: {Mem}MB (threshold: 500MB)", memoryMb);

        return true;
    }

    public ProcessMetrics GetMetrics()
    {
        if (_handle == null || _handle.HasExited)
            return new ProcessMetrics();

        var snapshot = _handle.TryGetSnapshot();
        if (snapshot == null)
            return new ProcessMetrics();

        return new ProcessMetrics
        {
            MemoryMb = snapshot.WorkingSetBytes / 1024 / 1024,
            CpuTime = snapshot.TotalProcessorTime,
            StartTime = snapshot.StartTime
        };
    }

    // ─── Private ──────────────────────────────────────────────────────────────

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

    private bool TryHotReload()
    {
        // Pre-check: don't attempt an HTTP call to a dead sing-box. Without
        // this, a crash-recovery path that tries hot-reload first (because
        // a debounced process rescan landed between Crashed and our state
        // update) dumps a 20-line HttpRequestException stack into the log
        // — every single time. Checking HasExited gives us a fast, clean
        // "hot-reload unavailable, restarting" log line instead.
        if (_handle == null || _handle.HasExited)
        {
            _logger.Debug("[SingBoxManager] Hot-reload skipped — sing-box process not alive");
            return false;
        }

        try
        {
            // v2.31.0-r1 (CO-3 audit fix): the previous sync-over-async pattern
            // (`PutAsync(...).GetAwaiter().GetResult()` on a static HttpClient)
            // is mitigated by HttpClient.Timeout=3s, but on saturated
            // threadpools the awaiter's continuation could land on a starved
            // worker, extending the wait beyond Timeout. Solutions:
            //   1. Explicit CancellationToken with hard 3s deadline → enforces
            //      cancellation at .NET layer, not HttpClient internals.
            //   2. Future: convert to async signature and propagate awaits up
            //      to HealthMonitor.OnDebounceElapsed / AttemptRestart.
            // For now (1) is non-invasive and bounds the worst case explicitly.
            //
            // 3G-2 (v3.0 refactor): bumped from `_http.PutAsync(...)` to the
            // shared `IHttpClient.SendAsync(HttpRequest)` seam. Same URL, same
            // 3s deadline (now belt-and-braces — `HttpRequest.Timeout` + the
            // CancellationToken below both enforce it), same JSON body.
            var url = $"http://{_settings.ClashApi}/configs?force=true";
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Put, new Uri(url),
                Body: bodyBytes,
                BodyContentType: "application/json",
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();

            if (response.IsSuccess())
            {
                _logger.Information("[SingBoxManager] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    response.StatusCode);
                return true;
            }

            _logger.Warning("[SingBoxManager] Hot-reload HTTP {Code}: {Body}",
                response.StatusCode, response.AsString());
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[SingBoxManager] Hot-reload timed out after 3s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
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
            var psi = new ProcessStartInfo("/usr/sbin/getcap", $"\"{exePath}\"")
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

    /// <summary>The default TUN adapter name when not overridden by user
    /// config. Hard-coded to keep <see cref="SingBoxManager"/>'s API
    /// surface narrow (it only knows <see cref="SingBoxSettings"/>, not
    /// <c>AppSettings.Tun.InterfaceName</c>). Same default used by
    /// <see cref="OnProcessExited"/>'s orphan cleanup.</summary>
    private const string DefaultTunInterfaceName = "VPNRouter-TUN";

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
        lock (_capturedStderrLock)
        {
            _capturedStderrCount = 0;
            Array.Clear(_capturedStderr, 0, _capturedStderr.Length);
        }

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
        // right dance: disable + Remove-NetAdapter so the next create
        // call hits a clean slate. It also has a defence-in-depth
        // direct-by-name pass on the well-known VPNRouter-TUN name
        // so locale-dependent netsh enumeration quirks can't slip
        // an adapter past us.
        //
        // Sync-over-async via GetAwaiter().GetResult() is safe here:
        // LaunchProcess is itself a sync void method called from sync
        // sites (Start / Restart / Stop's escalation chain). The async
        // work inside PreStartCleanupAsync is bounded (5 s netsh +
        // 10 s PowerShell timeouts) so worst case we block for
        // ~15-20 s; in practice it's well under 1 s. Linux/macOS
        // returns 0 immediately — no wintun, no work, no block.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                TunAdapterDiagnostics.PreStartCleanupAsync(
                        _logger, "SingBoxManager.LaunchProcess")
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "[SingBoxManager] pre-launch TUN cleanup failed (non-fatal)");
            }
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
                spawnExe = "/usr/bin/pkexec";
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

    /// <summary>Check if sing-box Clash API responds (macOS: sing-box runs as root child of sudo).
    /// 3G-2 (v3.0 refactor): routed through the shared <see cref="IHttpClient"/>
    /// seam with an explicit 3 s deadline mirroring the legacy <c>HttpClient.Timeout</c>.</summary>
    private bool IsClashApiAlive()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Get, new Uri($"http://{_settings.ClashApi}/configs"),
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();
            return response.IsSuccess();
        }
        catch { return false; }
    }

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
                return;
            }

            // Walk the bounded snapshot. The ring buffer wraps around
            // when count > buffer length; either way, every slot we
            // examine is either a captured line or null (slot never
            // touched). null is safe — IndexOf would NRE so check first.
            var keep = Math.Min(count, StderrBufferSize);
            for (var i = 0; i < keep; i++)
            {
                var line = snapshot[i];
                if (string.IsNullOrEmpty(line)) continue;

                // Three signature patterns:
                // 1. The FATAL itself — the strongest signal.
                // 2. The broader `configure tun interface:` prefix — catches
                //    other TUN-config-failure modes that share the
                //    orphan-handle root cause.
                // 3. The `open interface take too much time to finish`
                //    warning that precedes the FATAL on network-interface-
                //    change races (per PinkuDani 2026-05-21 log line 165).
                if (line.IndexOf("Cannot create a file when that file already exists",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("configure tun interface:",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("open interface take too much time to finish",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LastCrashWasTunOrphan = true;
                    _logger.Warning(
                        "[SingBoxManager] Detected TUN-orphan crash signature in stderr — " +
                        "HealthMonitor will fire netsh disable on VPNRouter-TUN before restart.");
                    return;
                }
            }

            LastCrashWasTunOrphan = false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex,
                "[SingBoxManager] DetectTunOrphanCrashSignature scan threw (non-fatal)");
            LastCrashWasTunOrphan = false;
        }
    }

    private static string WriteJsonToDisk(string json)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);

        var path = AppPaths.CurrentConfigPath;
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        // B1 (v2.36): atomic single-execution guard. CompareExchange
        // returns the prior value of _disposed; only the thread that
        // observes 0 (and flips to 1) proceeds with cleanup. Concurrent
        // Dispose() calls from multiple threads are now safe — only one
        // runs the body. Pre-B1 this was a `bool _disposed` flag with
        // an unprotected check-then-set, which had a theoretical
        // race (concurrent Dispose from finalizer + manual call).
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        // PinkuDani Fix #3 (2026-05-21): clear the TUN-orphan flag on
        // teardown so a Dispose-then-rebuild path (uncommon but possible
        // in long-lived services) doesn't carry stale state.
        LastCrashWasTunOrphan = false;
        Stop();
        _handle?.Dispose();
        // B1 (v2.36): explicit _tunLock disposal in the normal cleanup
        // path. Pre-B1 the ProcessExit ApplyDomain hook was the only
        // site that called _tunLock.Dispose(); now Dispose() owns the
        // cleanup directly and ProcessExit no-ops (via the
        // Volatile.Read(_disposed) gate in the ctor lambda). The
        // double-cleanup ambiguity is gone.
        _tunLock.Dispose();
    }

    /// <summary>
    /// v2.29.0-r5 — Linux stop escalation chain. Tries kill methods from
    /// least-privileged to most-privileged, verifying after each step that
    /// sing-box is actually gone. Mac uses a separate sudo path; this method
    /// is Linux-only.
    ///
    /// <para>Steps:</para>
    /// <list type="number">
    /// <item>Plain user pkill (works in capability mode + .deb installs
    ///   where sing-box runs as user via setcap CAP_NET_ADMIN).</item>
    /// <item>pkexec pkill -KILL (polkit GUI prompt; SIGKILL bypasses any
    ///   signal mask sing-box might have set).</item>
    /// <item>sudo pkill -KILL (NOPASSWD if sudoers entry was set up at
    ///   first Connect; falls through if not configured).</item>
    /// </list>
    ///
    /// <para>Each attempt is followed by IsSingBoxAlive() check (Clash API
    /// probe + pgrep) so we know immediately if it worked. Logs each step
    /// for postmortem.</para>
    /// </summary>
    private void LinuxStopEscalationChain()
    {
        // Step 1: plain user pkill. Cheap and works in capability mode.
        if (TrySpawnAndWait("/usr/bin/pkill", "-TERM -f sing-box", 3000, "user pkill -TERM"))
        {
            // Wait briefly for graceful exit (sing-box on SIGTERM should
            // tear down TUN cleanly within ~1 s).
            System.Threading.Thread.Sleep(800);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: user pkill -TERM succeeded");
                return;
            }
        }

        _logger.Information("[SingBoxManager] Linux stop: user pkill didn't kill sing-box, escalating to pkexec");

        // Step 2: pkexec with SIGKILL. GUI prompt — user might dismiss.
        if (TrySpawnAndWait("/usr/bin/pkexec", "pkill -KILL -f sing-box", 30000, "pkexec pkill -KILL"))
        {
            System.Threading.Thread.Sleep(500);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: pkexec pkill -KILL succeeded");
                return;
            }
        }

        _logger.Warning("[SingBoxManager] Linux stop: pkexec didn't kill sing-box, trying sudo");

        // Step 3: sudo with -n (non-interactive — fail if password needed
        // rather than block forever). If user set up NOPASSWD sudoers, this
        // works without prompt; otherwise it fails fast and we give up
        // (better to surface the failure than hang forever).
        if (TrySpawnAndWait("/usr/bin/sudo", "-n pkill -KILL -f sing-box", 5000, "sudo -n pkill -KILL"))
        {
            System.Threading.Thread.Sleep(500);
            if (!IsSingBoxAlive())
            {
                _logger.Information("[SingBoxManager] Linux stop: sudo -n pkill -KILL succeeded");
                return;
            }
        }

        if (IsSingBoxAlive())
        {
            _logger.Error("[SingBoxManager] Linux stop: ALL escalation steps failed — sing-box still alive. " +
                          "Manual intervention required: `sudo pkill -KILL -f sing-box`. " +
                          "Possible causes: pkexec/polkit agent not installed; sudoers NOPASSWD not set up; " +
                          "sing-box running under a different uid we can't kill.");
        }
    }

    /// <summary>v2.29.0-r5: spawn an external process, wait, return true
    /// iff exit code 0. Used by Linux stop escalation chain. Errors logged
    /// but never thrown.</summary>
    private bool TrySpawnAndWait(string fileName, string args, int timeoutMs, string label)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} — Process.Start returned null", label);
                return false;
            }
            if (!p.WaitForExit(timeoutMs))
            {
                _logger.Warning("[SingBoxManager] Linux stop: {Label} timed out after {Ms} ms", label, timeoutMs);
                try { p.Kill(true); } catch { }
                return false;
            }
            _logger.Information("[SingBoxManager] Linux stop: {Label} exit={Code}", label, p.ExitCode);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Linux stop: {Label} threw", label);
            return false;
        }
    }

    /// <summary>v2.29.0-r5: check if sing-box is still running.
    /// Two-signal test: Clash API at 127.0.0.1:9090 + pgrep -f sing-box.
    /// Returns true if EITHER signal says alive (defensive — false
    /// negative on Clash API alone could leave a zombie).</summary>
    private bool IsSingBoxAlive()
    {
        if (IsClashApiAlive()) return true;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pgrep", "-f sing-box")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(2000)) { try { p.Kill(true); } catch { } return false; }
            // pgrep exit 0 = found at least one process; 1 = none.
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
