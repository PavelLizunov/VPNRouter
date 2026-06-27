#nullable enable
using System.IO;
using System.Runtime.Versioning;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// ═══════════════════════════════════════════════════════════════════════════
// Phase 3C (2026-05-18) — StartupPipeline
// ═══════════════════════════════════════════════════════════════════════════
//
// Canonical VPN startup orchestrator. Single source of truth for the 8 phases
// that have to run in lock-step for a connect to be safe:
//
//   1. ResolveProfile        — Custom-mode dispatch / VLESS pre-gen invariant /
//                              Resolve subscription servers / Load profiles /
//                              Merge customisation / Resolve active profile /
//                              Custom-apps inject / Excluded-apps remove.
//   2. ResolveServers        — VlessServersResolver (already inside Step 1 for
//                              generated mode, exposed as a discrete phase
//                              boundary for test coverage of the empty-servers
//                              hard guard).
//   3. ScanProcesses         — IProcessScanner.ScanForProfile (with 30s timeout)
//                              + NetworkInterfaceDetector subnet auto-exclude.
//   4. GenerateConfig        — ConfigPipeline.Generate (Phase 2F) for generated
//                              mode, CustomConfigInjector.Inject for custom.
//   5. PreStartChecks        — ConfigSanityCheck.CheckBeforeStart + Auto-
//                              FailoverEngine wiring. Skipped for custom mode
//                              (user's JSON is their responsibility).
//   6. DeployAndSetupFirewall— Ensure sing-box binary deployed + Windows
//                              Firewall block-on-vpn-fail rules created in
//                              disabled state + pre-start TUN cleanup.
//   7. StartSingBox          — SingBoxManager.StartWithJson + warmup + post-
//                              start probe wiring.
//   8. StartMonitors         — ETW + HealthMonitor + WindowsDnsHardening
//                              (Windows only).
//
// ── Why this exists ──────────────────────────────────────────────────────
// Pre-3C VpnEngine.StartAsync was 880 LOC, touched 16 services inline, and
// had a sibling 200-LOC implementation in VpnEngine.ApplyAsync (the hot-
// reload path that re-runs phases 1-4 + a hot-reload-aware variant of 7).
// Phase 2F already extracted ConfigPipeline.Generate from the GenerateConfig
// step; this finishes the job by lifting the surrounding orchestration into
// its own file with named phases. Closes the v2.28.2 silent-leak bug class
// definitively — any new pre-start step propagates to every caller for free.
//
// ── What's NOT extracted ─────────────────────────────────────────────────
// • VpnEngine still owns lifecycle state (_singBox, _healthMonitor, _etw,
//   _firewall, _sanityCheck, _failover, _probeCts) + Dispose ordering +
//   public properties (IsRunning, ActiveServerAddress, etc.). Pipeline mutates
//   these via the supplied IStartupHost callbacks — keeps state ownership
//   single-rooted and avoids leaking SingBoxManager handles into a static
//   helper.
// • Stop semantics (graceful tear-down, WindowsDnsHardening.Restore, firewall
//   delete) stay in VpnEngine.Stop — the pipeline is start-only.
// • Custom-mode-only flows that don't fit the 8-phase ladder (custom config
//   path resolution, validation of user JSON) are private statics on VpnEngine
//   and called inline from phases 1 and 4 below.
//
// ── Contract ─────────────────────────────────────────────────────────────
// Idempotent at the orchestrator level: calling ExecuteAsync twice in a row
// with the same StartupContext produces the same StartupResult (modulo wall-
// clock time fields). The pipeline does NOT itself stop/restart sing-box on
// re-entry; callers signal intent via StartupMode.
//
// Failure semantics: each phase that throws bubbles a typed exception out
// (InvalidOperationException for invariant failures, FileNotFoundException
// for sing-box-binary-missing, ConflictingVpnException for AmneziaVPN/etc.
// pre-flight). The pipeline does NOT swallow phase failures — callers
// catch + surface as user-facing errors. F-E failover (phase 5) IS caught
// inside the pipeline because its outcome is "swap settings and re-enter"
// rather than "abort".

/// <summary>
/// Carries inputs into <see cref="StartupPipeline.ExecuteAsync"/>.
/// </summary>
/// <param name="Settings">Mutable app settings. Phase 2 mutates
/// <c>settings.Vless.Servers</c> in-place via <see cref="VlessServersResolver"/>;
/// callers MUST NOT persist this object after a successful start without
/// re-reading from disk (same constraint VpnEngine had pre-3C).</param>
/// <param name="Mode">Distinguishes between initial connect, hot-reload (apply
/// path), and auto-failover re-entry. Drives skip-decisions on phases like
/// PreStartChecks (which we deliberately skip on hot-reload so a transient
/// probe blip doesn't tear down a working session).</param>
/// <param name="SkipVpnConflictCheck">v2.32.1-r5 (Bug-r10-B) opt-out for the
/// pre-flight ConflictingVpnDetector probe. UI sets this when the user
/// explicitly clicks "Ignore" on the conflict banner.</param>
public sealed record StartupContext(
    AppSettings Settings,
    StartupMode Mode,
    bool SkipVpnConflictCheck = false);

/// <summary>
/// Outcome of <see cref="StartupPipeline.ExecuteAsync"/>.
/// </summary>
/// <param name="Success">True on a clean cold start; true on hot-reload
/// success; false when the pipeline returned early because of F-E
/// re-entry (the inner StartAsync has already finished — outer should
/// NOT continue).</param>
/// <param name="EarlyReturn">True when the pipeline short-circuited after
/// AutoFailover took over (phase 5 re-entered StartAsync via the supplied
/// restart delegate and the outer flow MUST NOT continue launching sing-
/// box). Distinct from <see cref="Success"/> = false (which is a genuine
/// failure surfaced via thrown exception).</param>
/// <param name="ProcessId">PID of the launched sing-box process, or null
/// if the pipeline aborted before phase 7.</param>
/// <param name="Duration">Wall-clock duration of the pipeline run, useful
/// for diagnostics + telemetry aggregation in Phase 4.</param>
/// <param name="ConfigJson">Generated sing-box JSON. Populated for
/// <see cref="StartupMode.HotReload"/> so the caller (VpnEngine.ApplyAsync)
/// can feed it into <c>SingBoxManager.ReloadConfigJson</c>. Null for
/// non-HotReload modes (the pipeline already started sing-box itself).</param>
/// <param name="Profile">Resolved active profile. Populated for HotReload
/// callers so they can compare against the pre-reload profile (used by
/// structural-change detection in ApplyAsync).</param>
public sealed record StartupResult(
    bool Success,
    bool EarlyReturn,
    int? ProcessId,
    TimeSpan Duration,
    string? ConfigJson = null,
    Profile? Profile = null);

/// <summary>
/// Mode tag passed via <see cref="StartupContext.Mode"/>. Each value drives a
/// different phase mask + side-effect set inside <see cref="StartupPipeline"/>.
/// </summary>
public enum StartupMode
{
    /// <summary>
    /// Initial connect (user clicked Start, autostart kicked in, service
    /// transitioned to Running). All 8 phases execute. PreStartChecks
    /// (F-E) is armed.
    /// </summary>
    ColdStart,

    /// <summary>
    /// Hot-reload (user changed settings on a running engine). Phases 1-4
    /// re-run to regenerate config + emit it via Clash API PUT. Phases
    /// 5-8 are SKIPPED — sing-box, firewall, ETW, and HealthMonitor are
    /// already up + carrying state. Falls back to full restart inside
    /// SingBoxManager.ReloadConfigJson if Clash API refuses the new config.
    /// </summary>
    HotReload,

    /// <summary>
    /// Re-entry after <see cref="AutoFailoverEngine"/> swapped the active
    /// server in response to a dead-config probe. Behaves like ColdStart
    /// but skips the F-E PreStartChecks branch (the outer caller already
    /// drove failover; running it again would recurse).
    /// </summary>
    AutoFailover,
}

/// <summary>
/// VpnEngine-side callback surface the pipeline uses to mutate engine state
/// + raise events. Single chokepoint so the pipeline never reaches into
/// VpnEngine fields directly.
///
/// <para>Implemented inline by VpnEngine; not a public abstraction. If we
/// ever need a unit test that drives StartupPipeline standalone, a test
/// fake can implement this interface and assert which callbacks fire.</para>
/// </summary>
internal interface IStartupHost
{
    /// <summary>Logger threaded through every phase.</summary>
    ILogger? Logger { get; }

    /// <summary>Process scanner (injected via VpnEngine ctor).</summary>
    IProcessScanner Scanner { get; }

    /// <summary>Firewall factory (injected via VpnEngine ctor).</summary>
    Func<IFirewallManager> FirewallFactory { get; }

    /// <summary>Process monitor (ETW) factory (injected via VpnEngine ctor).</summary>
    Func<IProcessMonitor> MonitorFactory { get; }

    /// <summary>Raise the engine's StatusChanged event.</summary>
    void OnStatus(string message);

    /// <summary>Raise the engine's Warning event.</summary>
    void OnWarning(string message);

    /// <summary>Forward the per-launch sing-box PID notification.</summary>
    void OnSingBoxStarted(int pid);

    /// <summary>
    /// Task #41 Stage 1 (2026-05-21) — forward the "TUN warmup probe confirmed
    /// reachability" notification. Implementations raise the engine's typed
    /// <c>Connected</c> event so App-side consumers can distinguish actual
    /// TUN-ready confirmation from the ambiguous <c>"Connected (PID N)"</c>
    /// <c>StatusChanged</c> string (which is also emitted on warmup failure
    /// for back-compat).
    ///
    /// <para>The pipeline calls this from EXACTLY ONE site:
    /// <see cref="StartupPipeline.ScheduleWarmupProbe"/>'s success branch
    /// (after <c>http.GetStringAsync(gstatic)</c> returns). The failure
    /// branch (15-attempt loop expiring) does NOT call this — Stage 2's
    /// App-side two-phase VM timer relies on that invariant.</para>
    /// </summary>
    void OnConnected(int pid);

    /// <summary>Forward HealthMonitor restart-attempt notifications.</summary>
    void OnRestartAttempted(int attempt, int max);

    /// <summary>
    /// G4 (2026-06-27): HealthMonitor hit the restart ceiling for the current
    /// server — run AutoFailover to swap to a healthy one instead of giving up.
    /// Host-owned because it needs the failover scaffolding + StartAsync closure.
    /// </summary>
    void OnFailoverRequested(string reason);

    /// <summary>Forward an F-E failover user-facing message.</summary>
    void OnAutoFailoverTriggered(string message);

    /// <summary>Forward an ETW-detected targeted process to listeners.</summary>
    void OnProcessDetected(string name, int pid);

    /// <summary>Store the active server's address for status display.</summary>
    void SetActiveServerAddress(string address);

    /// <summary>Store ActiveConfigMode + ActiveRoutingMode + TunFingerprint.</summary>
    void SetActiveModes(string configMode, string routingMode, string tunFingerprint);

    /// <summary>Store the resolved profile for later use (Apply, Stop).</summary>
    void SetActiveProfile(Profile profile);

    /// <summary>Store the latest ScanResult.</summary>
    void SetScanResult(ScanResult result);

    /// <summary>Store the lifecycle-owned SingBoxManager (Stop() disposes it).</summary>
    void SetSingBoxManager(SingBoxManager manager);

    /// <summary>
    /// dns-tunnel ONLY — bring up the slipstream transport sidecar before
    /// sing-box, so the VLESS outbound (127.0.0.1:port) has a live local front.
    /// Throws on failure (fail-closed: sing-box must never start over a dead
    /// local port). The pipeline calls this strictly gated on the active server
    /// protocol, so it never fires for any other server type.
    /// </summary>
    void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings);

    /// <summary>Store the lifecycle-owned firewall manager.</summary>
    void SetFirewallManager(IFirewallManager firewall);

    /// <summary>Store the lifecycle-owned ETW monitor.</summary>
    void SetProcessMonitor(IProcessMonitor etw);

    /// <summary>Store the lifecycle-owned HealthMonitor.</summary>
    void SetHealthMonitor(HealthMonitor monitor);

    /// <summary>
    /// Reset the ConfigSanityCheck / AutoFailoverEngine pair on cold start
    /// so cycle state (tried-server list) survives back-to-back failovers
    /// but resets after the user successfully connects to something.
    /// Implementation: clear cached instances; phase 5 lazily re-creates.
    /// Also captures the active <paramref name="settings"/> reference so the
    /// failover restart delegate (constructed lazily by WireFailover) can
    /// re-call StartAsync with the same settings the user kicked off with.
    /// </summary>
    void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck);

    /// <summary>
    /// Wire the F-E AutoFailoverEngine with a restart delegate that re-
    /// enters StartAsync. Used by phase 5 (pre-start) AND by the post-
    /// start probe scheduled in phase 7. The host owns the delegate
    /// because it has to capture VpnEngine.StartAsync, which is non-
    /// static (closures over `this`).
    /// </summary>
    AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck);

    /// <summary>
    /// Wire the F-E AutoFailoverEngine with a Stop()+StartAsync delegate
    /// — separate from <see cref="WireFailover"/> because the post-start
    /// probe needs to tear down the live sing-box before re-launching,
    /// whereas the pre-start phase 5 hasn't started one yet.
    /// </summary>
    AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck);

    /// <summary>
    /// Schedule the post-start Clash API probe (fire-and-forget). The
    /// host owns the CancellationTokenSource so Stop() can cancel a
    /// queued probe (avoiding "ghost failover after disconnect").
    /// </summary>
    void SchedulePostStartProbe(
        AppSettings settings,
        ConfigSanityCheck sanityCheck,
        CancellationToken ct);
}

/// <summary>
/// Phase 3C orchestrator. Walks the 8 startup phases listed in the
/// file-header comment, mutating <see cref="IStartupHost"/> state along the
/// way and returning a <see cref="StartupResult"/>.
/// </summary>
internal sealed class StartupPipeline
{
    private readonly IStartupHost _host;
    private readonly ISettingsStore _store;
    private readonly IWindowsDnsHardening _dnsHardening;

    /// <summary>
    /// Task #49 (2026-05-21): static seam for the TUN warmup probe's
    /// HTTP client. Default behaviour calls <c>new HttpClient</c> inline
    /// (preserving pre-Task-#49 production semantics — minimal allocations,
    /// no shared connection pool to leak); test code sets this field to
    /// a <c>FakeHttpClient</c> to drive the BR-7 success branch
    /// deterministically without hitting <c>gstatic.com</c> on the real
    /// internet.
    ///
    /// <para>Mirrors the existing static-seam pattern used by
    /// <see cref="SingBoxManager.Runner"/> and
    /// <see cref="TunAdapterDiagnostics.Runner"/>: production behaviour
    /// uses an inline default; tests overwrite + restore via try/finally.
    /// Each test must save the previous value before swapping and restore
    /// it in cleanup so a crash mid-test doesn't leak the swap into the
    /// next test.</para>
    ///
    /// <para><b>Thread-safety</b>: the field is set from test setup
    /// (single-threaded per xUnit's <c>parallelizeTestCollections: false</c>)
    /// and read from the warmup probe's <see cref="Task.Run"/> body.
    /// Volatile semantics aren't strictly needed since the swap happens
    /// before <see cref="ExecuteAsync"/> is invoked and the test holds a
    /// strong reference to the fake, but we still snapshot the field into
    /// a local at the top of <see cref="ScheduleWarmupProbe"/> for clarity.</para>
    ///
    /// <para><b>Why not a ctor parameter</b>: the existing
    /// <see cref="StartupPipeline"/> ctor already carries 3 seams
    /// (host, store, dnsHardening) and adding a fourth would require
    /// re-plumbing both <see cref="VpnEngine.StartAsync"/> AND
    /// <see cref="VpnEngine.ApplyAsync"/> ctor calls + adding a
    /// <see cref="VpnEngine"/> ctor parameter — 3+ file change. The
    /// static seam matches what Group 1 (Task #36-C) already established
    /// for the sing-box / tundiag side, keeping the test-injection
    /// vocabulary uniform.</para>
    /// </summary>
    public static IHttpClient? WarmupHttp;

    /// <param name="host">VpnEngine-side callback surface used to mutate
    /// engine state + raise events through the 8 pipeline phases.</param>
    /// <param name="store">3G-1 (v3.0 refactor): persistence seam used by
    /// the ActiveProfile sanitisation step. Defaults to
    /// <see cref="RealSettingsStore.Instance"/> for back-compat — pre-3G
    /// the code called <c>SettingsLoader.Load/Save</c> statically. Tests
    /// inject <c>InMemorySettingsStore</c> to keep the pipeline isolated
    /// from the on-disk config.</param>
    /// <param name="dnsHardening">Task #36-A (v3.0 refactor Phase 4):
    /// Windows DNS-leak-mitigation seam. Defaults to
    /// <see cref="WindowsDnsHardeningImpl.Default"/> which wraps the
    /// existing static facade (and is a no-op on non-Windows builds).
    /// Tests inject <c>NullWindowsDnsHardening</c> so the lifecycle
    /// happy-path test (Task #36-C) doesn't mutate HKLM. The seam covers
    /// phase 7's BR-7 deferred-lockdown branch AND phase 8's apply step,
    /// so a single fake captures both touch points.</param>
    public StartupPipeline(
        IStartupHost host,
        ISettingsStore? store = null,
        IWindowsDnsHardening? dnsHardening = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _store = store ?? RealSettingsStore.Instance;
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;
    }

    /// <summary>
    /// Walk all 8 phases in order. On <see cref="StartupMode.HotReload"/>,
    /// the orchestrator returns the regenerated JSON string instead of
    /// launching sing-box — the hot-reload caller (VpnEngine.ApplyAsync)
    /// feeds that into SingBoxManager.ReloadConfigJson itself, so it can
    /// drive structural-change detection + Clash API failover logic.
    /// </summary>
    public async Task<StartupResult> ExecuteAsync(
        StartupContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var settings = context.Settings;

        // Phase 0 — preflight (only ColdStart / AutoFailover; HotReload skips
        // this because sing-box is already running and we don't want a
        // conflicting-VPN probe to tear down a working session).
        if (context.Mode != StartupMode.HotReload)
        {
            PreflightConflictAndDns(settings, context.SkipVpnConflictCheck);
            await PreflightGeoDataAsync(settings, ct);
        }

        // Phase 1+2 — ResolveProfile + ResolveServers
        var (profile, isCustomConfig, customConfigJson) =
            await ResolveProfileAndServersAsync(settings, context.Mode, ct);

        // Phase 3 — ScanProcesses
        var scanResult = await ScanProcessesPhaseAsync(profile, settings, ct).ConfigureAwait(false);

        // Phase 4 — GenerateConfig
        var configJson = GenerateConfigPhase(
            profile,
            scanResult,
            settings,
            isCustomConfig,
            customConfigJson,
            context.Mode);

        ct.ThrowIfCancellationRequested();

        // HotReload returns immediately — the caller drives ReloadConfigJson
        // with the freshly-generated JSON. Phases 5-8 are SKIPPED because
        // sing-box / ETW / HealthMonitor are already running. ApplyAsync
        // owns the structural-change detection (RoutingMode mismatch,
        // TunFingerprint mismatch, process-list change) that the pipeline
        // can't make in the absence of ApplyAsync's "pre-reload baseline"
        // context.
        if (context.Mode == StartupMode.HotReload)
        {
            return new StartupResult(
                Success: true,
                EarlyReturn: false,
                ProcessId: null,
                Duration: sw.Elapsed,
                ConfigJson: configJson,
                Profile: profile);
        }

        // Phase 5 — PreStartChecks (F-E). AutoFailover mode skips because the
        // outer caller already triggered failover; running it again would
        // recurse.
        if (context.Mode == StartupMode.ColdStart && !isCustomConfig)
        {
            var earlyReturn = await PreStartChecksPhaseAsync(
                settings, configJson, ct);
            if (earlyReturn)
            {
                // F-E re-entered StartAsync via the host's restart delegate.
                // Outer caller must NOT proceed with the dead config.
                return new StartupResult(
                    Success: true,
                    EarlyReturn: true,
                    ProcessId: null,
                    Duration: sw.Elapsed);
            }
        }

        // Phase 6 — Deploy sing-box binary + firewall setup + pre-start TUN
        // cleanup.
        await DeployAndSetupFirewallPhaseAsync(
            settings, profile, scanResult, ct);

        ct.ThrowIfCancellationRequested();

        // Phase 6.5 (dns-tunnel ONLY) — bring up the slipstream transport before
        // sing-box. STRICTLY gated on the active server protocol → zero effect on
        // every other server type (the common path skips this entirely). The
        // VLESS outbound generated in Phase 4 targets 127.0.0.1:<port>, so the
        // local front must be live first. Fail-closed: a throw here aborts the
        // start, so sing-box never launches over a dead local port.
        var activeForTransport = settings.Vless?.GetActiveServers() ?? new List<VlessServerEntry>();
        if (activeForTransport.Count > 0 &&
            string.Equals(activeForTransport[0].Protocol, "dns-tunnel", StringComparison.OrdinalIgnoreCase))
        {
            _host.StartDnsTunnelTransport(activeForTransport[0], settings);
            ct.ThrowIfCancellationRequested();
        }

        // Phase 7 — StartSingBox + warmup + post-start probe.
        var pid = await StartSingBoxPhaseAsync(
            settings, configJson, isCustomConfig, ct);

        ct.ThrowIfCancellationRequested();

        // Phase 8 — StartMonitors.
        StartMonitorsPhase(settings, profile, scanResult);

        _host.OnStatus("VPN Router is running");

        return new StartupResult(
            Success: true,
            EarlyReturn: false,
            ProcessId: pid,
            Duration: sw.Elapsed);
    }

    // ─── Phase 0a: Preflight conflicting-VPN + DNS flush ───────────────────

    /// <summary>
    /// Pre-flight detect competing VPN clients holding wintun. Throws
    /// <see cref="ConflictingVpnException"/> if a peer is found and
    /// <paramref name="skipVpnConflictCheck"/> is false. Also flushes DNS
    /// cache so pre-VPN-resolved entries don't survive into the tunnel.
    /// </summary>
    private void PreflightConflictAndDns(AppSettings settings, bool skipVpnConflictCheck)
    {
        AppPaths.EnsureDirectories();

        if (!skipVpnConflictCheck)
        {
            var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses(_host.Logger);
            if (conflicts.Count > 0)
            {
                var first = conflicts[0];
                throw new ConflictingVpnException(
                    conflicts,
                    $"Another VPN client is running: {first.ProcessName} (PID {first.Pid}). " +
                    $"Only one VPN can hold the TUN adapter at a time. " +
                    $"Stop {first.ProcessName} before launching VPNRouter.");
            }

            // Soft notice (2026-06-26): coexisting VPN clients (WireGuard /
            // AmneziaVPN) run their own separate tunnel adapter and coexist with
            // VPNRouter-TUN via route_exclude_address — surface a warning but do
            // NOT block. The old hard-block threw on the user's AmneziaVPN even
            // though the connect then succeeds on retry (diag 20260626-212741).
            var coexisting = ConflictingVpnDetector.DetectCoexistingVpnProcesses(_host.Logger);
            if (coexisting.Count > 0)
            {
                var c = coexisting[0];
                _host.Logger?.Warning(
                    "[StartupPipeline] Coexisting VPN detected: {Name} (PID {Pid}) — VPNRouter " +
                    "excludes its subnet from TUN routing so they run side-by-side; proceeding. " +
                    "If routed apps lose internet, stop it and reconnect.",
                    c.ProcessName, c.Pid);
            }
        }
        else
        {
            _host.Logger?.Information(
                "[StartupPipeline] Skipping conflicting-VPN pre-flight check (user opt-in)");
        }

        if (settings.App.FlushDnsOnStart)
            DnsFlusher.Flush(_host.Logger);
    }

    /// <summary>
    /// If RU bypass is enabled and geo data isn't on disk yet, download it
    /// before phases 4 / 7. Failure is non-fatal — RU bypass just gets
    /// disabled for this session.
    /// </summary>
    private async Task PreflightGeoDataAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings.App.BypassRussianTraffic && !GeoDataDownloader.AreGeoFilesAvailable())
        {
            _host.OnStatus("Downloading geo data...");
            try
            {
                var downloader = new GeoDataDownloader(_host.Logger);
                var ok = await downloader.EnsureGeoFilesAsync(ct);
                if (!ok)
                    _host.Logger?.Warning(
                        "[StartupPipeline] Geo data download failed — RU bypass will be disabled for this session");
            }
            catch (Exception ex)
            {
                _host.Logger?.Warning(ex,
                    "[StartupPipeline] Geo data download error — RU bypass will be disabled");
            }
        }
    }

    // ─── Phase 1+2: ResolveProfile + ResolveServers ────────────────────────

    /// <summary>
    /// Combined Phase 1 (resolve / validate the profile) + Phase 2 (resolve
    /// servers via <see cref="VlessServersResolver"/>). The two are stitched
    /// together because the active-server address must be set before the
    /// config-generation phase, and the profile resolve also drives the
    /// custom-vs-generated dispatch that decides which Phase 4 branch runs.
    /// </summary>
    /// <returns>
    /// Tuple of (resolved profile, isCustomConfig flag, raw custom JSON or null).
    /// The raw custom JSON is read here once and passed forward to Phase 4
    /// so a racing edit between phases doesn't slip through the validation
    /// gate.
    /// </returns>
    private async Task<(Profile profile, bool isCustom, string? rawCustomJson)>
        ResolveProfileAndServersAsync(
            AppSettings settings,
            StartupMode mode,
            CancellationToken ct)
    {
        var isCustomConfig = (settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);
        var activeConfigMode = isCustomConfig ? "custom" : "generated";
        var activeRoutingMode = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase) ? "full" : "split";
        var tunFingerprint = VpnEngine.ComputeTunFingerprint(settings.Tun);

        _host.SetActiveModes(activeConfigMode, activeRoutingMode, tunFingerprint);

        string? rawCustomJson = null;

        if (isCustomConfig)
        {
            var customPath = VpnEngine.ResolveCustomConfigPath(settings);
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                throw new InvalidOperationException(
                    $"Custom config not found: {customPath}. Add a config in the Servers tab.");

            rawCustomJson = File.ReadAllText(customPath);
            var (isValid, errors) = CustomConfigInjector.Validate(rawCustomJson);
            if (!isValid)
                throw new InvalidOperationException(
                    $"Custom config validation failed: {string.Join("; ", errors)}");

            try
            {
                var (_, srv) = CustomConfigInjector.ParseConfigInfo(rawCustomJson);
                _host.SetActiveServerAddress(srv);
            }
            catch { _host.SetActiveServerAddress(""); }
        }
        else
        {
            // F-12 (parity audit P0) — defense-in-depth backstop for silent
            // ConfigMode flips. If the AppSettings model is inconsistent,
            // throw here BEFORE the resolver mutates anything in-place.
            var pregenValidation = LeakProtection.ValidateAppSettings(settings);
            if (!pregenValidation.IsValid)
            {
                var msg = string.Join(" ", pregenValidation.Errors);
                _host.Logger?.Error(
                    "[StartupPipeline] AppSettings invariant violation pre-generation: {Errors}",
                    msg);
                throw new InvalidOperationException(msg);
            }

            // v2.28.2: single source of truth for subscription→VLESS aggregation.
            // Resolver mutates settings.Vless.Servers in place; same code path
            // as ConfigPipeline + HealthMonitor.GenerateConfigJson.
            var allServers = VlessServersResolver.Resolve(settings, _host.Logger);
            if (allServers.Count == 0)
            {
                var why = VlessServersResolver.DescribeEmptyReason(settings)
                          ?? "VLESS server not configured.";
                throw new InvalidOperationException(why);
            }

            // Show the ACTIVE server (what will actually run), not Vless[0].
            var activeServers = settings.Vless.GetActiveServers();
            _host.SetActiveServerAddress(
                activeServers.Count > 0
                    ? activeServers[0].Server
                    : allServers[0].Server);
        }

        ct.ThrowIfCancellationRequested();

        _host.OnStatus("Loading profiles...");
        VpnEngine.QuarantineStaleUserCatalogue(_host.Logger);

        var sources = SafeMode.Enabled
            ? VpnEngine.BuildBundledOnlyProfileSources()
            : VpnEngine.BuildProfileSources(settings);
        if (SafeMode.Enabled)
            _host.Logger?.Warning(
                "[StartupPipeline] Safe mode — using bundled profiles only, ignoring user overrides");

        var manager = new ProfileManager(sources, _host.Logger);
        var collection = await manager.LoadAsync(ct);

        if (SafeMode.Enabled)
            _host.Logger?.Warning(
                "[StartupPipeline] Safe mode — skipping custom apps / categories / group-apps merge");
        if (!SafeMode.Enabled)
            VpnEngine.MergeUserCustomization(collection, settings);

        _host.Logger?.Information(
            "[StartupPipeline] Loaded profile catalogue ({Count}): {Names}",
            collection.Profiles.Count,
            string.Join(", ", collection.Profiles.Select(p => p.Name)));

        ct.ThrowIfCancellationRequested();

        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        var profileName = settings.ActiveProfile;

        if (string.IsNullOrEmpty(profileName) && !isFullTunnel && !isCustomConfig)
            throw new InvalidOperationException("No active profile specified in config.");

        if (SafeMode.Enabled)
        {
            _host.Logger?.Warning("[StartupPipeline] Safe mode — forcing full-tunnel routing");
            isFullTunnel = true;
        }

        Profile activeProfile;
        if (isFullTunnel)
        {
            _host.Logger?.Information(
                "[StartupPipeline] Full-tunnel mode — ignoring ActiveProfile '{Profile}' and skipping process scan",
                profileName ?? "(empty)");
            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }
        else if (!string.IsNullOrEmpty(profileName))
        {
            var names = profileName.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var merged = manager.MergeProfilesTolerant(names, out var missing);
            if (merged == null)
                throw new InvalidOperationException(
                    $"None of the requested profiles exist: {string.Join(", ", names)}. " +
                    $"Available: {string.Join(", ", collection.Profiles.Select(p => p.Name))}");

            activeProfile = merged;

            if (missing.Count > 0)
            {
                _host.OnWarning($"Skipped unknown profile(s): {string.Join(", ", missing)}");
                // Self-heal: rewrite settings.ActiveProfile to drop missing
                // names so this won't fire on next launch. ColdStart only —
                // ApplyAsync historically does NOT persist this back to disk
                // (preserving pre-3C asymmetry; the hot-reload caller leaves
                // settings.ActiveProfile alone so a transient catalogue blip
                // doesn't permanently sanitize the user's selection).
                if (mode == StartupMode.ColdStart || mode == StartupMode.AutoFailover)
                    PersistSanitizedActiveProfile(settings, names, missing, profileName);
            }
        }
        else if (isCustomConfig)
        {
            activeProfile = new Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
        }
        else
        {
            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }

        // Inject custom apps from GUI (top-level CustomApps list — separate
        // from CustomGroupApps which already merged into bundled profiles
        // via MergeUserCustomization above).
        if (settings.CustomApps?.Count > 0)
        {
            foreach (var app in settings.CustomApps)
            {
                if (!string.IsNullOrEmpty(app) &&
                    !activeProfile.Processes.Any(p =>
                        p.Name.Equals(app, StringComparison.OrdinalIgnoreCase)))
                {
                    activeProfile.Processes.Add(new ProcessRule
                    {
                        Name = app,
                        IncludeChildren = true,
                        ScanPatterns = new[] { app }
                    });
                }
            }
        }

        // Apply per-app exclusions LAST so they override everything above.
        // SafeMode gate kept here — pre-3C had it inside StartAsync only.
        if (!SafeMode.Enabled)
            VpnEngine.RemoveExcludedApps(activeProfile, settings.ExcludedApps);

        _host.OnStatus($"Profile: {activeProfile.Name} ({activeProfile.Processes.Count} rules)");
        _host.SetActiveProfile(activeProfile);

        ct.ThrowIfCancellationRequested();

        return (activeProfile, isCustomConfig, rawCustomJson);
    }

    /// <summary>
    /// When the tolerant profile resolver dropped names, rewrite the
    /// settings.ActiveProfile so this self-heals on next launch. We re-load
    /// settings from disk before saving so the in-place VlessServersResolver
    /// mutation from Phase 2 doesn't leak into yaml — see v2.30.0-r8 comment.
    /// </summary>
    private void PersistSanitizedActiveProfile(
        AppSettings settings,
        string[] names,
        IReadOnlyCollection<string> missing,
        string originalProfileName)
    {
        var sanitized = string.Join(",",
            names.Where(n => !missing.Contains(n, StringComparer.OrdinalIgnoreCase)));
        if (string.Equals(sanitized, originalProfileName, StringComparison.Ordinal))
            return;

        settings.ActiveProfile = sanitized;
        try
        {
            // CRITICAL — v2.30.0-r8 invariant: do NOT persist `settings`
            // directly here. VlessServersResolver has already mutated
            // settings.Vless.Servers in-place with the aggregated
            // subscription list (subscribe mode). Saving this object writes
            // that aggregate into vless.servers in YAML and on next launch
            // it resurfaces as fake "manual VLESS servers" in the VLESS tab.
            var fresh = _store.Load(AppPaths.ConfigYamlPath);
            fresh.ActiveProfile = sanitized;
            _store.Save(fresh);
            _host.Logger?.Information(
                "[StartupPipeline] ActiveProfile migrated: '{Old}' → '{New}'",
                originalProfileName, sanitized);
        }
        catch (Exception saveEx)
        {
            _host.Logger?.Warning(saveEx,
                "[StartupPipeline] Failed to persist ActiveProfile migration");
        }
    }

    // ─── Phase 3: ScanProcesses ────────────────────────────────────────────

    /// <summary>
    /// Run IProcessScanner.ScanForProfile with a 30s timeout (v2.22.4 self-
    /// heal: WMI child-lookup on a corrupt catalogue used to hang forever).
    /// Also auto-detect WireGuard/AmneziaWG subnets and merge into
    /// settings.Tun.RouteExcludeAddress.
    ///
    /// <para>3G-3 (v3.0 refactor): converted from <c>Task.Run(...).Wait(timeout)</c>
    /// + <c>.Result</c> to a fully-async <c>Task.WhenAny</c> + <c>await</c>
    /// pattern. The pre-3G blocking-on-Wait pinned a thread-pool worker
    /// for up to 30s under load (one of the audit-D smells) and risked
    /// deadlock when the caller's <see cref="SynchronizationContext"/>
    /// was captured (which doesn't happen on Service today, but would
    /// the moment any Avalonia UI path called into this directly).</para>
    /// </summary>
    private async Task<ScanResult> ScanProcessesPhaseAsync(
        Profile profile,
        AppSettings settings,
        CancellationToken ct)
    {
        _host.OnStatus("Scanning processes...");
        ScanResult? scanResult = null;
        try
        {
            var scanTask = Task.Run(() => _host.Scanner.ScanForProfile(profile), ct);
            // 30s budget — same as pre-3G. Task.WhenAny + a delay task is
            // the idiomatic async equivalent of Task.Wait(timeout). The
            // ct here also cancels the delay if the caller bails first.
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var winner = await Task.WhenAny(scanTask, timeoutTask).ConfigureAwait(false);
            if (winner == scanTask)
            {
                scanResult = await scanTask.ConfigureAwait(false);
            }
            else
            {
                _host.Logger?.Warning(
                    "[StartupPipeline] Process scan timed out after 30s — continuing with empty list. " +
                    "Check %ProgramData%\\VPNRouter\\profiles\\ for corrupt entries, or switch to Full tunnel mode.");
                _host.OnWarning(
                    "Process scan timed out — split mode may not route correctly. " +
                    "Switch to Full mode or reset your catalogue.");
                // Best-effort: observe the still-running scan so its
                // exception (if any) doesn't surface as an unobserved
                // task exception on finalisation. We don't propagate the
                // result because we've already committed to the empty-
                // list fallback below.
                _ = scanTask.ContinueWith(
                    t => { _ = t.Exception; },
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _host.Logger?.Error(ex,
                "[StartupPipeline] Process scan failed — continuing with empty list");
            _host.OnWarning($"Process scan error: {ex.Message}");
        }
        scanResult ??= new ScanResult
        {
            ProcessNames = new List<string>(),
            ScannedAt = DateTime.Now
        };
        _host.Logger?.Information(
            "[StartupPipeline] Resolved {Count} process names",
            scanResult.ProcessNames.Count);

        _host.SetScanResult(scanResult);

        ct.ThrowIfCancellationRequested();

        // 4.5 — auto-detect WG/AWG subnets that should bypass the TUN.
        //
        // RUNTIME-ONLY, recomputed fresh on EVERY connect: the detected subnets
        // are stored in the non-persisted settings.Tun.AutoDetectedExcludeAddress
        // and folded into the effective exclude list at config-generation time
        // (TunSettings.GetEffectiveRouteExcludeAddress). They are deliberately
        // NEVER merged into the persisted RouteExcludeAddress.
        //
        // The assignment is unconditional (even when empty) — that RESET is the
        // whole point of the fix: a WG/AWG adapter that has since disappeared, or
        // a move to a different network, stops contributing an exclude on the
        // next connect instead of leaving a stale subnet (e.g. 10.9.1.0/24,
        // widened from a /32 point-to-point) routed DIRECT past the VPN forever.
        var detectedSubnets = NetworkInterfaceDetector.DetectWireGuardSubnets(
            settings.Tun.InterfaceName, _host.Logger);
        settings.Tun.AutoDetectedExcludeAddress = detectedSubnets;
        if (detectedSubnets.Count > 0)
        {
            _host.Logger?.Information(
                "[StartupPipeline] Auto-excluded WG/AWG subnets (runtime-only, not persisted): {Subnets}",
                string.Join(", ", detectedSubnets));
        }

        return scanResult;
    }

    // ─── Phase 4: GenerateConfig ───────────────────────────────────────────

    /// <summary>
    /// Generate the sing-box JSON. Custom-mode runs through
    /// <see cref="CustomConfigInjector"/>; generated mode routes through
    /// <see cref="ConfigPipeline.Generate"/> (the Phase 2F canonical helper).
    /// </summary>
    private string GenerateConfigPhase(
        Profile profile,
        ScanResult scanResult,
        AppSettings settings,
        bool isCustomConfig,
        string? rawCustomJson,
        StartupMode mode)
    {
        if (isCustomConfig)
        {
            var customPath = VpnEngine.ResolveCustomConfigPath(settings);
            var activeEntry = settings.App.CustomConfigs
                .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig);
            var configName = activeEntry?.Name ?? "custom";
            var localCopy = CustomConfigInjector.GetProgramDataPath(configName);

            if (!File.Exists(localCopy) && File.Exists(customPath))
            {
                localCopy = CustomConfigInjector.CopyToProgramData(customPath, configName);
                _host.Logger?.Information(
                    "[StartupPipeline] Custom config copied to {Path}", localCopy);
            }

            // Prefer the JSON we read in Phase 1 (already validated) if the
            // path didn't change. Otherwise re-read so the local copy's
            // ProgramData content wins. Either way, validation already ran.
            var injectSource = (rawCustomJson != null && File.Exists(customPath))
                ? rawCustomJson
                : File.ReadAllText(localCopy);
            var configJson = CustomConfigInjector.Inject(
                injectSource, scanResult.ProcessNames, settings);
            _host.OnStatus($"Custom config '{configName}' injected with process routing");
            return configJson;
        }

        // Generated mode — ConfigPipeline.Generate (Phase 2F) handles
        // Resolve→Generate→Validate→Serialize as a single sequence.
        // HotReload also uses Strict mode (same as ColdStart) — Apply
        // previously did this inline; closure of Phase 2F-A.
        var json = ConfigPipeline.Generate(
            profile,
            scanResult.ProcessNames,
            settings,
            ConfigPipeline.ValidationMode.Strict,
            warningSink: msg => _host.OnWarning(msg),
            logger: _host.Logger);
        return json;
    }

    // ─── Phase 5: PreStartChecks (F-E sanity check + AutoFailover) ─────────

    /// <summary>
    /// Static dead-config detection. Pattern-matches the proxy outbound
    /// against known-placeholder fingerprints; if it matches, trigger
    /// AutoFailoverEngine which swaps active server + re-enters StartAsync
    /// via the host's restart delegate.
    /// </summary>
    /// <returns>True when AutoFailover took over (caller MUST stop).
    /// False on the happy path (caller proceeds with phase 6).</returns>
    private async Task<bool> PreStartChecksPhaseAsync(
        AppSettings settings,
        string configJson,
        CancellationToken ct)
    {
        _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);

        var preCheck = sanityCheck.CheckBeforeStart(configJson);
        if (!preCheck.IsDead) return false;

        _host.Logger?.Warning(
            "[StartupPipeline] F-E pre-start dead config: {Reason} (field: {Field})",
            preCheck.Reason, preCheck.OffendingField);

        var failover = _host.WireFailover(sanityCheck);
        var outcome = await failover.HandleDeadConfigAsync(
            preCheck.Reason ?? "dead config", ct);

        if (outcome.UserFacingMessage != null)
            _host.OnAutoFailoverTriggered(outcome.UserFacingMessage);

        if (outcome.Switched)
        {
            _host.Logger?.Information(
                "[StartupPipeline] F-E switched to {New} — abort outer StartAsync flow",
                outcome.NewActiveServer);
            return true;
        }

        // Failover refused — surface the message and throw so the caller's
        // try/catch sees the same exception type as the empty-servers path.
        _host.OnWarning(outcome.UserFacingMessage ?? preCheck.Reason ?? "Dead config");
        throw new InvalidOperationException(
            outcome.UserFacingMessage ?? preCheck.Reason ?? "Dead VPN config");
    }

    // ─── Phase 6: Deploy sing-box + Firewall + TUN cleanup ─────────────────

    /// <summary>
    /// Ensure the sing-box binary is deployed (bundle → ProgramData), create
    /// firewall block rules in disabled state, and pre-start sweep for stale
    /// wintun adapters left by a previous crash.
    /// </summary>
    private async Task DeployAndSetupFirewallPhaseAsync(
        AppSettings settings,
        Profile profile,
        ScanResult scanResult,
        CancellationToken ct)
    {
        DeploySingBoxBinary(settings);

        ct.ThrowIfCancellationRequested();

        // Firewall — created in disabled state. HealthMonitor enables on
        // crash, disables on successful restart.
        var firewall = _host.FirewallFactory();
        _host.SetFirewallManager(firewall);
        if (profile.BlockOnVpnFail)
        {
            firewall.CreateBlockRules(scanResult.ProcessNames);
            _host.OnStatus("Firewall block rules created (disabled)");
        }

        ct.ThrowIfCancellationRequested();

        // Pre-start wintun sweep (Windows only).
        await PreStartTunCleanupAsync(ct);
    }

    /// <summary>
    /// Deploy sing-box binary if the bundled copy differs in size from the
    /// installed copy (heuristic for "upgrade happened, redeploy").
    /// </summary>
    private void DeploySingBoxBinary(AppSettings settings)
    {
        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(settings.SingBox.ExecutablePath)
            : AppPaths.SingBoxExePath;
        var bundledPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");

        if (File.Exists(bundledPath))
        {
            bool needDeploy = !File.Exists(exePath);
            if (!needDeploy)
            {
                var installedSize = new FileInfo(exePath).Length;
                var bundledSize = new FileInfo(bundledPath).Length;
                needDeploy = installedSize != bundledSize;
            }
            if (needDeploy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                File.Copy(bundledPath, exePath, overwrite: true);
                _host.Logger?.Information(
                    "[StartupPipeline] Deployed sing-box from bundle to {Path}", exePath);
            }
        }
        else if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }
    }

    /// <summary>
    /// v2.32.x Bug-r9-H pre-start sweep for stale wintun adapters. No-op on
    /// macOS/Linux. Settles 500ms after a removal so Windows network stack
    /// finishes tearing down before sing-box re-creates.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task PreStartTunCleanupWindowsAsync(CancellationToken ct)
    {
        int removedAdapterCount = 0;
        try
        {
            removedAdapterCount = await TunAdapterDiagnostics
                .PreStartCleanupAsync(_host.Logger, "StartupPipeline");
        }
        catch (Exception ex)
        {
            _host.Logger?.Warning(ex,
                "[StartupPipeline] Pre-start TUN cleanup threw (non-fatal)");
        }
        if (removedAdapterCount > 0)
            await Task.Delay(500, ct);
    }

    private async Task PreStartTunCleanupAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            await PreStartTunCleanupWindowsAsync(ct);
    }

    // ─── Phase 7: StartSingBox + warmup + post-start probe ─────────────────

    /// <summary>
    /// Launch sing-box, wait up to 5s for IsRunning, fire warmup probe
    /// (fire-and-forget), wire post-start Clash API probe (also fire-and-
    /// forget). Throws if sing-box doesn't come up within 5s.
    /// </summary>
    private async Task<int> StartSingBoxPhaseAsync(
        AppSettings settings,
        string configJson,
        bool isCustomConfig,
        CancellationToken ct)
    {
        _host.OnStatus("Starting sing-box...");

        var singBox = new SingBoxManager(settings.SingBox, _host.Logger);
        singBox.Started += pid => _host.OnSingBoxStarted(pid);
        _host.SetSingBoxManager(singBox);
        singBox.StartWithJson(configJson);

        // Wait up to 5s for startup.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (singBox.IsRunning()) break;
        }

        if (!singBox.IsRunning())
        {
            // SingBoxManager already logged; we surface a fatal-ish message
            // and let the caller's catch tear down firewall + state.
            throw new Exception("sing-box failed to start within 5 seconds. Check logs.");
        }

        var pid = singBox.Pid ?? -1;
        _host.Logger?.Information("[StartupPipeline] sing-box started (PID {Pid})", pid);
        _host.OnStatus($"sing-box started (PID {pid})");

        // Fire-and-forget warmup probe — give TUN routing tables time to settle.
        // BR-7 (brat 2026-05-20) — passes settings so the success branch can
        // arm the deferred Wave 39 firewall DNS lockdown (was installed
        // immediately by WindowsDnsHardening.Apply pre-r11 and broke warm-up
        // itself on slow-TUN machines).
        ScheduleWarmupProbe(pid, settings, ct);

        // Phase 8.5 — F-E post-start probe via Clash API. The host wires
        // this with a Stop()+Restart delegate so a failure tears the live
        // sing-box down before re-launching.
        if (!isCustomConfig)
        {
            _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);
            _host.SchedulePostStartProbe(settings, sanityCheck, ct);
        }

        return pid;
    }

    /// <summary>
    /// Schedule the TUN warmup probe (15 attempts × 1s) on a background task.
    /// Captures the PID snapshot to avoid NRE if Stop() races between this
    /// scheduling call and the lambda body running.
    ///
    /// <para>BR-7 (brat 2026-05-20) — also responsible for arming the
    /// Wave 39 firewall DNS lockdown AFTER warm-up confirms TUN routing.
    /// On slow-TUN machines the lockdown installed via
    /// <see cref="WindowsDnsHardening.Apply"/> previously fired immediately
    /// after sing-box started, which broke DNS resolution for the warm-up
    /// probe itself: the probe needs to resolve gstatic.com via Cloudflare
    /// DoH through TUN, but with UDP/53 already banned on Ethernet and TUN
    /// not yet routing, the system fell into a 33-second resolution
    /// timeout. The user perceived this as "no internet after install".
    /// Deferring the lockdown to the success branch closes that window.
    /// If warm-up FAILS the lockdown is intentionally NOT installed — the
    /// user gets internet (with a DNS leak risk that's preferable to
    /// 33 s of no internet at all).</para>
    /// </summary>
    private void ScheduleWarmupProbe(int pidSnapshot, AppSettings settings, CancellationToken ct)
    {
        _host.OnStatus("Warming up network...");
        // Task #49 (2026-05-21): snapshot the static seam locally so the
        // background task body sees a stable IHttpClient reference for the
        // full warmup loop (avoids a race where a test resets WarmupHttp
        // to null between StartAsync return and the warmup probe firing).
        // Default null means "fall back to the inline HttpClient" — the
        // production path that pre-Task-#49 always ran.
        var seamHttp = WarmupHttp;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Two probe shapes:
            //   • seamHttp != null → IHttpClient seam (test FakeHttpClient or
            //     a process-wide IHttpClient set by future production wiring).
            //   • seamHttp == null → inline HttpClient with the same Timeout
            //     as pre-Task-#49 (semantically identical to the original
            //     `using var http = new HttpClient { Timeout = 3s }`).
            // The inline-HttpClient branch is kept as the default so a
            // future refactor that wants to drop the static seam can do so
            // safely (no behaviour change for un-overridden production).
            using var inlineHttp = seamHttp == null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(3) }
                : null;
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    if (seamHttp != null)
                    {
                        // IHttpClient seam — return value is checked against
                        // 2xx to match the throw-on-non-2xx semantics of
                        // HttpClient.GetStringAsync used by the inline branch.
                        var resp = await seamHttp.SendAsync(
                            new HttpRequest(
                                HttpMethod.Get,
                                new Uri("https://www.gstatic.com/generate_204"),
                                Timeout: TimeSpan.FromSeconds(3)),
                            ct);
                        if (!resp.IsSuccess())
                            throw new HttpRequestException(
                                $"warmup probe HTTP {resp.StatusCode}");
                    }
                    else
                    {
                        await inlineHttp!.GetStringAsync(
                            "https://www.gstatic.com/generate_204", ct);
                    }
                    _host.Logger?.Information(
                        "[StartupPipeline] TUN ready after {Ms}ms (attempt {Attempt})",
                        sw.ElapsedMilliseconds, attempt);
                    _host.OnStatus($"Connected (PID {pidSnapshot})");

                    // Task #41 Stage 1 (PinkuDani 2026-05-21) — fire the
                    // typed Connected event on the engine. This is the
                    // ONLY call site for OnConnected; the symmetric
                    // failure branch below intentionally does NOT fire it,
                    // so App-side consumers can use this as the unambiguous
                    // "TUN really up" signal (vs the OnStatus string which
                    // is emitted on both branches for back-compat). Stage 2
                    // (App-side two-phase VM timer) depends on this
                    // invariant — see plans/phase4-vpnengine-connected-
                    // event-stage1-2026-05-21.md.
                    try { _host.OnConnected(pidSnapshot); }
                    catch (Exception ex)
                    {
                        _host.Logger?.Warning(ex,
                            "[StartupPipeline] OnConnected callback threw (non-fatal)");
                    }

                    // BR-7: arm the Wave 39 firewall DNS lockdown now that
                    // TUN is confirmed routing. Idempotent + fire-and-
                    // forget; doesn't affect the user-visible Connected
                    // state.
                    //
                    // Task #36-A (Phase 4) — routed through IWindowsDnsHardening
                    // so tests inject NullWindowsDnsHardening and capture the
                    // BR-7 branch without touching real netsh / firewall. The
                    // impl is a no-op on non-Windows (no #if needed at the
                    // call site).
                    try { _dnsHardening.EnableLockdownIfConfigured(settings, _host.Logger); }
                    catch (Exception ex)
                    {
                        _host.Logger?.Warning(ex,
                            "[StartupPipeline] BR-7 deferred lockdown arm threw (non-fatal)");
                    }
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _host.Logger?.Debug(
                        "[StartupPipeline] Warm-up attempt {Attempt}: {Error}",
                        attempt, ex.GetType().Name);
                }
            }
            _host.Logger?.Warning(
                "[StartupPipeline] TUN warm-up failed after {Ms}ms — " +
                "Wave 39 firewall DNS lockdown NOT installed (BR-7: prefer " +
                "internet-up + DNS-leak-risk over internet-down + lockdown-on)",
                sw.ElapsedMilliseconds);
            _host.OnStatus($"Connected (PID {pidSnapshot})");
            // Task #41 Stage 1 (PinkuDani 2026-05-21) — INTENTIONALLY NOT
            // calling _host.OnConnected here. The OnStatus string above is
            // ambiguous (it's emitted on both branches for back-compat with
            // pre-#41 consumers that scan StatusChanged for "Connected (PID");
            // the typed OnConnected event must stay silent so App-side
            // consumers can distinguish actual TUN-ready from "warmup loop
            // expired but we let sing-box live anyway." Do NOT add a call
            // here without first migrating Stage 2 off the
            // success-branch-only invariant.
        }, ct);
    }

    // ─── Phase 8: StartMonitors (ETW + HealthMonitor + Windows DNS) ────────

    /// <summary>
    /// Wire up the ETW process monitor + HealthMonitor + apply Windows DNS
    /// hardening (Windows only — SMHNR off, TUN metric, etc.).
    /// </summary>
    private void StartMonitorsPhase(
        AppSettings settings,
        Profile activeProfile,
        ScanResult scanResult)
    {
        var profile = activeProfile;
        // ETW + HealthMonitor are owned by VpnEngine via Set* callbacks so
        // Stop()/Dispose() can dispose them.
        var etw = _host.MonitorFactory();
        _host.SetProcessMonitor(etw);

        // The SingBoxManager + IFirewallManager have already been wired to
        // the host in phases 6+7. Pull them back via a lightweight
        // accessor — but we don't expose those getters; instead the host's
        // SetHealthMonitor / SetProcessMonitor takes responsibility for
        // disposal. The HealthMonitor needs SingBoxManager + scanner +
        // firewall; we construct it here and hand it over.
        var singBox = ((StartupHostInternal)_host).SingBox
            ?? throw new InvalidOperationException(
                "StartupPipeline phase 8: SingBoxManager missing (phase 7 didn't set it).");
        var firewall = ((StartupHostInternal)_host).Firewall
            ?? throw new InvalidOperationException(
                "StartupPipeline phase 8: IFirewallManager missing (phase 6 didn't set it).");

        var healthMonitor = new HealthMonitor(
            singBox, _host.Scanner, firewall,
            settings.Monitoring, _host.Logger);

        etw.ProcessStarted += (_, e) =>
        {
            var isTargeted = profile.Processes
                .Any(r => r.ScanPatterns
                    .Any(p => ProcessScanner.MatchesPattern(e.ProcessName + ".exe", p)));

            if (isTargeted)
            {
                _host.OnProcessDetected(e.ProcessName, e.ProcessId);
                healthMonitor.OnNewProcessDetected(e.ProcessName);
            }
        };

        healthMonitor.RestartAttempted += (_, attempt) =>
            _host.OnRestartAttempted(attempt, settings.Monitoring.MaxRestartAttempts);

        // G4 (2026-06-27): when restarts hit the ceiling, hand off to AutoFailover
        // (swap to a healthy server) instead of silently giving up.
        healthMonitor.FailoverRequested += (_, reason) => _host.OnFailoverRequested(reason);

        etw.Start();
        healthMonitor.Start(profile, settings, scanResult);

        _host.SetHealthMonitor(healthMonitor);

        if (profile.BlockOnVpnFail)
            _host.OnStatus("Firewall leak protection ready (armed for VPN failure)");

        // Wave 39 (2026-05-19): pass settings so WindowsDnsHardening can
        // honour the AppConfig.DnsLeakLockdown toggle and install the
        // Wave 39 firewall-level DNS port blocks alongside the existing
        // SMHNR / ParallelAAAA / TUN-metric hardening.
        //
        // Task #36-A (Phase 4) — routed through IWindowsDnsHardening so
        // happy-path lifecycle tests (Task #36-C) inject NullWindowsDnsHardening
        // and capture this invocation without touching HKLM. Impl is a
        // no-op on non-Windows, replacing the prior #if PLATFORM_WINDOWS
        // guard at the call site.
        _dnsHardening.Apply(settings, _host.Logger);
    }
}

/// <summary>
/// Internal contract used by phase 8 to retrieve previously-set lifecycle
/// objects without exposing public getters on IStartupHost. VpnEngine
/// implements both interfaces; phase 8 casts to this one. Tests that drive
/// the pipeline standalone implement both — see StartupPipelineTests'
/// fake host class for the pattern.
/// </summary>
internal interface StartupHostInternal : IStartupHost
{
    SingBoxManager? SingBox { get; }
    IFirewallManager? Firewall { get; }
}
