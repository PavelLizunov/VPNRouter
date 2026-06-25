using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public enum SingBoxState { Stopped, Starting, Running, Restarting, Failed }

public partial class SingBoxManager : IDisposable
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
    /// v2.37.0-r52 (ekko 2026-05-25 forced-restart crash suppression):
    /// set TRUE while <see cref="Restart"/> is in flight; <see cref="OnProcessExited"/>
    /// uses it to differentiate intentional Stop-during-Restart (exit code -1
    /// from Windows TerminateProcess) from a real sing-box FATAL.
    ///
    /// <para><b>Why this exists despite <c>SuppressExitedEvent</c>:</b> ekko's
    /// 25 May logs show 25+ "[ERR] sing-box crashed (exit code: -1)" lines
    /// during user-initiated routing_mode flips (split↔full). Each flip triggers
    /// <see cref="VpnEngine.ApplyAsync"/> → forceRestart=true →
    /// <see cref="ReloadConfigJson"/> → <see cref="Restart"/> →
    /// <see cref="StopInternal"/>(releaseLock:false). Although StopInternal
    /// calls <c>_handle.SuppressExitedEvent()</c> before <c>Kill()</c>, the
    /// OS-level event delivery still wins the race in ~30ms windows
    /// (brat-2026-05-24 logged 14ms, ekko 33ms) — the Exited callback is
    /// already in the dispatcher queue when SuppressExitedEvent breaks the
    /// subscription. Result: <c>OnProcessExited</c> fires, logs "crashed",
    /// flips State to Failed, and <see cref="HealthMonitor"/> sees the
    /// Crashed event → backoff restart loop kicks in (5s/10s/20s) on top of
    /// the explicit Restart already happening, causing 10-15s outage during
    /// what should be a 1-2s flip.</para>
    ///
    /// <para><b>Belt-and-braces:</b> this flag is the second line of defence.
    /// SuppressExitedEvent stays — it works in the majority of cases. When it
    /// loses the race, this flag catches the late callback and converts the
    /// ERR log into an INF "expected exit during restart" line + suppresses
    /// the Crashed event so HealthMonitor doesn't double-restart. Doesn't
    /// touch the genuine-crash path (different exit codes, or Stop() not in
    /// flight).</para>
    ///
    /// <para>Set true at top of <see cref="Restart"/>, cleared in finally at
    /// the end of <see cref="Restart"/> (after LaunchProcess returns or
    /// throws). Volatile so OnProcessExited (which runs on the ThreadPool
    /// dispatcher thread) sees the latest value without lock contention.</para>
    /// </summary>
    private volatile bool _restartInProgress;

    /// <summary>
    /// v2.41.2-r4 (2026-06-09 — reconnect-stop false-crash suppression):
    /// set TRUE while an intentional <see cref="StopInternal"/> teardown is in
    /// flight. <see cref="OnProcessExited"/> reads it — together with
    /// <see cref="_restartInProgress"/> — to treat a late OS Exited callback
    /// carrying an intentional-kill exit code (-1 / 137 / 143) as the expected
    /// tail of a Stop, not a crash.
    ///
    /// <para><b>Why this exists alongside <see cref="_restartInProgress"/>:</b>
    /// that flag is set ONLY inside <see cref="Restart"/>. But the GUI server /
    /// subscription switch (<c>MainWindowViewModel.ReconnectAsync</c>) tears the
    /// old sing-box down via <see cref="VpnEngine.Stop"/> → <see cref="Stop"/> →
    /// <c>StopInternal(releaseLock: true)</c> and then a FRESH
    /// <see cref="VpnEngine.StartAsync"/> — i.e. Stop()+Start, NOT
    /// <see cref="Restart"/>. So <see cref="_restartInProgress"/> is false during
    /// that stop, and when <c>SuppressExitedEvent</c> loses its ~14-33ms race
    /// (the same window documented on <see cref="_restartInProgress"/>) the late
    /// callback fell through to the ERR "sing-box crashed (exit code: -1)" branch
    /// AND fired <see cref="Crashed"/> → <see cref="HealthMonitor"/> launched a
    /// redundant recovery restart on top of the reconnect (churn + a brief extra
    /// outage). Pavel's 2026-06-09 diagnostics (v2.41.2-r1) caught this on every
    /// server switch.</para>
    ///
    /// <para><b>Scope is deliberately tight — this is the load-bearing safety
    /// property:</b> set right after the concurrent-stop guard and cleared in
    /// <see cref="StopInternal"/>'s finally, so it is true ONLY across the actual
    /// kill+wait+cleanup body — exactly the window the late callback lands in. It
    /// is NEVER true during a steady-state run, so it cannot mask a GENUINE crash:
    /// a process that dies on its own has no Stop/Restart in flight → both flags
    /// false → Crashed still fires and HealthMonitor still recovers, unchanged.
    /// The exit-code gate (-1/137/143) is a second discriminator — a real
    /// sing-box FATAL exits with code 1, never the Kill-signal codes. Volatile
    /// for the same cross-thread reason as <see cref="_restartInProgress"/>
    /// (written on the caller thread, read on the ThreadPool dispatcher thread
    /// running <see cref="OnProcessExited"/>).</para>
    /// </summary>
    private volatile bool _stopInProgress;

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
        // v2.40.0 (audit P1, plans/bug-responsiveness-memory-audit-targets):
        // use a NAMED handler (not a capturing lambda) so Dispose() can
        // unsubscribe it. Pre-fix the anonymous lambda captured `this` and
        // could never be removed, keeping every disposed SingBoxManager alive
        // on AppDomain.ProcessExit's invocation list until process exit —
        // harmless for the usual one-manager-per-process case, but a leak when
        // a test harness / future host-reload recreates the manager.
        AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit;
    }

    /// <summary>
    /// Last-resort TUN-lock release on abrupt process termination
    /// (<c>Environment.Exit</c> / OOM-kill / Ctrl+C without graceful
    /// shutdown). No-ops when <see cref="Dispose"/> has already run (gated on
    /// <c>_disposed</c>); <see cref="Dispose"/> unsubscribes it so a
    /// normally-disposed instance isn't retained. See the ctor comment +
    /// plans/singbox-lifecycle-hardening-v2.36.md.
    /// </summary>
    private void OnAppDomainProcessExit(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _tunLock.Dispose();
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

    public void Restart()
    {
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
        finally
        {
            _restartInProgress = false;
        }
    }

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

    public bool TryReloadConfigJson(string configJson)
    {
        _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
        _currentConfigPath = WriteJsonToDisk(configJson);
        return TryHotReload();
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
        // v2.40.0 (audit P1): drop the ProcessExit subscription so this
        // disposed instance is no longer retained by the AppDomain hook. Safe
        // ordering — _disposed is already 1, so even a ProcessExit firing
        // mid-Dispose no-ops; this unsubscribe just releases the reference.
        AppDomain.CurrentDomain.ProcessExit -= OnAppDomainProcessExit;
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
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
