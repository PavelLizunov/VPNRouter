using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Monitors sing-box health and manages the full lifecycle:
/// - Periodic health check every N seconds
/// - Auto-restart on crash with exponential backoff
/// - Debounced process rescan (5s window) to minimize sing-box reloads
/// - Firewall rule management for block_on_vpn_fail
/// </summary>
public class HealthMonitor : IDisposable
{
    private readonly SingBoxManager _singBox;
    private readonly IProcessScanner _scanner;
    private readonly IFirewallManager _firewall;
    private readonly MonitoringSettings _settings;
    private readonly ILogger _logger;

    // Phase 2D-4 (2026-05-17): Clash API talking concern split out of
    // SingBoxManager so HealthMonitor can be tested without spawning a
    // real sing-box process. See ISingBoxApi.cs. The default
    // ClashSingBoxApi points at SingBoxSettings.ClashApi
    // (127.0.0.1:9090 by convention).
    private readonly ISingBoxApi _api;

    // Owned ClashSingBoxApi (when ctor created a default). Disposed in
    // Stop()/Dispose() so the HttpClient leak doesn't survive teardown.
    // Null when the ctor was passed an externally-supplied ISingBoxApi
    // (test fakes, future DI wiring) — disposal is the caller's job.
    private readonly IDisposable? _ownedApi;

    // DNS-leak-lockdown reconciler. "Block DNS outside VPN" is a projection of
    // live tunnel state: lifted (fail open) the moment the tunnel stops serving
    // and re-armed on recovery, so a crash + restart-backoff or a dead/slow
    // server can't strand the user with DNS blocked ("no internet / endless
    // loading"). Optional ctor arg defaults to the real Windows impl; tests
    // inject a capture stub (NullWindowsDnsHardening).
    private readonly IWindowsDnsHardening _dnsHardening;

    // v2.42.0 StrictDns runtime failover. "Strict DNS — all DNS via VPN"
    // forces dns.final = vpn-dns (DoH through the proxy). When the proxy goes
    // unreachable (dead/slow server — the germany "endless loading / no
    // internet" report) EVERY DNS query hangs on that dead path. We suppress
    // StrictDns (dns.final → local-dns, DoH on the real NIC) while the proxy
    // is unreachable and re-arm on recovery. _strictDnsFailedOver is the live
    // "currently suppressed" state read by GenerateConfigJson; the streak
    // counters debounce so a single transient hiccup doesn't flap dns.final.
    // See StrictDnsFailoverPolicy (sibling of the DnsLeakLockdown fail-open).
    private volatile bool _strictDnsFailedOver;
    private int _strictDnsUnhealthyStreak;
    private int _strictDnsHealthyStreak;
    private const string StrictDnsProbeUrl = "http://www.gstatic.com/generate_204";
    private const int StrictDnsProbeTimeoutMs = 3000;
    private const int StrictDnsFailThreshold = 2;     // consecutive failed probes → fail open
    private const int StrictDnsRecoverThreshold = 2;  // consecutive healthy probes → re-arm

    private System.Threading.Timer? _healthTimer;
    private System.Threading.Timer? _debounceTimer;

    // v2.31.6-r10 (Phase D): power-event listener so we can recover
    // immediately on resume/unlock instead of waiting for the next
    // periodic OnHealthTick (which Windows modern-standby may have
    // throttled to >30 minutes per brat's 2026-05-03 log).
    private PowerEventListener? _powerListener;

    private Profile _activeProfile = null!;
    private AppSettings _appSettings = null!;
    private ScanResult? _lastScan;
    private int _restartAttempts;
    private bool _vpnWasRunning;
    private bool _disposed;
    // volatile (M-9, perf audit 2026-06-11): read on the threadpool AttemptRestart
    // continuation to abort a sing-box revival after a user Stop; the write happens
    // on the UI thread in Stop(), so the reader must see it without tearing.
    private volatile bool _isStopping;   // set during HealthMonitor.Stop() to block crash handling

    // v2.31.5-r2 (user-reported VPN-loss bug):
    // tracks user intent — true between Start() and Stop(), regardless of
    // whether sing-box is currently up. Pre-fix the only crash-recovery path
    // was the Task.Delay scheduled inside AttemptRestart from OnSingBoxCrashed.
    // If that continuation didn't fire (laptop slept and the task got
    // cancelled, App quit between schedule and fire, dispatcher starved,
    // exception thrown before logger flushed) the user was stranded:
    //   - _vpnWasRunning was set to false by OnSingBoxCrashed,
    //   - the periodic health-tick check `!isHealthy && _vpnWasRunning`
    //     evaluated to false-after-crash and did nothing,
    //   - firewall block rules stayed enabled forever, but VPN never came
    //     back without manual user intervention.
    // The new flag drives a defensive recovery path inside OnHealthTick
    // that re-attempts AttemptRestart whenever sing-box is dead, the user
    // intends VPN to run, and we're not in the middle of Stop().
    // See plans/release-notes-v2.31.5-r2.md for the full timeline.
    private bool _shouldBeRunning;

    // Cancels any pending AttemptRestart Task.Delay — prevents stale restarts
    // from firing after a successful reload has already happened.
    private CancellationTokenSource? _restartCts;

    // v2.31.6-r9 — re-entry guard for OnHealthTick.
    // System.Threading.Timer can fire callbacks re-entrantly when a
    // previous tick is still running. The body does cross-process
    // calls (`SingBoxManager.IsHealthy` → Clash API HTTP / Process
    // probe) which under strict-mode 5 s polls or under WMI/HTTP
    // latency can take multiple seconds. Two ticks racing each
    // other could each call `AttemptRestart` (now serialised by
    // the lock added in r8 — the harm is bounded but still wasteful)
    // or both observe stale state at different points. The Interlocked
    // gate here ensures only ONE OnHealthTick body runs at a time;
    // a re-entrant call returns immediately. CompareExchange returns
    // the *previous* value, so 0→1 success means we won the race;
    // any non-zero previous value means another tick is in-flight.
    private int _onHealthTickInProgress;

    // v2.31.6-r8 — serialise AttemptRestart against itself.
    // Iter#4 audit (2026-05-04) flagged that AttemptRestart is invokable from
    // two callbacks running on different threadpool threads:
    //   • OnHealthTick (periodic timer) — every 5/30 s while VPN should be up
    //   • OnSingBoxCrashed (Process.Exited event)
    // Pre-r8 the body did `_restartAttempts++;` and a non-atomic CTS swap
    // (`var old = _restartCts; _restartCts = new ...;`). Two threads could
    // race the increment (both passing the MaxRestartAttempts gate when
    // they shouldn't) AND race the CTS swap (one Cancel/Dispose hitting an
    // already-disposed CTS, or a leaked old reference). The lock here makes
    // the whole "increment + swap CTS + schedule Task.Delay" sequence
    // atomic with respect to itself; the existing Task.Delay continuation
    // remains async (it checks `ct.IsCancellationRequested` and `_isStopping`
    // before doing real work, so a stale restart from a cancelled attempt
    // is still benign).
    private readonly object _attemptRestartLock = new();

    // Debounce window — wait 5s after last new process before reloading
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);

    // Minimum time between full restarts to prevent restart storms.
    // If hot-reload fails and a full restart is needed, enforce at least this gap.
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(60);
    private DateTime _lastFullRestart = DateTime.MinValue;

    // v2.40.0-r10 #3 (core-audit leak window): a full sing-box Restart()
    // re-creates the TUN from scratch — the interface is NOT yet routing the
    // instant Restart() returns (it takes the OS a beat to bring the adapter
    // up and install routes; observed wintun warm-up is up to ~16s, see the
    // "device is not ready" retries in SingBoxManager). Lifting the kill-
    // switch block rules at that moment opens a leak window: routed apps
    // egress via the default route (direct/ISP) until the TUN is actually
    // serving. So the full-restart path no longer disables block rules inline
    // — it sets this flag, and OnHealthTick lifts the rules only once the
    // Clash API actually RESPONDS (GET /version succeeds == sing-box finished
    // loading the TUN inbound and is serving). On Windows IsHealthy() only
    // proves the process is alive, so the explicit Clash-API probe
    // (ClashApiResponds) is the real "TUN up" signal; a time-based fallback
    // (DeferredDisableMaxWait) lifts anyway if the API wedges so the user's
    // internet can't be stranded forever. Hot-reload keeps the TUN up the
    // whole time and still disables inline. int (not bool) so
    // Interlocked.Exchange can atomically check-and-clear across the restart-
    // continuation threadpool thread (set) and the timer thread (consume).
    private int _deferredBlockRuleDisable;

    // v2.40.0-r10 #3: hard ceiling on how long the deferred kill-switch lift
    // waits for the Clash API to confirm the restarted TUN is serving before
    // lifting anyway (process must still be healthy). 45s >> the ~16s worst-
    // case TUN warm-up, so by then the tunnel is up or sing-box is broken (in
    // which case isHealthy is false and we don't lift). Prevents a wedged
    // Clash API from stranding the user behind block rules indefinitely.
    private static readonly TimeSpan DeferredDisableMaxWait = TimeSpan.FromSeconds(45);

    public event EventHandler? VpnStarted;
    public event EventHandler? VpnStopped;
    public event EventHandler<int>? RestartAttempted; // arg = attempt number

    /// <summary>
    /// Construct the health monitor.
    /// </summary>
    /// <param name="singBox">SingBox process lifecycle manager (start /
    /// stop / kill / Restart).</param>
    /// <param name="scanner">Process scanner used in debounced rescans.</param>
    /// <param name="firewall">Firewall manager for block_on_vpn_fail leak
    /// protection on crash.</param>
    /// <param name="settings">MonitoringSettings (health-check interval,
    /// max restart attempts, etc.).</param>
    /// <param name="logger">Optional Serilog logger; defaults to
    /// <see cref="Log.Logger"/>.</param>
    /// <param name="api">Optional <see cref="ISingBoxApi"/> override
    /// for Clash-API-talking. When null, a default
    /// <see cref="ClashSingBoxApi"/> is constructed pointing at
    /// <c>http://127.0.0.1:9090</c> — same target the inline pre-2D-4
    /// code used. Pass a <see cref="VPNRouter.Tests.Fakes.FakeSingBoxApi"/>
    /// in tests to avoid spawning sing-box. (Note: when wave 6 sibling
    /// task 2D-3 lands its <c>IHttpClient</c> / <c>PolicyHttpClient</c>
    /// abstractions, refactor the default-construction path to wire
    /// those through — for now we use a plain HttpClient.)</param>
    public HealthMonitor(
        SingBoxManager singBox,
        IProcessScanner scanner,
        IFirewallManager firewall,
        MonitoringSettings settings,
        ILogger? logger = null,
        ISingBoxApi? api = null,
        IWindowsDnsHardening? dnsHardening = null)
    {
        _singBox = singBox;
        _scanner = scanner;
        _firewall = firewall;
        _settings = settings;
        _logger = logger ?? Log.Logger;
        // Default to the real Windows impl (no-op off-Windows); tests pass a stub.
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;

        // Resolve the Clash API client. Default to a ClashSingBoxApi
        // wired against the same target SingBoxManager.TryHotReload used
        // pre-2D-4 (the Clash API listens on localhost:9090 by
        // convention; the YAML default matches).
        if (api is not null)
        {
            _api = api;
            _ownedApi = null;
        }
        else
        {
            var concrete = new ClashSingBoxApi(logger: _logger);
            _api = concrete;
            _ownedApi = concrete;
        }

        _singBox.Crashed += OnSingBoxCrashed;
    }

    // ─── Start / Stop ─────────────────────────────────────────────────────────

    public void Start(Profile profile, AppSettings appSettings, ScanResult? initialScan = null)
    {
        // v2.40.0 (audit P2, plans/bug-responsiveness-memory-audit-targets):
        // idempotent Start. If a prior Start didn't go through Stop (a lifecycle
        // slip, or a re-arm after a fault), the old _healthTimer +
        // _powerListener would be silently overwritten below — the Timer leaks
        // and, worse, the PowerEventListener keeps its Windows SystemEvents
        // subscription alive for the life of the process. Tear the prior run
        // down first. No-op on the normal first Start (both fields null), so
        // the common path is unaffected.
        if (_healthTimer != null || _powerListener != null)
        {
            _logger.Warning("[HealthMonitor] Start() called while already running — " +
                "restarting cleanly to avoid orphaning the prior timer + power listener");
            Stop();
        }

        _activeProfile = profile;
        _appSettings = appSettings;
        _restartAttempts = 0;
        _isStopping = false;
        _shouldBeRunning = true;   // v2.31.5-r2: arms the OnHealthTick recovery path
        _lastScan = initialScan; // baseline — prevents reload on first debounce if nothing changed

        // Strict mode: poll every 5 seconds instead of the configured interval
        // (default 30s). Catches silent hangs faster — process exit events
        // already fire immediately, so this only matters for "alive but stuck"
        // sing-box where Clash API stops responding.
        var intervalSeconds = appSettings.App.StrictMode ? 5 : _settings.HealthCheckInterval;
        var intervalMs = intervalSeconds * 1000;

        _healthTimer = new System.Threading.Timer(
            OnHealthTick, null, intervalMs, intervalMs);

        // v2.31.6-r10 (Phase D): subscribe to Windows session/power events.
        // No-op on non-Windows; on Windows fires HealthMonitor.ProbeNow on
        // resume/unlock/console-connect so recovery doesn't have to wait
        // for the next periodic tick (modern-standby may have throttled
        // it to >30 min per brat's logs).
        _powerListener = new PowerEventListener(ProbeNow, _logger);
        _powerListener.Start();

        _logger.Information("[HealthMonitor] Started — check every {Sec}s, max {Max} restarts (strict mode: {Strict})",
            intervalSeconds, _settings.MaxRestartAttempts, appSettings.App.StrictMode);
    }

    public void Stop()
    {
        _isStopping = true;
        _shouldBeRunning = false;  // v2.31.5-r2: disarm OnHealthTick recovery

        // v2.40.0 Phase C (C3-1): clear the r10 #3 deferred-kill-switch-lift
        // state so it can't leak across a Stop()/Start() (reconnect). Without
        // this, a deferred lift still pending at disconnect would, on the next
        // session's first healthy tick, observe a STALE _lastFullRestart (now
        // far older than DeferredDisableMaxWait) -> fallbackElapsed=true -> lift
        // the kill-switch WITHOUT confirming the new session's TUN is serving,
        // defeating the deferred gate the r10 fix added.
        System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0);
        _lastFullRestart = DateTime.MinValue;
        // v2.31.0-r1 (CO-2): also dispose the CTS — Cancel alone leaves the
        // wait handle alive until the GC catches the finalizer. Symmetric
        // with the AttemptRestart swap pattern.
        var cts = _restartCts;
        _restartCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        // v2.31.0-r1 (CO-1): atomic swap on shutdown so a late ETW callback
        // racing OnNewProcessDetected can't observe a half-disposed state.
        var ht = System.Threading.Interlocked.Exchange(ref _healthTimer, null);
        var dt = System.Threading.Interlocked.Exchange(ref _debounceTimer, null);
        ht?.Dispose();
        dt?.Dispose();

        // v2.31.6-r10 (Phase D): unsubscribe SystemEvents listener.
        var pl = System.Threading.Interlocked.Exchange(ref _powerListener, null);
        pl?.Dispose();

        _logger.Information("[HealthMonitor] Stopped");
    }

    /// <summary>
    /// v2.31.6-r10 (Phase D, brat user-reported recovery gap fix).
    /// Public out-of-band probe — runs the same body as the periodic
    /// <see cref="OnHealthTick"/> immediately. Called from
    /// <see cref="PowerEventListener"/> on resume/unlock/console-connect
    /// so recovery doesn't have to wait for the next periodic tick
    /// (modern-standby can throttle the timer to &gt;30 min).
    ///
    /// <para>Safe to call concurrently with the periodic timer — the
    /// r9 <c>_onHealthTickInProgress</c> Interlocked gate inside
    /// OnHealthTick serialises both paths so only one runs at a time.</para>
    /// </summary>
    public void ProbeNow()
    {
        if (_isStopping || _disposed) return;
        OnHealthTick(state: null);
    }

    /// <summary>
    /// Call when a new process is detected by ETW/polling that might need to be added.
    /// Resets the debounce timer — actual reload happens 5s after the last call.
    ///
    /// IMPORTANT: skips debounce entirely if the process name is already in
    /// _lastScan.ProcessNames. Browser tabs spawn many child processes with names
    /// already monitored (chrome.exe, msedge.exe), and rescanning + hot-reloading
    /// for each one creates a "process rescan storm" that visibly stalls the VPN
    /// for 1-3 seconds when many tabs are opened in quick succession.
    /// </summary>
    public void OnNewProcessDetected(string processName)
    {
        // Skip if the name is already monitored — config wouldn't change anyway.
        // Saves a full process rescan + sing-box hot-reload per browser tab spawn.
        if (_lastScan?.ProcessNames != null &&
            _lastScan.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Verbose("[HealthMonitor] Process {Name} already monitored — skipping debounce", processName);
            return;
        }

        _logger.Debug("[HealthMonitor] New process detected: {Name} — debouncing", processName);

        // v2.31.0-r1 (CO-1 audit fix): atomic-swap the debounce timer.
        // Pre-fix this read+null+new sequence was non-atomic; ETW callbacks
        // fire on multiple threadpool threads (EtwProcessMonitor.cs:98-130),
        // so two concurrent calls could:
        //   - both read the same _debounceTimer reference → both call
        //     Dispose on the same instance (ObjectDisposedException) AND
        //     leak the second new'd timer (assigned-then-overwritten),
        //   - or fire two timers back-to-back, doubling the rescan storm.
        // Now: Interlocked.Exchange swaps in the new timer atomically and
        // returns the previous (or null), which we Dispose safely once.
        var newTimer = new System.Threading.Timer(
            OnDebounceElapsed, null,
            (int)DebounceWindow.TotalMilliseconds,
            Timeout.Infinite); // fire once
        var oldTimer = System.Threading.Interlocked.Exchange(ref _debounceTimer, newTimer);
        oldTimer?.Dispose();
    }

    // ─── Private: Health check ────────────────────────────────────────────────

    private void OnHealthTick(object? state)
    {
        // v2.31.6-r9: re-entry guard. If a previous tick is still
        // running (e.g. blocked in IsHealthy's Clash API HTTP call),
        // skip this invocation rather than running the body in
        // parallel. The previous tick will clear the flag in finally.
        if (System.Threading.Interlocked.CompareExchange(ref _onHealthTickInProgress, 1, 0) != 0)
        {
            _logger.Debug("[HealthMonitor] OnHealthTick skipped — previous tick still in progress");
            return;
        }

        try
        {
            var isHealthy = _singBox.IsHealthy();

            // v2.40.0-r10 #3 (core-audit): consume the deferred kill-switch
            // lift requested by the full-restart path. The leak window we're
            // closing is "fresh TUN re-created but not routing yet", so we must
            // NOT lift merely because the sing-box PROCESS is back (on Windows
            // IsHealthy() is process-liveness only — the Clash-API check is
            // macOS-only). Gate on the Clash API actually responding
            // (GET /version succeeds == sing-box loaded the TUN inbound and is
            // serving), with a time-based fallback so a wedged API can't strand
            // the user behind block rules forever. Interlocked.Exchange does an
            // atomic check-and-clear so a concurrent restart can't double-fire
            // or lose the flag. Fail-closed: while pending, block rules stay on.
            if (isHealthy && System.Threading.Volatile.Read(ref _deferredBlockRuleDisable) == 1)
            {
                var sinceRestart = DateTime.UtcNow - _lastFullRestart;
                var clashServing = ClashApiResponds();
                var fallbackElapsed = sinceRestart >= DeferredDisableMaxWait;
                if ((clashServing || fallbackElapsed)
                    && System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0) == 1)
                {
                    try
                    {
                        _firewall.DisableBlockRules();
                        _logger.Information(
                            "[HealthMonitor] Kill-switch block rules lifted after restart — TUN serving confirmed (clashApi={Clash}, fallback={Fallback}, +{Secs:F0}s)",
                            clashServing, fallbackElapsed, sinceRestart.TotalSeconds);
                    }
                    catch (Exception fwEx)
                    {
                        _logger.Error(fwEx, "[HealthMonitor] Failed to lift firewall block rules on deferred-disable");
                    }
                }
            }

            if (!isHealthy && _vpnWasRunning)
            {
                _logger.Warning("[HealthMonitor] Health check failed — sing-box is not healthy");
                AttemptRestart();
            }
            else if (!isHealthy && _shouldBeRunning && !_isStopping)
            {
                // v2.31.5-r2 (user-reported VPN-loss bug): defensive recovery.
                // sing-box is dead AND user wants VPN up AND we're not in
                // the middle of Stop(). This path catches the gap where
                // OnSingBoxCrashed scheduled an AttemptRestart Task.Delay
                // that never fired its continuation (laptop slept across the
                // 5s deadline; App was killed-then-revived by the OS;
                // dispatcher starved long enough that the CTS got cancelled
                // by Stop()-then-Start() outside our intent). Pre-fix this
                // condition silently stranded the user: _vpnWasRunning was
                // set false by OnSingBoxCrashed, the original `!isHealthy
                // && _vpnWasRunning` check no longer matched, and recovery
                // never happened until manual reconnect.
                //
                // Bound by _settings.MaxRestartAttempts — same cap as the
                // crash path. If we hit the ceiling here, AttemptRestart
                // logs "Max restart attempts reached" and gives up; the UI
                // surfaces "VPN down" and the user can press Reconnect.
                _logger.Warning("[HealthMonitor] sing-box dead while user wants VPN up — initiating recovery (intended-running path)");
                AttemptRestart();
            }
            else if (isHealthy && !_vpnWasRunning)
            {
                _vpnWasRunning = true;
                // v2.31.5-r2: a successful health observation after a crash
                // gap means recovery worked. Reset the attempt counter so
                // the next unrelated crash doesn't inherit a half-burned
                // backoff budget — the user shouldn't pay for previous
                // restart history once they're connected again.
                _restartAttempts = 0;
                _logger.Information("[HealthMonitor] VPN is up");
                VpnStarted?.Invoke(this, EventArgs.Empty);
            }

            // v2.42.0: DnsLeakLockdown "Auto" (fail-open) reconcile. The
            // "Block DNS outside VPN" firewall lockdown now follows live tunnel
            // state instead of staying pinned for the whole session — while the
            // tunnel is confirmed serving it stays armed; the moment serving
            // drops (crash backoff, dead/slow server, TUN not routing) it is
            // LIFTED so the user keeps DNS + internet instead of "no internet /
            // endless loading". Gate "serving" on the Clash API actually
            // responding (Windows IsHealthy() is process-liveness only and would
            // re-arm before the TUN forwards DNS — same signal the deferred
            // kill-switch lift above uses). Short-circuit && skips the 3s probe
            // when sing-box is already dead. Only runs when the feature is on
            // (zero cost + no extra probe otherwise). Idempotent — the
            // reconciler only touches the firewall on a real Enable/Disable
            // transition (see DnsLockdownPolicy).
            if (_appSettings?.App?.DnsLeakLockdown == true)
            {
                bool serving = isHealthy && ClashApiResponds();
                _dnsHardening.ReconcileLockdownForHealth(serving, _appSettings, _logger);
            }

            // v2.42.0: StrictDns runtime failover — keep "all DNS via tunnel"
            // (dns.final=vpn-dns) only while the proxy is actually reachable;
            // fail open to a direct resolver (local-dns) otherwise, and re-arm
            // on recovery. Gated on isHealthy because the reconcile applies via
            // hot-reload, which needs sing-box up; the reconciler is also a
            // no-op when StrictDns isn't the sole DNS driver (zero cost when
            // the feature is off).
            if (isHealthy)
                ReconcileStrictDnsFailover();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] Exception in health tick");
        }
        finally
        {
            // v2.31.6-r9: clear the re-entry guard. Volatile write via
            // Interlocked.Exchange ensures the next tick sees the cleared
            // flag without needing a memory barrier dance.
            System.Threading.Interlocked.Exchange(ref _onHealthTickInProgress, 0);
        }
    }

    // v2.40.0-r10 #3: cheap Clash-API liveness probe (GET /version) used to
    // gate the deferred kill-switch lift. A non-null version means the Clash
    // API is serving, which on every platform implies sing-box finished
    // loading its config — including the TUN inbound — i.e. the TUN is up.
    // This is the signal Windows IsHealthy() lacks (it only proves the
    // process is alive). The ISingBoxApi impl enforces an internal 3s deadline
    // so this can't wedge the timer thread; any failure (timeout, hung API,
    // dead process) is treated as "not serving yet" so we keep waiting
    // (fail-closed — block rules stay enabled).
    private bool ClashApiResponds()
    {
        try { return _api.GetVersionAsync().GetAwaiter().GetResult() != null; }
        catch { return false; }
    }

    // ─── v2.42.0: StrictDns runtime failover ─────────────────────────────────

    /// <summary>
    /// Raised when StrictDns ("all DNS via tunnel") is auto-suppressed because
    /// the proxy went unreachable (arg=true) or re-armed on recovery
    /// (arg=false). The App may surface a toast; Core only logs.
    /// </summary>
    public event EventHandler<bool>? StrictDnsFailoverChanged;

    /// <summary>
    /// True when StrictDns is the SOLE reason DNS is forced through the tunnel —
    /// generated (not custom) mode, split (not full) tunnel, include (not
    /// exclude) apps, and the toggle on. In full-tunnel / exclude mode ALL
    /// traffic legitimately rides the tunnel so DNS must stay on vpn-dns; we
    /// never fail those over.
    /// </summary>
    private bool StrictDnsIsSoleDriver()
    {
        var app = _appSettings?.App;
        if (app is null || !app.StrictDns) return false;
        if ((app.ConfigMode ?? "generated").Equals("custom", StringComparison.OrdinalIgnoreCase)) return false;
        if ((app.RoutingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase)) return false;
        if ((app.RoutingAppsMode ?? "include").Equals("exclude", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// Live proxy-reachability probe via Clash <c>/proxies/proxy/delay</c> —
    /// sing-box fetches a 204 endpoint THROUGH the proxy. Non-null delay =
    /// reachable. Tests the proxy directly (not dns.final), so it stays valid
    /// as both the fail-open trigger and the re-arm signal. The ISingBoxApi
    /// impl enforces an internal deadline so this can't wedge the timer thread.
    /// </summary>
    private bool ProxyReachable()
    {
        try
        {
            return _api.GetProxyDelayAsync("proxy", StrictDnsProbeUrl, StrictDnsProbeTimeoutMs)
                       .GetAwaiter().GetResult() != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// StrictDns failover reconcile (see <see cref="StrictDnsFailoverPolicy"/>).
    /// Probes the proxy, debounces both directions, and on a real transition
    /// regenerates the config (StrictDns suppressed / restored) + hot-reloads —
    /// only <c>dns.final</c> moves, routing is unchanged. Called every healthy
    /// tick; no-op when StrictDns isn't the sole DNS driver and we're not
    /// currently failed over (so zero probe cost when the feature is off).
    /// </summary>
    private void ReconcileStrictDnsFailover()
    {
        bool soleDriver = StrictDnsIsSoleDriver();
        var scan = _lastScan;
        // Nothing to arm AND nothing to undo, or no scan to regenerate from yet
        // → bail before the (3s) probe.
        if ((!soleDriver && !_strictDnsFailedOver) || scan is null)
            return;

        bool proxyOk = ProxyReachable();
        if (proxyOk) { _strictDnsHealthyStreak++; _strictDnsUnhealthyStreak = 0; }
        else { _strictDnsUnhealthyStreak++; _strictDnsHealthyStreak = 0; }

        // Hysteresis: only flip after the relevant threshold of consecutive
        // same-result probes, so a single transient hiccup doesn't flap dns.final.
        bool effectiveHealthy = _strictDnsFailedOver
            ? _strictDnsHealthyStreak >= StrictDnsRecoverThreshold   // re-arm: N consecutive good
            : _strictDnsUnhealthyStreak < StrictDnsFailThreshold;    // fail: N consecutive bad

        var action = StrictDnsFailoverPolicy.Decide(soleDriver, effectiveHealthy, _strictDnsFailedOver);
        if (action == StrictDnsAction.None)
            return;

        bool failOpen = action == StrictDnsAction.FailOpen;
        _strictDnsFailedOver = failOpen;

        try
        {
            // GenerateConfigJson reads _strictDnsFailedOver; reuse the last
            // scan's process list so only dns.final changes.
            var configJson = GenerateConfigJson(scan.ProcessNames.ToList());
            var reloaded = TryHotReloadViaApi(configJson);

            if (failOpen)
                _logger.Warning(
                    "[HealthMonitor] StrictDns auto-disabled (fail-open) — proxy unreachable after {N} probes; DNS failed over to direct resolver (local-dns) so the machine keeps internet (reload={Ok})",
                    _strictDnsUnhealthyStreak, reloaded);
            else
                _logger.Information(
                    "[HealthMonitor] StrictDns re-armed — proxy reachable again after {N} probes; all DNS back through the tunnel (reload={Ok})",
                    _strictDnsHealthyStreak, reloaded);

            StrictDnsFailoverChanged?.Invoke(this, failOpen);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] StrictDns failover reload failed");
        }
    }

    private void OnSingBoxCrashed(object? sender, EventArgs e)
    {
        if (_isStopping)
            return; // graceful shutdown in progress — ignore

        _vpnWasRunning = false;
        VpnStopped?.Invoke(this, EventArgs.Empty);

        // Enable firewall block rules — prevent traffic from leaking direct
        // while sing-box is down and TUN interface is gone
        try { _firewall.EnableBlockRules(); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to enable firewall block rules on crash"); }

        // v2.42.0: DnsLeakLockdown fail-open. sing-box (and the TUN with it) is
        // gone, so the DNS-port block rules would now strand the user offline.
        // Lift them immediately rather than waiting for the next health tick —
        // the tick re-arms once the tunnel is confirmed serving again. This is
        // the INVERSE of the kill-switch EnableBlockRules() above on purpose:
        // block_on_vpn_fail is a kill-switch (fail-CLOSED on crash), the DNS
        // leak lockdown is a privacy feature with nothing to protect while the
        // tunnel is down (fail-OPEN on crash). Different threat models — see
        // DnsLockdownPolicy. Gated inside the reconciler on the DnsLeakLockdown
        // flag, so this is a no-op when the feature is off.
        try { _dnsHardening.ReconcileLockdownForHealth(false, _appSettings, _logger); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to lift DNS lockdown on crash"); }

        if (_settings.RestartOnFailure)
            AttemptRestart();
    }

    private void AttemptRestart()
    {
        // v2.31.6-r8: serialise the attempt-counter increment + CTS swap
        // so concurrent OnHealthTick and OnSingBoxCrashed callbacks don't
        // race the gate or leak/double-dispose the cancellation token.
        // The Task.Delay continuation is left outside the lock so we don't
        // hold the lock across an async wait.
        int attempt;
        CancellationToken ct;
        int delayMs;
        lock (_attemptRestartLock)
        {
            if (_restartAttempts >= _settings.MaxRestartAttempts)
            {
                _logger.Error("[HealthMonitor] Max restart attempts ({Max}) reached — giving up",
                    _settings.MaxRestartAttempts);
                return;
            }

            _restartAttempts++;
            attempt = _restartAttempts;

            // Cancel any previously scheduled restart — only the latest one matters.
            // v2.31.0-r1 (CO-2 audit fix): the previous code reassigned _restartCts
            // without disposing the old one — one CancellationTokenSource leaked
            // per restart attempt. Long-running sessions with many crash-restart
            // cycles accumulated CTS instances. Now: Cancel + Dispose the previous
            // before swapping in the new one.
            // v2.31.6-r8: the swap is now under the lock so a concurrent caller
            // can't observe a half-published CTS (read after our write to
            // _restartCts but before we Cancel/Dispose oldCts).
            var oldCts = _restartCts;
            _restartCts = new CancellationTokenSource();
            ct = _restartCts.Token;
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed elsewhere */ }
                oldCts.Dispose();
            }

            // Exponential backoff: 5s, 10s, 20s, 40s, 80s
            delayMs = (int)Math.Pow(2, attempt - 1) * 5000;
        }

        RestartAttempted?.Invoke(this, attempt);

        _logger.Warning("[HealthMonitor] Restarting sing-box (attempt {N}/{Max}) in {Delay}ms",
            attempt, _settings.MaxRestartAttempts, delayMs);

        Task.Delay(delayMs, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _isStopping) return;

            // If sing-box is already running (e.g. debounce reload succeeded),
            // skip — no need to restart what's already up.
            // v2.31.6-r8: counter-reset under the lock so a concurrent
            // AttemptRestart caller observes the cleared budget atomically
            // (otherwise it could read mid-write of `_restartAttempts++`
            // and produce an off-by-one cap evaluation).
            if (_singBox.IsRunning())
            {
                _logger.Debug("[HealthMonitor] sing-box already running — skipping scheduled restart");
                lock (_attemptRestartLock) { _restartAttempts = 0; }
                return;
            }

            try
            {
                // Re-scan and regenerate config before restart
                var scan = _scanner.ScanForProfile(_activeProfile);
                var configJson = GenerateConfigJson(scan.ProcessNames);

                // PinkuDani Fix #3 (2026-05-21): if SingBoxManager flagged
                // the previous crash as a TUN orphan, force-disable the
                // adapter via netsh BEFORE the next restart attempt. See
                // RunTunOrphanRecoveryCleanup for the rationale + timing.
                if (!RunTunOrphanRecoveryCleanup(ct)) return;

                // M-9 (perf audit 2026-06-11): re-check the stop/cancel gate
                // immediately before reviving sing-box. The entry check (top of
                // this continuation) can be 1-3s stale by now — the scan, config
                // regen, and TUN-orphan cleanup above all take real time, and the
                // user may have pressed Stop in that window. Without this re-check
                // sing-box gets RESTARTED after a user Stop (UI shows "disconnected"
                // while the tunnel is live). _isStopping is volatile so this
                // threadpool thread sees Stop()'s write on the UI thread.
                if (ct.IsCancellationRequested || _isStopping)
                {
                    _logger.Information("[HealthMonitor] Stop requested during restart prep — aborting sing-box revival");
                    return;
                }

                // Phase 2D-4 (2026-05-17): try hot-reload via the
                // ISingBoxApi split first; fall back to a full restart
                // if it didn't take. Pre-2D-4 this was a single
                // _singBox.ReloadConfigJson(configJson) call that
                // bundled write+hot-reload+restart-fallback inline.
                var hotReloaded = TryHotReloadViaApi(configJson);
                if (!hotReloaded)
                {
                    _logger.Warning("[HealthMonitor] Hot-reload unavailable on restart attempt — performing full restart");
                    // #2 (2026-06-08, Pavel crash cascade): re-generate from the
                    // CURRENT settings immediately before the full restart. The
                    // configJson captured at the top of this continuation can be
                    // seconds stale — a concurrent AutoFailover server-switch may
                    // have persisted a NEW active server DURING the hot-reload
                    // attempt + TUN-orphan cleanup window above. The old
                    // `_singBox.Restart()` relaunched the on-disk config, which
                    // resurrected the PRE-switch server (runtime = Germany while
                    // UI/settings had already moved to Finland). Re-scan +
                    // regenerate + write-then-restart so the relaunch tracks the
                    // switched server and the two recovery paths (HealthMonitor +
                    // AutoFailover) converge instead of fighting.
                    try
                    {
                        var freshScan = _scanner.ScanForProfile(_activeProfile);
                        configJson = GenerateConfigJson(freshScan.ProcessNames);
                        scan = freshScan;
                        _singBox.ReloadConfigJson(configJson, forceRestart: true);
                    }
                    catch (Exception regenEx)
                    {
                        // scout #3 #5: GenerateConfigJson can throw (e.g. the v2.28.2
                        // empty-servers hard guard) if a concurrent settings mutation
                        // left zero resolvable servers at this instant. The old
                        // `_singBox.Restart()` could NOT fail this way — it relaunched
                        // the last-good on-disk config regardless of settings state.
                        // Fall back to that so a transient regen failure can't abort
                        // recovery entirely (relaunch-stale beats no-relaunch; the
                        // crash-path block rules stay enabled, so no leak).
                        _logger.Warning(regenEx,
                            "[HealthMonitor] Config regen before full restart failed — " +
                            "falling back to relaunch of last-good on-disk config");
                        _singBox.Restart();
                    }
                }
                _lastScan = scan;
                _lastFullRestart = DateTime.UtcNow;

                if (hotReloaded)
                {
                    // Hot-reload swapped the config in place via the Clash API —
                    // the TUN never went down, so it's safe to lift the
                    // kill-switch block rules immediately. Clear any pending
                    // deferred-disable from a prior full restart too.
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0);
                    try { _firewall.DisableBlockRules(); }
                    catch (Exception fwEx) { _logger.Error(fwEx, "[HealthMonitor] Failed to disable firewall block rules after hot-reload"); }
                }
                else
                {
                    // v2.40.0-r10 #3: full restart re-created the TUN — DEFER
                    // lifting the block rules until a health tick confirms
                    // sing-box is actually serving, to avoid a leak window
                    // while the fresh TUN is still coming up. Fail-closed: if
                    // it never becomes healthy, the block rules stay on.
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    _logger.Information("[HealthMonitor] Full restart done — deferring kill-switch lift until health confirmed");
                }

                _logger.Information("[HealthMonitor] sing-box restarted successfully");
                // v2.31.6-r8: counter-reset under the lock (see comment above).
                lock (_attemptRestartLock) { _restartAttempts = 0; }
                _vpnWasRunning = true;
                VpnStarted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[HealthMonitor] Restart attempt {N} failed", _restartAttempts);
            }
        }, CancellationToken.None); // ContinueWith always runs, checks ct inside
    }

    /// <summary>
    /// PinkuDani Fix #3 (2026-05-21): if <see cref="SingBoxManager.LastCrashWasTunOrphan"/>
    /// is true, force-disable the well-known <c>VPNRouter-TUN</c> adapter
    /// via netsh before the AttemptRestart caller goes to relaunch sing-box.
    ///
    /// <para>Closes the gap where Fix #1+#4's <c>PreStartCleanupAsync</c>
    /// didn't find the orphan via netsh enumeration on PinkuDani-class
    /// machines (enumeration timing unreliable mid-restart-loop).
    /// Direct-by-name + netsh-only — bypasses both enumeration uncertainty
    /// AND the unreliable PowerShell module that Win10 LTSC strips out.</para>
    ///
    /// <para>Windows-only — Linux/macOS sing-box doesn't use wintun so
    /// there's no equivalent crash class. The flag check short-circuits
    /// when false (the common case for non-TUN-orphan crashes) so we
    /// don't pay the ~5-50ms netsh cost on unrelated restarts.</para>
    ///
    /// <para>Internal so tests can invoke it directly via reflection
    /// without waiting 5+ seconds for AttemptRestart's exponential
    /// backoff timer to fire. Returns true when the caller should
    /// proceed with the restart; false only when the caller cancellation
    /// fires during the post-disable settle delay (caller should bail
    /// out of the continuation, since Stop() / a new restart is racing).</para>
    ///
    /// <para>Brief: plans/pinkudani-fix3-singbox-tun-orphan-recovery-2026-05-21.md.</para>
    /// </summary>
    internal bool RunTunOrphanRecoveryCleanup(CancellationToken ct)
    {
        if (!_singBox.LastCrashWasTunOrphan) return true;
        if (!OperatingSystem.IsWindows()) return true;

        _logger.Information(
            "[HealthMonitor] Previous crash was TUN orphan ('Cannot create a file when that file already exists'). " +
            "Force-disabling VPNRouter-TUN via netsh before retry.");

        try
        {
            var disabled = TunAdapterDiagnostics
                .TryDisableAdapterViaNetshAsync(
                    _logger, "VPNRouter-TUN",
                    "HealthMonitor.AttemptRestart.TunOrphan")
                .GetAwaiter().GetResult();

            if (!disabled)
            {
                _logger.Warning(
                    "[HealthMonitor] netsh disable failed — retry may also fail. " +
                    "User may need to manually disable VPNRouter-TUN in Network Connections " +
                    "OR install RSAT NetAdapter PowerShell module for reliable cleanup.");
            }

            // Brief delay so Windows has time to tear down the wintun
            // kernel handle after netsh disable. Per Agent A (Fix #1+#4)
            // brief: netsh admin=disabled is documented to release the
            // kernel handle but exact timing is unverified. 500ms is
            // generous; tune down later once field validation closes the
            // unverified-assumption loop.
            Task.Delay(500, ct).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            // ct fired during the delay — Stop() / new restart is racing.
            // Tell the caller to bail out of the continuation.
            return false;
        }
        catch (Exception netshEx)
        {
            _logger.Warning(netshEx,
                "[HealthMonitor] netsh disable for VPNRouter-TUN threw (non-fatal) — continuing with restart");
            return true;
        }
    }

    /// <summary>
    /// Phase 2D-4 (2026-05-17): write <paramref name="configJson"/> to
    /// disk and ask the Clash API to hot-reload via
    /// <see cref="ISingBoxApi.ReloadConfigAsync"/>. Returns whether the
    /// hot-reload was accepted. Callers decide how to handle failure —
    /// the AttemptRestart path escalates to a full
    /// <see cref="SingBoxManager.Restart"/>, while the OnDebounceElapsed
    /// path applies its own cooldown policy first.
    ///
    /// <para>Pre-2D-4 the equivalent code was bundled inside
    /// <see cref="SingBoxManager.ReloadConfigJson"/> /
    /// <see cref="SingBoxManager.TryReloadConfigJson"/>. The split here
    /// makes the Clash-API-talking step substitutable with
    /// <c>FakeSingBoxApi</c> in tests so we can drive crash-recovery +
    /// auto-failover paths without spawning real sing-box.</para>
    ///
    /// <para>This wraps an async call with a synchronous
    /// <c>.GetAwaiter().GetResult()</c> because the existing call sites
    /// (Task.Delay continuation in AttemptRestart, Timer callback in
    /// OnDebounceElapsed) are already sync-callback shaped. Future
    /// work: propagate async up to HealthMonitor's public surface so we
    /// can drop the sync-over-async. The ClashSingBoxApi enforces its
    /// own 3s deadline internally so a hung Clash API cannot stall
    /// this thread indefinitely.</para>
    /// </summary>
    /// <returns><c>true</c> if hot-reload was accepted; <c>false</c>
    /// otherwise (caller picks the fallback policy).</returns>
    private bool TryHotReloadViaApi(string configJson)
    {
        var path = _singBox.WriteConfigToDisk(configJson);
        try
        {
            return _api.ReloadConfigAsync(path).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // ISingBoxApi.ReloadConfigAsync contract says return false on
            // failure, never throw — but a buggy / mocked impl could
            // surface an exception. Treat it as "hot-reload didn't work".
            _logger.Debug(ex, "[HealthMonitor] ISingBoxApi.ReloadConfigAsync threw — returning false");
            return false;
        }
    }

    // ─── Private: Debounced process rescan ───────────────────────────────────

    private void OnDebounceElapsed(object? state)
    {
        if (_isStopping) return;

        try
        {
            _logger.Information("[HealthMonitor] Debounce elapsed — rescanning processes");

            var newScan = _scanner.ScanForProfile(_activeProfile);

            // Compare only the filtered (non-wildcard) names that actually reach sing-box.
            // This avoids spurious reloads when only wildcard patterns shift between scans.
            var newFiltered  = FilterForSingBox(newScan.ProcessNames);
            var prevFiltered = _lastScan == null ? null : FilterForSingBox(_lastScan.ProcessNames);

            if (prevFiltered != null &&
                new HashSet<string>(newFiltered, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(prevFiltered))
            {
                _logger.Debug("[HealthMonitor] No effective process changes — skipping reload");
                _lastScan = newScan; // keep scan timestamp fresh
                return;
            }

            _logger.Information("[HealthMonitor] Process list changed ({Prev} → {New} processes) — reloading sing-box config",
                prevFiltered?.Count ?? 0, newFiltered.Count);

            // Cancel any pending AttemptRestart — we're doing a fresh reload now
            _restartCts?.Cancel();

            var configJson = GenerateConfigJson(newScan.ProcessNames);

            // Try hot-reload first (no restart fallback) to avoid TUN restart storms.
            // If hot-reload fails, only do a full restart if cooldown has elapsed.
            //
            // Phase 2D-4 (2026-05-17): hot-reload goes through ISingBoxApi
            // split (write to disk via SingBoxManager.WriteConfigToDisk;
            // PUT /configs via ClashSingBoxApi). Pre-2D-4 this was a
            // single _singBox.TryReloadConfigJson(configJson) call. Same
            // behaviour, but the Clash API talking is now substitutable.
            if (TryHotReloadViaApi(configJson))
            {
                _lastScan = newScan;
                _restartAttempts = 0;
                _logger.Information("[HealthMonitor] Hot-reload succeeded with {Count} processes", newFiltered.Count);
            }
            else
            {
                var sinceLastRestart = DateTime.UtcNow - _lastFullRestart;
                if (sinceLastRestart < RestartCooldown)
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed, but cooldown active ({Remaining}s left) — deferring full restart",
                        (int)(RestartCooldown - sinceLastRestart).TotalSeconds);
                    // Save scan so next debounce can try again
                    _lastScan = newScan;
                }
                else
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed — performing full restart");
                    _lastFullRestart = DateTime.UtcNow;
                    // v2.40.0 Phase C (C4-1): a full restart bounces the TUN
                    // (~16s warm-up). Protect routed apps during the bounce the
                    // same way the crash path does — enable the kill-switch
                    // block rules BEFORE the TUN drops, then DEFER lifting them
                    // until a health tick confirms the new TUN is serving
                    // (Clash-API gated, r10 #3). No-op when block_on_vpn_fail
                    // created no rules. Pre-fix this debounce-driven config
                    // reload bounced the TUN with no kill-switch coverage.
                    try { _firewall.EnableBlockRules(); }
                    catch (Exception fwEx) { _logger.Error(fwEx, "[HealthMonitor] Failed to enable block rules before debounce restart"); }
                    _singBox.Restart();
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    _lastScan = newScan;
                    _restartAttempts = 0;
                    _logger.Information("[HealthMonitor] Full restart completed with {Count} processes", newFiltered.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] Error in debounced rescan");
        }
    }

    /// <summary>
    /// Generates sing-box config JSON. Uses CustomConfigInjector for custom mode,
    /// ConfigGenerator for generated mode. Handles both transparently.
    /// </summary>
    private string GenerateConfigJson(List<string> processNames)
    {
        var isCustom = (_appSettings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

        if (isCustom)
        {
            // Try named ProgramData copy first (multi-config)
            var configName = _appSettings.App.ActiveCustomConfig;
            if (!string.IsNullOrEmpty(configName))
            {
                var namedPath = CustomConfigInjector.GetProgramDataPath(configName);
                if (File.Exists(namedPath))
                {
                    var rawJson = File.ReadAllText(namedPath);
                    return CustomConfigInjector.Inject(rawJson, processNames, _appSettings);
                }
            }

            // Fallback: old single custom.json or custom_config path
            var legacyPath = Environment.ExpandEnvironmentVariables(
                @"%ProgramData%\VPNRouter\config\custom.json");
            if (File.Exists(legacyPath))
            {
                var rawJson = File.ReadAllText(legacyPath);
                return CustomConfigInjector.Inject(rawJson, processNames, _appSettings);
            }

            if (!string.IsNullOrEmpty(_appSettings.App.CustomConfig))
            {
                var customPath = Environment.ExpandEnvironmentVariables(_appSettings.App.CustomConfig);
                var fallbackJson = File.ReadAllText(customPath);
                return CustomConfigInjector.Inject(fallbackJson, processNames, _appSettings);
            }
        }

        // Phase 2F (2026-05-17): config-generation pipeline extracted into
        // ConfigPipeline.Generate so VpnEngine + HealthMonitor share the
        // exact same Resolve → Generate → Validate → Serialize sequence.
        // This closes the v2.28.2 silent-leak bug class (parallel pipelines
        // drifting over time). Advisory ValidationMode preserves the r5
        // recovery contract: validation failure logs+warns, doesn't throw
        // (so a transient invariant glitch doesn't block HealthMonitor's
        // auto-restart path).
        //
        // Pre-2F this body inlined:
        //   1. VlessServersResolver.Resolve (defense-in-depth, idempotent)
        //   2. ConfigGenerator.Generate
        //   3. LeakProtection.ValidateConfig (advisory — r5 chokepoint)
        //   4. ConfigGenerator.Serialize
        // All four steps now live in ConfigPipeline. Behaviour is identical:
        // same exception surface (none, advisory mode swallows + logs),
        // same WARN log lines, same serialised output.
        return ConfigPipeline.Generate(
            _activeProfile,
            processNames,
            _appSettings,
            ConfigPipeline.ValidationMode.Advisory,
            warningSink: null, // HealthMonitor surfaces leaks via logger only
            logger: _logger,
            // v2.42.0 StrictDns failover: while the proxy is unreachable we
            // suppress "all DNS via tunnel" (dns.final → local-dns) so the
            // machine keeps DNS. The flag persists across regens (process
            // rescans, restarts) until a healthy tick re-arms it.
            strictDnsOverride: _strictDnsFailedOver ? false : (bool?)null);
    }

    /// <summary>
    /// Returns only exact process names (no wildcards) — the same subset
    /// that ConfigGenerator passes to sing-box process_name rules.
    /// </summary>
    private static List<string> FilterForSingBox(IEnumerable<string> names) =>
        names.Where(p => !p.Contains('*') && !p.Contains('?'))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        // M-1 (perf audit 2026-06-11): unsubscribe from SingBoxManager.Crashed so
        // the two objects don't root each other after teardown. The subscription
        // is made ONCE in the ctor (there is no Start() that re-subscribes), so
        // the -= belongs in Dispose (terminal), NOT Stop (which can precede a
        // resume). Combined with SingBoxManager.Dispose unhooking ProcessExit
        // (now called from VpnEngine.Stop), this closes the per-connect leak.
        try { _singBox.Crashed -= OnSingBoxCrashed; } catch { }

        // Phase 2D-4: tear down the owned ClashSingBoxApi (and its
        // HttpClient) if the ctor created one. External fakes are the
        // caller's responsibility.
        _ownedApi?.Dispose();
    }
}
