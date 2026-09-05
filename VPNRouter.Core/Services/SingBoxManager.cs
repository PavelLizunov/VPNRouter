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
    // Unix stop-side pkexec / sudo commands also route through this seam so
    // exact argument tokens and timeouts remain deterministic in tests.
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

    // Windows TUN removal is requested after an intentional stop/crash and can
    // outlive both the callback and the manager that started it. Keep one
    // process-wide ordered queue so a reconnect's fresh manager never races a
    // prior pnputil removal of the same adapter.
    private static readonly object s_tunRemovalGate = new();
    private static Task<TunAdapterNotReadyException?> s_pendingTunRemoval =
        Task.FromResult<TunAdapterNotReadyException?>(null);

    internal static void ResetTunRemovalQueueForTests()
    {
        lock (s_tunRemovalGate)
        {
            s_pendingTunRemoval = Task.FromResult<TunAdapterNotReadyException?>(null);
        }
    }

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
    private bool _ownsTunLock;
    private bool _exactStopUnconfirmed;
    private readonly object _lifecycleGate = new();

    // 3G-2 (v3.0 refactor): replaced the per-class `static readonly HttpClient`
    // with the shared IHttpClient seam — consolidated retry policy, shared
    // DNS-refresh pool (PolicyHttpClient.Shared), test-injectable.
    // Roadmap: plans/v3.0-refactor-roadmap.md §3G-2.
    private readonly IHttpClient _http;

    public SingBoxState State { get; private set; } = SingBoxState.Stopped;
    public int? Pid => _handle != null && !_handle.HasExited ? _handle.Pid : null;
    internal IProcessHandle? OwnedProcessHandle => _handle;
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
    /// because the netsh enumeration step missed the orphan or exact PnP
    /// removal was unavailable).
    ///
    /// <para>Reset to <c>false</c> on every successful <see cref="StartWithJson"/>,
    /// <see cref="Stop"/>, and <see cref="Dispose"/> — only the immediately-
    /// preceding crash's signature controls the flag. <see cref="HealthMonitor"/>
    /// reads this in its <c>AttemptRestart</c> continuation to fire a
    /// netsh-based force-disable on `VPNRouter-TUN` before the next
    /// <see cref="Restart"/> call.</para>
    ///
    /// <para>Windows-only detection covers three substring patterns observed in field
    /// logs (PinkuDani 2026-05-21, alicemoren1991 2026-05-19): the FATAL
    /// itself, the broader `configure tun interface:` prefix (catches
    /// localised variants and future TUN-config-failure modes), and the
    /// `open interface take too much time to finish` warning that precedes
    /// the FATAL in network-interface-change races.</para>
    /// </summary>
    public bool LastCrashWasTunOrphan { get; private set; }

    internal bool LastCrashWasLinuxTunPermissionFailure { get; private set; }

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

        // ProcessExit is a last-resort release for a live manager. Normal
        // Dispose unsubscribes after Stop releases only this manager's lease;
        // the singleton itself remains process-wide so an older manager cannot
        // dispose a lock already acquired by a newer one. The kernel releases
        // its semaphore handle on abrupt process termination regardless.
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
    public void ReloadConfigJson(string configJson, bool forceRestart = false) =>
        ReloadConfigJsonWithResult(configJson, forceRestart);

    internal bool ReloadConfigJsonWithResult(string configJson, bool forceRestart = false)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.Debug("[SingBoxManager] ReloadConfigJson ignored — manager already disposed");
                return false;
            }
            if (!_ownsTunLock)
            {
                _logger.Warning("[SingBoxManager] ReloadConfigJson ignored — manager does not own valid TUN lease");
                return false;
            }

            if (_exactStopUnconfirmed)
            {
                StopInternal(releaseLock: false);
                if (State != SingBoxState.Stopped)
                    return false;

                forceRestart = true;
            }

            _logger.Information("[SingBoxManager] Reloading config{Mode}",
                forceRestart ? " (force restart, no hot-reload attempt)" : "");
            _currentConfigPath = WriteJsonToDisk(configJson);

            if (!forceRestart && TryHotReload())
                return true;

            if (!forceRestart)
                _logger.Warning("[SingBoxManager] Hot-reload unavailable — restarting sing-box");

            return RestartCore();
        }
    }

    public bool TryReloadConfigJson(string configJson)
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                _logger.Debug("[SingBoxManager] TryReloadConfigJson ignored — manager already disposed");
                return false;
            }
            if (!_ownsTunLock || _exactStopUnconfirmed)
            {
                _logger.Warning("[SingBoxManager] TryReloadConfigJson ignored — manager does not own valid TUN lease");
                return false;
            }

            _logger.Information("[SingBoxManager] Attempting hot-reload (no restart fallback)");
            _currentConfigPath = WriteJsonToDisk(configJson);
            return TryHotReload();
        }
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// <summary>The default TUN adapter name when not overridden by user
    /// config. Hard-coded to keep <see cref="SingBoxManager"/>'s API
    /// surface narrow (it only knows <see cref="SingBoxSettings"/>, not
    /// <c>AppSettings.Tun.InterfaceName</c>). Same default used by
    /// <see cref="OnProcessExited"/>'s orphan cleanup.</summary>
    private const string DefaultTunInterfaceName = "VPNRouter-TUN";

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
        LastCrashWasLinuxTunPermissionFailure = false;
        Stop();
        // A failed exact stop keeps the exact IProcessHandle as its
        // only retry authority. Do not dispose that handle while its lease is
        // deliberately retained.
        if (!_ownsTunLock)
            _handle?.Dispose();
        // TunOwnershipLock is deliberately process-wide. Normal Stop releases
        // this manager's lease; disposing the singleton here could race a newer
        // manager that acquired it. A failed stop retains this manager's lease.
        if (_ownsTunLock)
        {
            _logger.Warning(
                "[SingBoxManager] Dispose preserved TUN ownership because exact stop was not confirmed (state={State})",
                State);
            AppDomain.CurrentDomain.ProcessExit += OnAppDomainProcessExit;
            Volatile.Write(ref _disposed, 0);
        }
    }
}

public class ProcessMetrics
{
    public long MemoryMb { get; init; }
    public TimeSpan CpuTime { get; init; }
    public DateTime? StartTime { get; init; }
}
