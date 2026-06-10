using System.IO;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Unified VPN engine that encapsulates the full lifecycle:
/// load config → resolve profile → scan processes → generate sing-box config →
/// firewall → start sing-box → ETW monitor → health monitor.
///
/// Used by GUI (in-process), and can replace CLI/Service startup logic.
/// </summary>
public class VpnEngine : IDisposable
{
    private SingBoxManager? _singBox;
    private HealthMonitor? _healthMonitor;
    private IProcessMonitor? _etw;
    private IFirewallManager? _firewall;
    private Profile? _activeProfile;
    private ScanResult? _scanResult;
    private readonly IProcessScanner _scanner;
    private readonly Func<IFirewallManager> _firewallFactory;
    private readonly Func<IProcessMonitor> _monitorFactory;
    private readonly IWindowsDnsHardening _dnsHardening;
    // Fix #1 (v2.41.0 r3): macOS/Linux DNS-leak hardening seam. Defaults to a
    // no-op; PlatformServices supplies MacDnsHardening on macOS. Gated behind
    // the DnsLeakLockdown setting in StartAsync (opt-in leak protection).
    private readonly IUnixDnsHardening _unixDns;
    private readonly ILogger? _logger;

    // F-E (2026-05-11): runtime safety net for dead/placeholder configs.
    // Created lazily on first StartAsync to avoid leaking HttpClient when
    // VpnEngine is constructed but never used. Lives for the engine's
    // lifetime so cycle state (tried-server list) survives back-to-back
    // failovers; reset on every successful start.
    private ConfigSanityCheck? _sanityCheck;
    private AutoFailoverEngine? _failover;
    // Post-start probe runs in the background; we cancel it on Stop so a
    // queued probe doesn't fire failover after the user manually disconnects.
    private CancellationTokenSource? _probeCts;

    // DNS-tunnel (slipstream) transport sidecar. Created lazily by the startup
    // host ONLY when the active server is dns-tunnel; null for every other
    // server type. Stopped in Stop() after sing-box. See
    // plans/dns-tunnel-slipstream-integration-2026-06-10.md.
    private SlipstreamManager? _slipstream;

    private bool _disposed;

    // ─── Public state ────────────────────────────────────────────────────────

    public bool IsRunning => _singBox?.IsRunning() ?? false;
    public string ActiveProfileName => _activeProfile?.Name ?? string.Empty;
    public int? SingBoxPid => _singBox?.Pid;
    public List<string> MonitoredProcesses => _scanResult?.ProcessNames ?? new();

    /// <summary>"custom" or "generated" — set during StartAsync.</summary>
    public string ActiveConfigMode { get; private set; } = "generated";

    /// <summary>"split" or "full" — set during StartAsync.</summary>
    public string ActiveRoutingMode { get; private set; } = "split";

    /// <summary>IP/host of the active server (for status display).</summary>
    public string ActiveServerAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Fingerprint of TUN-layer settings taken at the last successful
    /// start/restart. Compared against the incoming AppSettings on every
    /// ApplyAsync — any mismatch escalates to <c>forceRestart = true</c>
    /// because TUN interface changes (name / IP / MTU / auto_route /
    /// strict_route / route_exclude_address) can't be re-laid by sing-box's
    /// Clash API hot-reload; the adapter has to be destroyed and recreated.
    ///
    /// <para>v2.27.2: introduced alongside RoutingMode auto-detect (v2.27.1)
    /// as part of the "structural change self-heal" family. Callers should
    /// NEVER need to manually pass <c>forceRestart = true</c> — the engine
    /// figures it out.</para>
    /// </summary>
    internal string TunFingerprint { get; private set; } = string.Empty;

    // ─── Events for UI ───────────────────────────────────────────────────────

    /// <summary>Fired when engine status changes (e.g. "Loading profiles...", "sing-box started")</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fired when a targeted process is detected by ETW</summary>
    public event Action<string, int>? ProcessDetected;

    /// <summary>Fired on sing-box restart attempt (attemptNumber, maxAttempts)</summary>
    public event Action<int, int>? RestartAttempted;

    /// <summary>Fired on validation warnings</summary>
    public event Action<string>? Warning;

    /// <summary>Fired after every successful sing-box launch (initial start
    /// AND any restart). Consumers use this to keep persisted state (CLI
    /// state.json, GUI banner PID, doctor lockfile) in sync with the live
    /// process — without it, stop/status commands race against stale PIDs
    /// the moment HealthMonitor does its first restart.</summary>
    public event Action<int>? SingBoxStarted;

    /// <summary>
    /// Fires EXACTLY ONCE per successful Start lifecycle when the TUN warmup
    /// probe (<see cref="StartupPipeline.ScheduleWarmupProbe"/>) confirms
    /// <c>gstatic.com</c> is reachable through the tunnel. Payload is the
    /// sing-box PID.
    ///
    /// <para>Does NOT fire when warmup fails (the 15-attempt loop expires) —
    /// callers wanting the "warmup loop ended" signal must subscribe to
    /// <see cref="StatusChanged"/> and filter by the specific
    /// <c>"Connected (PID N)"</c> message text. That string is emitted on
    /// BOTH the success and failure branches of the warmup loop, which is
    /// exactly the ambiguity this typed event resolves.</para>
    ///
    /// <para>Task #41 Stage 1 (PinkuDani 2026-05-21): added so the App-side
    /// VM can split the connect timeout into Phase A (start budget — wait
    /// for sing-box to come up) + Phase B (TUN warm-up budget — wait for
    /// confirmed routability). See
    /// <c>plans/phase4-vpnengine-connected-event-stage1-2026-05-21.md</c>
    /// for the design rationale. Stage 2 (App-side two-phase timer in
    /// <c>MainWindowViewModel</c>) is a separate change.</para>
    /// </summary>
    public event Action<int>? Connected;

    /// <summary>F-E (2026-05-11): fired when <see cref="AutoFailoverEngine"/>
    /// switches to a different server after the pre-start sanity check or
    /// post-start Clash API probe flagged the active one as dead. Carries
    /// the user-readable message (e.g. "Переключение на сервер: backup-2").
    /// Consumers (App / CLI) surface this as a toast or status line so the
    /// user understands why the active server changed mid-connect.</summary>
    public event Action<string>? AutoFailoverTriggered;

    /// <summary>
    /// Construct a <see cref="VpnEngine"/> with explicit dependencies.
    ///
    /// <para><b>3G-4 (v3.0 refactor):</b> direct construction is deprecated —
    /// use <see cref="VPNRouter.Core.Platform.PlatformServices.CreateVpnEngine"/>
    /// instead so the platform-specific scanner/firewall/monitor wiring
    /// stays in one place. The attribute is warning-only
    /// (<c>error: false</c>) so existing call sites (CLI's StartCommand,
    /// Service's VPNRouterService — both predating the factory introduction)
    /// keep compiling while we migrate them in Phase 4. New code MUST
    /// use the factory; tests that need to inject a fake scanner / firewall
    /// can suppress the warning with <c>#pragma warning disable CS0618</c>
    /// or via the factory's overloads (Phase 4 will add a test-friendly
    /// builder).</para>
    /// </summary>
    [Obsolete(
        "Use PlatformServices.CreateVpnEngine — direct construction bypasses " +
        "the platform-specific scanner / firewall / monitor wiring. This " +
        "warning is non-fatal during Phase 3; will become an error in " +
        "Phase 4 once all call sites are migrated.",
        error: false)]
    public VpnEngine(
        IProcessScanner scanner,
        Func<IFirewallManager> firewallFactory,
        Func<IProcessMonitor> monitorFactory,
        ILogger? logger = null,
        IWindowsDnsHardening? dnsHardening = null,
        IUnixDnsHardening? unixDnsHardening = null)
    {
        _scanner = scanner;
        _firewallFactory = firewallFactory;
        _monitorFactory = monitorFactory;
        _logger = logger;
        // Task #36-A (Phase 4): the DNS-hardening seam. Defaults to the
        // back-compat singleton that wraps the static WindowsDnsHardening
        // facade (no-op on non-Windows). Tests pass NullWindowsDnsHardening
        // so the happy-path lifecycle suite (Task #36-C) doesn't mutate the
        // CI / dev machine's machine-wide DNS policy in HKLM.
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;
        // Fix #1 (r3): Unix DNS hardening seam (NullUnixDnsHardening = no-op on
        // Windows / in tests; MacDnsHardening on macOS via PlatformServices).
        _unixDns = unixDnsHardening ?? NullUnixDnsHardening.Default;
        // Crash-recovery: if a prior session crashed while DNS was pinned to the
        // TUN, heal the stranded system resolver before doing anything else.
        try { _unixDns.RestoreStrandedIfAny(_logger); } catch { }
    }

    // ─── Start ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Full VPN startup sequence. Throws on fatal errors.
    /// Checks CancellationToken after each major step to allow clean abort
    /// when the service receives a stop signal during startup.
    /// </summary>
    /// <param name="skipVpnConflictCheck">
    /// v2.32.1-r5 (Bug-r10-B): when true, skips the
    /// <see cref="ConflictingVpnDetector"/> pre-flight check. UI surfaces
    /// an «Игнорировать» button alongside «Завершить» in the conflict
    /// banner — clicking it sets a session-scoped flag the VM passes here.
    /// Use case: AmneziaVPN GUI client sitting idle in tray (process running
    /// but wintun not held), or multi-adapter setups where 2 VPNs coexist.
    /// If the user is wrong, sing-box's own adapter-creation will still fail
    /// downstream with the wintun «file already exists» error — recoverable,
    /// not destructive.
    /// </param>
    public async Task StartAsync(AppSettings settings, CancellationToken ct = default, bool skipVpnConflictCheck = false)
    {
        // Phase 3C (2026-05-18): the 750-LOC inline sequence that used to
        // live here moved into StartupPipeline. The pipeline walks the 8
        // canonical phases (Resolve -> Scan -> Generate -> PreStartChecks
        // -> Deploy/Firewall -> StartSingBox -> StartMonitors) and mutates
        // this engine's state via the IStartupHost callbacks implemented
        // at the bottom of this file. See
        // plans/phase3-3C-startup-pipeline-2026-05-18.md and the file-
        // header comment in StartupPipeline.cs for the rationale.
        var host = new VpnEngineStartupHost(this);
        // Task #36-A — plumb the DNS-hardening seam from the engine into
        // the pipeline so Null* test doubles installed at engine construction
        // propagate down to phase 7 (BR-7 deferred lockdown) + phase 8
        // (Apply) without separate wiring.
        var pipeline = new StartupPipeline(host, dnsHardening: _dnsHardening);
        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.ColdStart, skipVpnConflictCheck),
            ct);

        // F-E early-return path: AutoFailoverEngine re-entered StartAsync
        // via the restart delegate (see WireFailover below). The inner call
        // has already completed all 8 phases; the outer must NOT continue
        // or it would double-launch sing-box.
        if (result.EarlyReturn)
        {
            _logger?.Information(
                "[VpnEngine] StartAsync: F-E re-entry handled by inner call (outer aborting)");
            return;
        }

        // Fix #1 (r3): macOS DNS-leak hardening — pin the system resolver to the
        // TUN gateway so mDNSResponder's queries enter the tunnel instead of
        // leaking to the ISP (the diagnosed macOS leak). Opt-in via the same
        // DnsLeakLockdown toggle as the Windows lockdown. No-op on Windows /
        // Linux (NullUnixDnsHardening); MacDnsHardening itself is best-effort
        // and degrades to no-op if the networksetup sudoers grant is absent.
        if (settings.App.DnsLeakLockdown)
        {
            try
            {
                var gateway = VPNRouter.Core.Platform.Unix.MacDnsParsers
                    .DeriveDnsTarget(settings.Tun?.Ipv4Address);
                if (!string.IsNullOrEmpty(gateway))
                    _unixDns.Apply(gateway!, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[VpnEngine] Unix DNS hardening Apply failed (non-fatal)");
            }
        }
    }

    // ─── Apply (hot-reload config changes) ──────────────────────────────────

    /// <summary>
    /// Hot-reload config after user changed app list (groups, custom apps, categories).
    /// Re-resolves profile, re-scans processes, regenerates sing-box config,
    /// then tries Clash API hot-reload. Falls back to full restart on failure.
    /// Returns true on success (either hot-reload or restart).
    ///
    /// <para>
    /// v2.20.4: <paramref name="forceRestart"/> bypasses the hot-reload
    /// attempt and goes straight to stop+launch. Callers changing
    /// structural things — especially <see cref="AppSettings.AppConfig.RoutingMode"/>
    /// (split ↔ full) — must pass true. sing-box's Clash API <c>PUT /configs</c>
    /// accepts the new config and reports success, but the TUN route table
    /// and DNS rules from the previous config remain active for already-
    /// established connections. Users saw "toggle does nothing" because
    /// the API returned 200 and we returned success. A full process restart
    /// is the only way to guarantee the new routing mode takes effect.
    /// </para>
    /// </summary>
    public async Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct = default, bool forceRestart = false)
    {
        if (_singBox == null || !_singBox.IsRunning())
        {
            _logger?.Warning("[VpnEngine] Apply called but sing-box not running");
            return false;
        }

        OnStatus("Applying config changes...");

        try
        {
            // Phase 3C (2026-05-18): run phases 1-4 of the StartupPipeline
            // in HotReload mode to regenerate the sing-box JSON. The
            // pipeline does NOT touch sing-box / firewall / ETW /
            // HealthMonitor in HotReload mode -- those are already up and
            // carrying state. Closes Phase 2F-A follow-up: the third
            // inline pipeline that pre-3C lived in this method is now
            // single-sourced through StartupPipeline.
            //
            // Pre-3C this method had ~200 LOC of duplicate orchestration
            // (Resolve+merge profile, scan processes, generate config,
            // validate, snapshot oldProcessSet for structural-change
            // detection). The pipeline now handles the regen; we keep
            // ONLY the hot-reload-specific structural-change detection
            // (RoutingMode mismatch, TunFingerprint mismatch, process-
            // list change) and the ReloadConfigJson call here -- those
            // are intrinsic to the Apply path and don't belong in the
            // pipeline.
            var oldProcessSet = (_scanResult?.ProcessNames ?? new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var host = new VpnEngineStartupHost(this);
            // Task #36-A — same as StartAsync: propagate the DNS-hardening
            // seam into the pipeline. HotReload itself doesn't fire phase 7/8,
            // but keeping the wiring symmetric means future evolution of the
            // HotReload phase mask doesn't have to be re-plumbed.
            var pipeline = new StartupPipeline(host, dnsHardening: _dnsHardening);
            var result = await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                ct);

            if (!result.Success || result.ConfigJson == null)
            {
                _logger?.Warning("[VpnEngine] Apply: pipeline returned no config JSON");
                return false;
            }

            var configJson = result.ConfigJson;

            // v2.27.1 -- auto-detect structural changes that hot-reload
            // CAN'T pick up.
            var newRoutingMode = (settings.App.RoutingMode ?? "split").ToLowerInvariant();
            if (!string.Equals(newRoutingMode, ActiveRoutingMode, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Information(
                    "[VpnEngine] RoutingMode change detected ({Old} -> {New}) -- escalating to full restart so TUN routes are re-laid",
                    ActiveRoutingMode, newRoutingMode);
                forceRestart = true;
            }

            // v2.27.2: TUN-layer structural change detection.
            var newTunFingerprint = ComputeTunFingerprint(settings.Tun);
            if (!string.Equals(newTunFingerprint, TunFingerprint, StringComparison.Ordinal))
            {
                _logger?.Information(
                    "[VpnEngine] TUN settings change detected -- escalating to full restart. Old fingerprint {Old}, new {New}",
                    TunFingerprint, newTunFingerprint);
                forceRestart = true;
            }

            // v2.31.8-r4: detect process list mutations that hot-reload
            // can't honour for ALREADY-OPEN sockets.
            var newProcessSet = (_scanResult?.ProcessNames ?? new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!oldProcessSet.SetEquals(newProcessSet))
            {
                var added = newProcessSet.Except(oldProcessSet).ToList();
                var removed = oldProcessSet.Except(newProcessSet).ToList();
                _logger?.Information(
                    "[VpnEngine] Process list change detected (+{AddedCount}: {Added} / -{RemovedCount}: {Removed}) -- escalating to full restart so existing TCP connections rejoin under new rules",
                    added.Count, string.Join(",", added),
                    removed.Count, string.Join(",", removed));
                forceRestart = true;
            }

            // Try hot-reload first, UNLESS caller explicitly asked for a
            // full restart (v2.20.4 + v2.27.1 auto-detect above).
            if (!forceRestart && _singBox.TryReloadConfigJson(configJson))
            {
                OnStatus($"Applied (hot-reload, PID {_singBox.Pid})");
                _logger?.Information("[VpnEngine] Applied via hot-reload");
                return true;
            }

            if (forceRestart)
                _logger?.Information("[VpnEngine] Forced full restart (structural change)");
            else
                _logger?.Warning("[VpnEngine] Hot-reload failed, falling back to full restart");

            // v2.31.7-r1: pass through forceRestart so the structural-
            // change intent reaches sing-box.
            _singBox.ReloadConfigJson(configJson, forceRestart);
            ActiveRoutingMode = newRoutingMode;
            TunFingerprint = newTunFingerprint;
            OnStatus($"Applied (restart, PID {_singBox.Pid})");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[VpnEngine] Apply failed");
            OnStatus($"Apply failed: {ex.Message}");
            return false;
        }
    }

    // ─── Stop ────────────────────────────────────────────────────────────────

    public void Stop()
    {
        OnStatus("Stopping...");

        // BR-5 (brat 2026-05-19) — re-ordered so sing-box dies EARLY,
        // before the firewall cleanup that previously took 2-3 seconds.
        // brat-2026-05-19 reported "internet from the required IP
        // appeared for a couple of seconds after turning off" — the
        // log showed a 2.3-second window between [VpnEngine] Stopping
        // and [SingBoxManager] Stopping sing-box, during which the
        // VPN was still routing through the live wintun adapter.
        //
        // BR-6a (audit follow-up 2026-05-20) — refined ordering after
        // the r9 audit caught a race window: in r9 the order was
        //   _probeCts.Cancel → _singBox.Stop → ... → _healthMonitor.Stop
        // That left a ~50-200 ms window in which the HealthMonitor's
        // periodic timer (30s interval) could observe sing-box dead +
        // _vpnWasRunning true and fire AttemptRestart — a false
        // restart of the VPN immediately after the user pressed Stop.
        // The branch in OnHealthTick does NOT check _isStopping, so
        // the only safe ordering is "stop the monitor before killing
        // its target". HealthMonitor.Stop is fast (~ms — disposes a
        // Timer) so doing it first costs nothing on the user-visible
        // disconnect path.
        try { _probeCts?.Cancel(); } catch { }
        try { _healthMonitor?.Stop(); } catch { }  // BR-6a: BEFORE sing-box
        try { _singBox?.Stop(); } catch { }
        // DNS-tunnel: tear the transport down AFTER sing-box (the outbound that
        // rode it is already gone). No-op for every non-dns-tunnel session
        // (_slipstream stays null unless a dns-tunnel server was started).
        try { _slipstream?.Stop(); } catch { }

        // Task #36-A (Phase 4) — routed through IWindowsDnsHardening so the
        // happy-path Stop test (Task #36-C) captures the Restore invocation
        // through NullWindowsDnsHardening without mutating HKLM. Impl is a
        // no-op on non-Windows builds; the prior PLATFORM_WINDOWS guard at
        // the call site collapsed into the impl itself.
        try { _dnsHardening.Restore(_logger); } catch { }
        // Fix #1 (r3): restore the macOS system resolver pinned at connect.
        try { _unixDns.Restore(_logger); } catch { }

        try { _etw?.Stop(); } catch { }

        if (_activeProfile?.BlockOnVpnFail == true)
        {
            try { _firewall?.DisableBlockRules(); } catch { }
            try { _firewall?.DeleteAllRules(); } catch { }
        }

        try { _firewall?.Dispose(); } catch { }

        _singBox = null;
        _slipstream = null;
        _healthMonitor = null;
        _etw = null;
        _firewall = null;

        // v2.27.2 — passive diagnostic: log TUN adapter state *after* a
        // graceful stop. If the adapter persists in netsh after
        // sing-box has exited cleanly, we've got a wintun/sing-box
        // driver-level leak. Useful for correlating with user reports
        // like "after heavy toggling, TUN adapter 'sticks' and a new
        // start either fails or reuses stale routes".
        try
        {
            if (OperatingSystem.IsWindows())
                TunAdapterDiagnostics.LogAdapterState(_logger, "VpnEngine.after-stop");
        }
        catch { /* diagnostics must never throw */ }

        OnStatus("Stopped");
        _logger?.Information("[VpnEngine] Stopped");
    }

    // ─── Config resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the custom config file path. Priority:
    /// 1. ActiveCustomConfig name → look up in CustomConfigs list → ProgramData path
    /// 2. Fallback to single CustomConfig path (backward compat)
    /// </summary>
    internal static string ResolveCustomConfigPath(AppSettings settings)
    {
        // Try multi-config list
        if (settings.App.CustomConfigs?.Count > 0)
        {
            // Pick active config by name, or fall back to first in list
            var entry = !string.IsNullOrEmpty(settings.App.ActiveCustomConfig)
                ? settings.App.CustomConfigs
                    .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig)
                    ?? settings.App.CustomConfigs[0]
                : settings.App.CustomConfigs[0];

            var path = Environment.ExpandEnvironmentVariables(entry.Path);
            if (File.Exists(path))
                return path;

            var pdPath = CustomConfigInjector.GetProgramDataPath(entry.Name);
            if (File.Exists(pdPath))
                return pdPath;
        }

        // Fallback: single custom_config path (backward compat)
        return Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRunning) Stop();
        else
        {
            // v2.40.0 Phase C (C1-1): a mid-Start exception can leave firewall
            // block rules created (StartupPipeline Phase 6) and/or DNS hardening
            // applied (Phase 8) while sing-box never reached Running. Stop() is
            // the only teardown path, but Dispose only calls it when IsRunning —
            // so on the CLI/Service failure paths (which Dispose the engine
            // without an explicit Stop) that partial state would orphan the
            // user's firewall/registry until the next-launch sweep. Tear it down
            // here, best-effort. Idempotent: Restore + firewall Dispose
            // (DeleteAllRules) are safe on already-clean / never-started state.
            try { _dnsHardening.Restore(_logger); } catch { }
            try { _unixDns.Restore(_logger); } catch { }  // Fix #1 (r3): mac DNS partial-start cleanup
            try { _firewall?.Dispose(); } catch { }   // Dispose -> DeleteAllRules
            _firewall = null;
        }
        try { _probeCts?.Cancel(); } catch { }
        try { _probeCts?.Dispose(); } catch { }
        _probeCts = null;
        GC.SuppressFinalize(this);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void OnStatus(string message)
    {
        _logger?.Information("[VpnEngine] {Status}", message);
        StatusChanged?.Invoke(message);
    }

    /// <summary>
    /// F-E helper — parse "host:port" into just the port. Returns the
    /// sing-box default (9090) on parse failure so the probe still tries.
    /// </summary>
    internal static int ParseClashApiPort(string? hostPort)
    {
        const int Default = 9090;
        if (string.IsNullOrWhiteSpace(hostPort)) return Default;
        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx < 0 || colonIdx == hostPort.Length - 1) return Default;
        var portStr = hostPort[(colonIdx + 1)..];
        return int.TryParse(portStr, out var port) && port > 0 && port <= 65535
            ? port
            : Default;
    }

    /// <summary>
    /// Build a stable fingerprint of TUN-layer settings for change detection
    /// between StartAsync and ApplyAsync. Any field here that changes means
    /// the adapter needs to be rebuilt — Clash API hot-reload CANNOT re-lay
    /// kernel-level TUN properties.
    ///
    /// <para>Exposed as <c>internal</c> (not public) because tests live in
    /// the Tests project which has InternalsVisibleTo via friend assembly
    /// attribute. Production callers should compare
    /// <see cref="TunFingerprint"/> values instead of recomputing.</para>
    /// </summary>
    internal static string ComputeTunFingerprint(Models.TunSettings tun)
    {
        // Order-independent join of the exclude list — users might reorder
        // entries in the UI without any structural change intended.
        var excludes = tun.RouteExcludeAddress ?? new List<string>();
        var excludeKey = string.Join(",",
            excludes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal));

        return string.Join("|",
            (tun.InterfaceName ?? "").Trim().ToLowerInvariant(),
            (tun.Ipv4Address ?? "").Trim().ToLowerInvariant(),
            tun.Ipv6Enabled ? "1" : "0",
            tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tun.AutoRoute ? "1" : "0",
            tun.StrictRoute ? "1" : "0",
            excludeKey);
    }

    /// <summary>
    /// v2.31.6-r10 (Phase F) — consolidated user-customization merge step.
    /// Mutates <paramref name="collection"/> in place by:
    ///
    /// <list type="bullet">
    ///   <item>Adding entries from <c>settings.CustomGroupApps</c> as
    ///   ProcessRule items on existing profiles whose name matches the
    ///   group key (case-insensitive). Skips dupes — a name already
    ///   present in the profile's Processes is left alone.</item>
    ///   <item>Injecting entries from <c>settings.CustomCategories</c>
    ///   as new <see cref="Profile"/> instances appended to the
    ///   collection. Skips categories whose Name collides with an
    ///   existing profile (the existing profile wins; user can rename
    ///   in CustomCategories to disambiguate).</item>
    /// </list>
    ///
    /// <para>App names are normalised to <c>foo.exe</c> form (extension
    /// added if missing) — sing-box's <c>process_name</c> matcher is
    /// case-sensitive but extension-aware, and this is the canonical
    /// shape the rest of the pipeline uses.</para>
    ///
    /// <para><b>SafeMode handling</b>: this helper does NOT itself
    /// check <see cref="SafeMode.Enabled"/>. The caller decides:
    /// <see cref="StartAsync"/> wraps the call in
    /// <c>if (!SafeMode.Enabled)</c> (so safe mode bypasses customization
    /// at boot — the entire point of safe mode). <see cref="ApplyAsync"/>
    /// calls unconditionally — preserving the pre-r10 asymmetry where
    /// a hot-reload Apply still merges customization even when a
    /// previous safe-mode boot would have skipped it. If we ever decide
    /// Apply should also respect safe mode, that's a separate
    /// behavioural change.</para>
    ///
    /// <para>Pre-r10 this body was duplicated ~50 LOC verbatim across
    /// StartAsync and ApplyAsync — a silent-leak class of bug if the
    /// two ever drifted (and they did briefly drift in v2.28.2's
    /// initial fix attempt, caught by the regression suite). The
    /// extraction makes the merge behaviour single-sourced.</para>
    /// </summary>
    internal static void MergeUserCustomization(
        ProfileCollection collection,
        AppSettings settings)
    {
        // Merge user-added apps into existing default groups.
        if (settings.CustomGroupApps?.Count > 0)
        {
            foreach (var (groupName, extras) in settings.CustomGroupApps)
            {
                var profile = collection.Profiles.FirstOrDefault(p =>
                    p.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (profile == null) continue;
                foreach (var app in extras ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    if (profile.Processes.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
            }
        }

        // Inject user-created categories as new profiles.
        if (settings.CustomCategories?.Count > 0)
        {
            foreach (var cat in settings.CustomCategories)
            {
                if (string.IsNullOrWhiteSpace(cat.Name)) continue;
                if (collection.Profiles.Any(p => p.Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var profile = new Profile
                {
                    Name = cat.Name,
                    Description = "User category",
                    DnsMode = "vpn_only",
                    BlockOnVpnFail = false,
                    Processes = new List<ProcessRule>()
                };
                foreach (var app in cat.Apps ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
                collection.Profiles.Add(profile);
            }
        }
    }

    /// <summary>
    /// Bug-r9-I (2026-05-11): drop process rules for any app the user has
    /// individually unchecked from the Applications tab. Operates on the
    /// fully-prepared <paramref name="profile"/> (post-merge,
    /// post-CustomApps), so an excluded name is removed regardless of
    /// which pathway put it on the list — bundled profile, user-added
    /// group extra, or top-level CustomApps.
    ///
    /// <para><b>Suffix variance</b>: callers persist process names in their
    /// view form (with <c>.exe</c> on Windows, stripped on macOS/Linux),
    /// while the engine pipeline normalises everything to <c>foo.exe</c>
    /// via <c>NormalizeName</c>. The matcher below strips <c>.exe</c> from
    /// both sides before comparing so the variance doesn't cause silent
    /// misses (e.g. user persists <c>firefox</c> on Linux, engine sees
    /// <c>firefox.exe</c> after MergeUserCustomization).</para>
    ///
    /// <para>No-op when <paramref name="excludedApps"/> is null or empty —
    /// the common case (no exclusions configured).</para>
    /// </summary>
    internal static void RemoveExcludedApps(Profile? profile, IReadOnlyList<string>? excludedApps)
    {
        if (profile == null) return;
        if (excludedApps == null || excludedApps.Count == 0) return;

        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in excludedApps)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            excludeSet.Add(StripExeSuffix(raw));
        }
        if (excludeSet.Count == 0) return;

        profile.Processes.RemoveAll(p =>
            p != null && !string.IsNullOrEmpty(p.Name)
            && excludeSet.Contains(StripExeSuffix(p.Name)));
    }

    private static string StripExeSuffix(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    internal static List<IProfileSource> BuildProfileSources(AppSettings settings)
    {
        var sources = new List<IProfileSource>();
        int priority = 10;

        foreach (var src in settings.ProfileSources)
        {
            switch (src.Type?.ToLowerInvariant())
            {
                case "github" when !string.IsNullOrEmpty(src.Url):
                    sources.Add(new GitHubProfileSource(src.Url, priority));
                    break;
                case "local" when !string.IsNullOrEmpty(src.Path):
                    sources.Add(new LocalProfileSource(src.Path, priority + 10));
                    break;
            }
            priority += 10;
        }

        // v2.21.9: platform-aware bundled profiles. Previously BuildProfileSources
        // always loaded default.json (Windows layout with .exe process names
        // and group names like "Discord_Privacy" / "Work_Suite" / "Browsers" /
        // "Terminal"). On Linux SettingsLoader + MainWindowViewModel already
        // route to default-linux.json for UI load/display, but this engine
        // path still pulled default.json at runtime — so Apply ran with the
        // WRONG profile catalogue. User hit "Profile 'Messengers' not found"
        // because the UI offered Linux-style profiles but the engine only
        // knew Windows-style ones.
        //
        // Now we prefer the platform-specific variant if it exists, and
        // fall back to default.json so Windows builds keep working.
        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));

        // Generic default.json always added as a fallback at slightly lower
        // priority so profiles referenced by BOTH files (e.g. SimpleSplit's
        // Browsers + Discord_Privacy + Work_Suite) resolve against the
        // platform variant first.
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));

        // User profiles directory under AppPaths (where ProfilesDir lives)
        var platformProfiles = Path.Combine(AppPaths.ProfilesDir, platformDefaultName);
        if (File.Exists(platformProfiles))
            sources.Add(new LocalProfileSource(platformProfiles, 85));
        var userDefault = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userDefault) && !userDefault.Equals(platformProfiles, StringComparison.Ordinal))
            sources.Add(new LocalProfileSource(userDefault, 83));

        // Built-in fallback
        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    /// <summary>
    /// Safe-mode variant: only the bundled catalogue next to the exe,
    /// then hard-coded BuiltInProfiles. No user-level file, no yaml
    /// ProfileSources. Guarantees we never touch a potentially broken
    /// user override when the user has chosen --safe.
    /// </summary>
    internal static List<IProfileSource> BuildBundledOnlyProfileSources()
    {
        var sources = new List<IProfileSource>();
        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));
        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    // ─── v2.22.4 self-healing: stale user-catalogue quarantine ──────────

    private static readonly string[] ExpectedV222Groups =
    {
        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
    };

    /// <summary>
    /// Rename a user-level <c>profiles/default.json</c> that doesn't match
    /// the v2.22 group schema. Triggered at start of every StartAsync so
    /// stale catalogues from older installs can't deadlock ProcessScanner.
    /// Errors are swallowed — never fatal.
    /// </summary>
    internal static void QuarantineStaleUserCatalogue(ILogger? logger)
    {
        try
        {
            var userPath = Path.Combine(AppPaths.ProfilesDir, "default.json");
            if (!File.Exists(userPath)) return;

            bool shouldQuarantine = false;
            string reason = "";

            try
            {
                var json = File.ReadAllText(userPath);
                var collection = JsonSerializer.Deserialize(
                    json, Json.AppJsonContext.Default.ProfileCollection);
                if (collection == null || collection.Profiles == null || collection.Profiles.Count == 0)
                {
                    shouldQuarantine = true;
                    reason = "empty or unparseable";
                }
                else
                {
                    var present = new HashSet<string>(
                        collection.Profiles.Select(p => p.Name),
                        StringComparer.OrdinalIgnoreCase);
                    var missing = ExpectedV222Groups.Count(g => !present.Contains(g));
                    // Heuristic: if ≥3 of 8 standard v2.22 groups absent,
                    // this is an older-schema catalogue. Just one or two
                    // missing — could be legitimate user customization.
                    if (missing >= 3)
                    {
                        shouldQuarantine = true;
                        reason = $"{missing} of {ExpectedV222Groups.Length} standard groups missing";
                    }
                }
            }
            catch (Exception ex)
            {
                shouldQuarantine = true;
                reason = $"parse error: {ex.Message}";
            }

            if (!shouldQuarantine) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{userPath}.migrated-{stamp}";
            File.Move(userPath, backup);
            logger?.Warning(
                "[VpnEngine] Quarantined stale user catalogue {Path} ({Reason}) → {Backup}. Using bundled defaults.",
                userPath, reason, backup);
        }
        catch (Exception ex)
        {
            // Quarantine is best-effort. If we can't even rename it,
            // let it through — the catalogue load will either succeed or
            // skip to fallback sources naturally.
            logger?.Warning(ex, "[VpnEngine] Could not quarantine stale user catalogue");
        }
    }

    // ─── Phase 3C: IStartupHost / StartupHostInternal implementation ────────
    //
    // VpnEngineStartupHost is the adapter that lets StartupPipeline mutate
    // VpnEngine's lifecycle state + raise public events without the pipeline
    // having to reach into private fields directly. Constructed fresh on every
    // StartAsync / ApplyAsync call so the pipeline can't accidentally retain a
    // stale reference to a disposed engine.
    //
    // Why nested: gives the host class direct access to VpnEngine's private
    // fields (_singBox, _firewall, _sanityCheck, etc.) without making them
    // internal. Closer to a friend-class pattern; safer than promoting half the
    // engine's state to internal getters.
    private sealed class VpnEngineStartupHost : StartupHostInternal
    {
        private readonly VpnEngine _engine;

        public VpnEngineStartupHost(VpnEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public ILogger? Logger => _engine._logger;
        public IProcessScanner Scanner => _engine._scanner;
        public Func<IFirewallManager> FirewallFactory => _engine._firewallFactory;
        public Func<IProcessMonitor> MonitorFactory => _engine._monitorFactory;

        public SingBoxManager? SingBox => _engine._singBox;
        public IFirewallManager? Firewall => _engine._firewall;

        public void OnStatus(string message) => _engine.OnStatus(message);

        public void OnWarning(string message) => _engine.Warning?.Invoke(message);

        public void OnSingBoxStarted(int pid) => _engine.SingBoxStarted?.Invoke(pid);

        // Task #41 Stage 1 (2026-05-21) — fire the typed Connected event on
        // the engine ONLY when the pipeline's warmup probe success branch
        // calls into this. StartupPipeline does not invoke this from the
        // failure branch (the 15-attempt loop expiring still emits the
        // "Connected (PID N)" StatusChanged string for back-compat, but
        // the typed event stays silent — that's the invariant Stage 2's
        // App-side two-phase timer depends on).
        public void OnConnected(int pid) => _engine.Connected?.Invoke(pid);

        public void OnRestartAttempted(int attempt, int max) =>
            _engine.RestartAttempted?.Invoke(attempt, max);

        public void OnAutoFailoverTriggered(string message) =>
            _engine.AutoFailoverTriggered?.Invoke(message);

        public void OnProcessDetected(string name, int pid) =>
            _engine.ProcessDetected?.Invoke(name, pid);

        public void SetActiveServerAddress(string address) =>
            _engine.ActiveServerAddress = address;

        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint)
        {
            _engine.ActiveConfigMode = configMode;
            _engine.ActiveRoutingMode = routingMode;
            _engine.TunFingerprint = tunFingerprint;
        }

        public void SetActiveProfile(Profile profile) => _engine._activeProfile = profile;

        public void SetScanResult(ScanResult result) => _engine._scanResult = result;

        public void SetSingBoxManager(SingBoxManager manager) => _engine._singBox = manager;

        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings)
        {
            // The DNS-leak lockdown blocks the very DNS queries slipstream needs
            // to reach the НСДИ resolvers (the tunnel's own transport). Warn, don't
            // hard-fail — the user may have allow-rules. (Opt-in, off by default.)
            if (settings.App?.DnsLeakLockdown == true)
                _engine._logger?.Warning(
                    "[VpnEngine] dns-tunnel active WITH DnsLeakLockdown — the lockdown may block " +
                    "slipstream-client's DNS to the resolvers (the tunnel's own transport). " +
                    "If the tunnel won't connect, disable DnsLeakLockdown.");

            var slip = _engine._slipstream ??= new SlipstreamManager(_engine._logger);
            _engine.OnStatus("Starting DNS-tunnel transport...");
            slip.Start(activeServer, SlipstreamManager.DefaultLocalPort); // throws → fail-closed

            // Fail-closed: confirm the local port is actually accepting before
            // sing-box is told to dial it.
            if (!slip.WaitForPortListening(5000))
            {
                slip.Stop();
                throw new SlipstreamException(
                    "slipstream-client did not start listening on 127.0.0.1:" +
                    SlipstreamManager.DefaultLocalPort + " within 5s");
            }
            _engine.OnStatus("DNS-tunnel transport up (127.0.0.1:" + SlipstreamManager.DefaultLocalPort + ")");
        }

        public void SetFirewallManager(IFirewallManager firewall) => _engine._firewall = firewall;

        public void SetProcessMonitor(IProcessMonitor etw) => _engine._etw = etw;

        public void SetHealthMonitor(HealthMonitor monitor) => _engine._healthMonitor = monitor;

        /// <summary>
        /// Lazily create the ConfigSanityCheck instance. Pre-3C this was
        /// inlined as `_sanityCheck ??= new ConfigSanityCheck(_logger)` in two
        /// places inside StartAsync. The cycle state on _sanityCheck +
        /// _failover lives for the engine's lifetime so back-to-back failovers
        /// remember which servers were tried; a successful start does NOT
        /// clear it (matching pre-3C behaviour).
        /// </summary>
        public void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck)
        {
            CaptureSettings(settings);
            _engine._sanityCheck ??= new ConfigSanityCheck(_engine._logger);
            sanityCheck = _engine._sanityCheck;
        }

        /// <summary>
        /// Wire AutoFailoverEngine with a restart delegate that calls back
        /// into StartAsync. Used by the pre-start F-E check (phase 5).
        /// </summary>
        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck)
        {
            // The pre-start check hasn't started sing-box yet, so the restart
            // delegate just re-enters StartAsync without a Stop call.
            _engine._failover ??= new AutoFailoverEngine(
                CapturedSettings(),
                sanityCheck,
                restart: async (innerCt) =>
                {
                    try
                    {
                        await _engine.StartAsync(CapturedSettings(), innerCt);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _engine._logger?.Warning(ex,
                            "[VpnEngine] F-E restart delegate threw inside StartAsync");
                        return false;
                    }
                },
                logger: _engine._logger);
            return _engine._failover;
        }

        /// <summary>
        /// Wire AutoFailoverEngine for the post-start probe path -- restart
        /// delegate tears down the live sing-box BEFORE re-entering StartAsync
        /// so we don't leave an orphan adapter while the new instance comes
        /// up. Pre-3C this was inlined inside the post-start probe lambda.
        /// </summary>
        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck)
        {
            _engine._failover ??= new AutoFailoverEngine(
                CapturedSettings(),
                sanityCheck,
                restart: async (innerCt) =>
                {
                    try
                    {
                        _engine.Stop();
                        await _engine.StartAsync(CapturedSettings(), innerCt);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _engine._logger?.Warning(ex,
                            "[VpnEngine] F-E probe-driven restart threw");
                        return false;
                    }
                },
                logger: _engine._logger);
            return _engine._failover;
        }

        /// <summary>
        /// Settings backing field for the failover delegates. Pre-3C the
        /// delegates closed over the StartAsync `settings` parameter directly;
        /// post-3C the pipeline owns the settings reference and passes it
        /// through every callback. We stash it on the host on first wire-up
        /// so the closures can re-reference it on retry.
        /// </summary>
        private AppSettings _capturedSettings = null!;
        public void CaptureSettings(AppSettings settings) => _capturedSettings = settings;
        private AppSettings CapturedSettings() =>
            _capturedSettings ?? throw new InvalidOperationException(
                "StartupHost: CaptureSettings was not called before F-E wire-up.");

        /// <summary>
        /// Schedule the post-start Clash API probe. Fire-and-forget. Owns its
        /// own CancellationTokenSource on the engine so Stop() can cancel a
        /// queued probe (avoiding "ghost failover after manual disconnect").
        /// </summary>
        public void SchedulePostStartProbe(
            AppSettings settings,
            ConfigSanityCheck sanityCheck,
            CancellationToken ct)
        {
            CaptureSettings(settings);

            _engine._probeCts?.Cancel();
            _engine._probeCts?.Dispose();
            _engine._probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var probeCt = _engine._probeCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), probeCt);
                    var clashPort = ParseClashApiPort(settings.SingBox.ClashApi);
                    var probe = await sanityCheck.ProbeAsync(clashPort, probeCt);
                    if (probe.IsDead && !probeCt.IsCancellationRequested)
                    {
                        _engine._logger?.Warning(
                            "[VpnEngine] F-E post-start probe failed: {Reason}",
                            probe.Reason);

                        var failover = WireFailoverWithStop(sanityCheck);
                        var outcome = await failover.HandleDeadConfigAsync(
                            probe.Reason ?? "probe failed", probeCt);
                        if (outcome.UserFacingMessage != null)
                            _engine.AutoFailoverTriggered?.Invoke(outcome.UserFacingMessage);
                    }
                }
                catch (OperationCanceledException) { /* Stop() cancelled — fine */ }
                catch (Exception ex)
                {
                    _engine._logger?.Debug(ex,
                        "[VpnEngine] F-E probe task threw (non-fatal)");
                }
            }, probeCt);
        }
    }
}