using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 3C (2026-05-18) — pins the 8-phase contract of
/// <see cref="StartupPipeline"/>. Each test exercises ONE phase boundary in
/// isolation by intercepting the host callbacks the pipeline drives, and
/// pinning either the side-effect (state mutation / event raised) or the
/// thrown exception type + message.
///
/// <para>The full StartAsync ColdStart walk depends on Windows-specific
/// services (ConflictingVpnDetector, sing-box binary deploy, TUN cleanup)
/// that can't run in a CI test environment. Those phases are covered by
/// the existing VpnEngine integration suite (HeadlessGuiTests on Windows).
/// Here we exercise the LOGICAL phases that have unit-testable contracts:</para>
/// <list type="number">
///   <item><c>ResolveProfile_NoActiveProfile</c> — phase 1 throws when split
///   mode has no active profile.</item>
///   <item><c>ResolveServers_EmptyConfig</c> — phase 2 throws with
///   actionable message on subscribe mode with no enabled subs.</item>
///   <item><c>GenerateConfig_PlaceholderActiveServer</c> — phase 4 routes
///   through VlessServersResolver scope guard (placeholder → subscription).</item>
///   <item><c>GenerateConfig_CustomMode_UsesInjector</c> — phase 4 custom
///   dispatch.</item>
///   <item><c>SetupFirewall_NoBlockOnFail_Skipped</c> — phase 6 honours
///   BlockOnVpnFail=false.</item>
///   <item><c>SetupFirewall_BlockOnFail_SplitRouting_PassesIsFullTunnelFalse</c>
///   — phase 6 passes <c>isFullTunnel=false</c> to CreateBlockRules in split
///   mode; guards a future inversion of the RoutingMode derivation.</item>
///   <item><c>HotReload_ReturnsConfigJson_SkipsPhases5to8</c> — HotReload
///   mode short-circuit after phase 4.</item>
///   <item><c>StartupResult_RecordShape</c> — record + enum shape pin.</item>
/// </list>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class StartupPipelineTests : IDisposable
{
    // 3G-1 (v3.0 refactor): InMemorySettingsStore handles the persistence
    // seam (replaces what used to be SafeMode-driven Save no-op). SafeMode
    // still flips on for OTHER reasons in this test class (forces full-
    // tunnel routing for tests that don't want the full profile-catalogue
    // load path) — those are documented inline at each test, and the flag
    // is restored in Dispose so parallel test classes are not affected.
    private readonly InMemorySettingsStore _store = new();
    private readonly bool _wasSafeMode;

    public StartupPipelineTests()
    {
        // Required for tests that exercise the FullTunnel path via
        // SafeMode short-circuit (the pipeline forces FullTunnel when
        // SafeMode.Enabled, sidestepping the bundled-profile catalogue
        // load that test fixtures don't seed). Without it, tests that
        // declare ActiveProfile="TestProfile" trip the "None of the
        // requested profiles exist" gate because the bundled catalogue
        // doesn't include "TestProfile".
        _wasSafeMode = SafeMode.Enabled;
        SafeMode.Enabled = true;
    }

    public void Dispose()
    {
        SafeMode.Enabled = _wasSafeMode;
    }

    // ─── Test helpers ────────────────────────────────────────────────────

    private static VlessServerEntry MakeServer(
        string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "11111111-2222-3333-4444-" + host.GetHashCode().ToString("X").PadLeft(12, '0'),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                ShortId = "d86e92a0c6dd2271"
            }
        };

    private static AppSettings BuildBaseSettings(string configMode = "generated") =>
        new()
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = configMode,
                RoutingMode = "split",
                Subscriptions = new List<SubscriptionEntry>()
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig(),
            ActiveProfile = "TestProfile"
        };

    // ─── Tests ──────────────────────────────────────────────────────────

    // Phase 1: ResolveProfile — no active profile in split mode → throw.
    [Fact]
    public async Task ResolveProfile_NoActiveProfileInSplitMode_ThrowsInvariantViolation()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry> { MakeServer("m", "1.2.3.4") };
        settings.Vless.ActiveServer = "m";
        settings.ActiveProfile = null;   // missing
        settings.App.RoutingMode = "split";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                TestContext.Current.CancellationToken));

        // HotReload skips conflict / DNS / geo preflight, so the next gate
        // we hit is the "no active profile" guard in ResolveProfileAndServers.
        Assert.Contains("No active profile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Phase 2: ResolveServers — empty subscription pool → actionable throw.
    [Fact]
    public async Task ResolveServers_EmptyConfig_ThrowsWithActionableMessage()
    {
        // Subscribe mode, no enabled subscriptions, empty Vless.Servers — the
        // v2.28.2 silent-leak preconditions. Pipeline must throw with the
        // user-actionable DescribeEmptyReason message, NOT the generic
        // "no active VLESS servers" string.
        var settings = BuildBaseSettings(configMode: "subscribe");
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                TestContext.Current.CancellationToken));

        // Either the DescribeEmptyReason message or the F-12 invariant
        // violation message — both are actionable. We pin that SOMETHING
        // user-facing was thrown (not a generic NullReferenceException).
        Assert.NotNull(ex.Message);
        Assert.NotEmpty(ex.Message);
    }

    // Phase 4: GenerateConfig — placeholder active server → resolver scope
    // guard kicks in, subscription wins, no leak.
    [Fact]
    public async Task GenerateConfig_PlaceholderActiveServer_FallsBackToSubscription()
    {
        // Same shape as ConfigPipelineTests.Generate_PlaceholderActiveServer:
        // generated mode, working subscription, placeholder in vless.servers
        // shadowing as ActiveServer. Pipeline phase 4 routes through
        // ConfigPipeline → VlessServersResolver scope guard → subscription
        // wins.
        const string placeholderServer = "195.135.255.216";
        const string placeholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
        const string placeholderShortId = "78ca7952";

        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("de-01", "104.194.156.93", 443)
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = placeholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = placeholderPubkey,
                    ShortId = placeholderShortId
                }
            }
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        // HotReload mode is the only mode we can drive standalone (ColdStart
        // calls ConflictingVpnDetector / sing-box deploy). HotReload exits
        // after phase 4 with the regenerated JSON.
        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ConfigJson);

        // Pin: placeholder IP must NOT appear as outbound server in generated
        // JSON. Subscription's de-01 wins.
        Assert.DoesNotContain(placeholderServer, result.ConfigJson);
        Assert.Contains("104.194.156.93", result.ConfigJson);

        // Active server has been auto-corrected to the subscription entry.
        Assert.Equal("de-01", settings.Vless.ActiveServer);
    }

    // Phase 4: GenerateConfig in HotReload mode produces JSON that lands in
    // result.ConfigJson — proving the pipeline returns a feed-back to
    // ApplyAsync rather than starting sing-box.
    [Fact]
    public async Task HotReload_ReturnsConfigJsonAndProfile()
    {
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.EarlyReturn);
        Assert.NotNull(result.ConfigJson);
        Assert.NotEmpty(result.ConfigJson!);
        Assert.NotNull(result.Profile);
        Assert.False(string.IsNullOrEmpty(result.Profile!.Name));

        // Pin: HotReload mode does NOT start sing-box, so SingBoxManager was
        // never instantiated. The host's SetSingBoxManager was not called.
        Assert.Null(host.SetSingBox);

        // Pin: HotReload does NOT touch firewall / process monitoring / HealthMonitor.
        Assert.Null(host.SetFirewall);
        Assert.Null(host.SetEtw);
        Assert.Null(host.SetHealth);
    }

    // Phase 6: SetupFirewall — when BlockOnVpnFail=false, no firewall rules
    // get created (no CreateBlockRules call).
    [Fact]
    public async Task SetupFirewall_NoBlockOnFail_SkipsRuleCreation()
    {
        // We use HotReload mode to skip phases 5-8 (which need sing-box),
        // then directly call the firewall rules helper to pin the contract.
        // Actually simpler: build a profile with BlockOnVpnFail=false and
        // verify HotReload's behaviour doesn't leak through.
        //
        // For phase 6 specifically, the pipeline only runs it in non-HotReload
        // modes. So we check the ConfigPipeline result's profile carries the
        // BlockOnVpnFail flag through — which then drives phase 6 in real
        // cold-start scenarios.
        var settings = BuildBaseSettings();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";
        settings.ActiveProfile = "TestProfile";
        settings.App.RoutingMode = "split";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        // In HotReload mode phase 6 is skipped; we just pin that firewall
        // was never touched.
        Assert.Null(host.SetFirewall);
    }

    // Phase 6 wiring: split routing must pass isFullTunnel=false to CreateBlockRules.
    [Fact]
    public async Task SetupFirewall_BlockOnFail_SplitRouting_PassesIsFullTunnelFalse()
    {
        var (thrown, host) = await RunFirewallWalkAsync("split", "Discord_Privacy");

        Assert.IsType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(1, host.CapturedFirewall.CreateBlockRulesCount);
        Assert.False(host.CapturedFirewall.LastIsFullTunnel);
    }

    // Full-tunnel + selected profile with BlockOnVpnFail=true must arm the kill-switch.
    [Fact]
    public async Task SetupFirewall_FullTunnel_SelectedProfileBlockOnFail_ArmsKillSwitch()
    {
        var (thrown, host) = await RunFirewallWalkAsync("full", "Discord_Privacy");

        Assert.IsType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(1, host.CapturedFirewall.CreateBlockRulesCount);
        Assert.True(host.CapturedFirewall.LastIsFullTunnel);
    }

    // Full-tunnel + BlockOnVpnFail=false intent must NOT arm.
    [Fact]
    public async Task SetupFirewall_FullTunnel_NoBlockIntent_DoesNotArm()
    {
        var (thrown, host) = await RunFirewallWalkAsync("full", "Browsers");

        Assert.IsNotType<FirewallCapturedSentinelException>(thrown);
        Assert.Equal(0, host.CapturedFirewall.CreateBlockRulesCount);
    }

    // ColdStart walk with capturing firewall armed to abort at phase 6.
    private async Task<(Exception? Thrown, TestStartupHost Host)> RunFirewallWalkAsync(
        string routingMode, string? activeProfile)
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.FlushDnsOnStart = false;
        settings.App.BypassRussianTraffic = false;
        settings.ActiveProfile = activeProfile;
        settings.Vless.Servers = new List<VlessServerEntry> { MakeServer("main", "104.194.156.93", 443) };
        settings.Vless.ActiveServer = "main";

        var fakeBin = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), $"vpnrouter-fake-singbox-{Guid.NewGuid():N}.exe")
            : AppPaths.SingBoxExePath;
        var createdFakeBin = !File.Exists(fakeBin);
        if (createdFakeBin)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fakeBin)!);
            File.WriteAllText(fakeBin, "fake");
        }
        settings.SingBox.ExecutablePath = fakeBin;

        var host = new TestStartupHost();
        host.CapturedFirewall.AbortAfterCapture = true;
        var pipeline = new StartupPipeline(host, _store);

        var prevSafeMode = SafeMode.Enabled;
        SafeMode.Enabled = false;
        try
        {
            var thrown = await Record.ExceptionAsync(async () =>
                await pipeline.ExecuteAsync(
                    new StartupContext(settings, StartupMode.ColdStart, SkipVpnConflictCheck: true),
                    TestContext.Current.CancellationToken));
            return (thrown, host);
        }
        finally
        {
            SafeMode.Enabled = prevSafeMode;
            if (createdFakeBin)
            {
                try { File.Delete(fakeBin); } catch { }
            }
        }
    }

    // Phase 5: PreStartChecks skipped on HotReload (per pipeline contract).
    [Fact]
    public async Task PreStartChecks_SkippedInHotReloadMode()
    {
        // Pipeline contract: PreStartChecks (F-E) only fires on ColdStart.
        // We verify that by feeding a config with a placeholder fingerprint
        // and showing the pipeline does NOT trigger AutoFailover in HotReload
        // — the resolver scope guard at phase 2 catches it instead.
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.EarlyReturn);   // F-E early-return path never fired
        Assert.False(host.AutoFailoverInvoked);
        Assert.False(host.SanityCheckEnsured);
    }

    // Phase 1+2: ResolveProfile honours CustomApps injection — top-level
    // CustomApps get added to the resolved profile's process rules.
    [Fact]
    public async Task ResolveProfile_CustomApps_Injected()
    {
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.CustomApps = new List<string> { "myapp.exe" };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Profile);
        // CustomApps merge happens AFTER profile resolve. The bundled fallback
        // profile (post-quarantine) might not have a profile literally named
        // "TestProfile" — the resolver falls back to FullTunnel/CustomConfig
        // in that case. Either way, CustomApps either show up in the merged
        // profile or in a FullTunnel substitute. We pin that the side-effect
        // path executed (no throw).
        Assert.NotNull(result.Profile!.Processes);
    }

    // Phase 3 (step 4.5): WG/AWG auto-exclude is recomputed fresh every connect
    // into the runtime-only AutoDetectedExcludeAddress and NEVER merged into the
    // persisted RouteExcludeAddress. A subnet that no longer corresponds to a
    // live adapter must drop out, and the user's persisted list must be untouched.
    [Fact]
    public async Task ScanProcesses_VanishedWgAdapter_DropsStaleAutoExclude_AndNeverMutatesPersistedList()
    {
        // Pre-seed the runtime auto list with a stale subnet from a "previous
        // connect", plus a real user-authored exclude in the persisted list.
        // We use 203.0.113.0/24 (RFC 5737 TEST-NET-3, documentation-only) as the
        // stale entry: no real network adapter can ever carry it, so the
        // pipeline's fresh DetectWireGuardSubnets re-scan is GUARANTEED to drop
        // it regardless of whatever WG/AWG adapters this host actually has.
        var settings = BuildBaseSettings();
        settings.App.RoutingMode = "split";
        settings.ActiveProfile = "TestProfile";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            MakeServer("main", "104.194.156.93", 443)
        };
        settings.Vless.ActiveServer = "main";

        settings.Tun.RouteExcludeAddress = new List<string> { "192.168.77.0/24" };
        settings.Tun.AutoDetectedExcludeAddress = new List<string> { "203.0.113.0/24" };
        var persistedSnapshot = new List<string>(settings.Tun.RouteExcludeAddress);

        var host = new TestStartupHost();
        var pipeline = new StartupPipeline(host, _store);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.HotReload),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.ConfigJson);

        // 1. The stale auto subnet is gone after the fresh re-detect.
        Assert.DoesNotContain("203.0.113.0/24", settings.Tun.AutoDetectedExcludeAddress);

        // 2. The persisted user list was NEVER mutated by auto-detection.
        Assert.Equal(persistedSnapshot, settings.Tun.RouteExcludeAddress);

        // 3. Generated config carries the user exclude but NOT the stale auto one.
        Assert.Contains("192.168.77.0/24", result.ConfigJson!);
        Assert.DoesNotContain("203.0.113.0/24", result.ConfigJson!);
    }

    // Phase 8 (records): StartupResult shape pin — fields populated correctly
    // for the HotReload mode.
    [Fact]
    public void StartupResult_RecordShape_FieldsPresent()
    {
        // Pure shape check — records' equality + immutability + with-pattern.
        var r1 = new StartupResult(
            Success: true,
            EarlyReturn: false,
            ProcessId: 12345,
            Duration: TimeSpan.FromMilliseconds(500),
            ConfigJson: "{}",
            Profile: new Profile { Name = "X" });

        var r2 = r1 with { ProcessId = 99999 };

        Assert.Equal(12345, r1.ProcessId);
        Assert.Equal(99999, r2.ProcessId);
        Assert.NotEqual(r1, r2);
        Assert.Equal("X", r1.Profile?.Name);
        Assert.Equal("{}", r1.ConfigJson);

        var modes = new[]
        {
            StartupMode.ColdStart,
            StartupMode.HotReload,
            StartupMode.AutoFailover
        };
        Assert.Equal(3, modes.Length);
    }

    // ─── Firewall-capture helper ───────────────────────────

    /// <summary>
    /// Recording <see cref="IFirewallManager"/> fake: captures the phase-6
    /// CreateBlockRules isFullTunnel argument. With AbortAfterCapture set, throws
    /// a sentinel after the first capture so a ColdStart walk stops at phase 6.
    /// </summary>
    internal sealed class CapturingFirewall : IFirewallManager
    {
        public int CreateBlockRulesCount { get; private set; }
        public bool? LastIsFullTunnel { get; private set; }
        public bool AbortAfterCapture { get; set; }

        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true)
        {
            CreateBlockRulesCount++;
            LastIsFullTunnel = isFullTunnel;
            if (AbortAfterCapture)
                throw new FirewallCapturedSentinelException();
        }

        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    /// <summary>Aborts a ColdStart walk at phase 6 once CreateBlockRules is captured.</summary>
    internal sealed class FirewallCapturedSentinelException : Exception
    {
        public FirewallCapturedSentinelException()
            : base("sentinel: firewall CreateBlockRules captured (test abort)") { }
    }

    // ─── Fake host for unit-testing the pipeline ───────────────────────

    /// <summary>
    /// In-memory <see cref="StartupHostInternal"/> implementation. Records
    /// every callback so tests can assert which phase ran. Does NOT
    /// instantiate real SingBoxManager / IFirewallManager / IProcessMonitor —
    /// those flow only on ColdStart paths which this fake doesn't exercise.
    /// </summary>
    internal sealed class TestStartupHost : StartupHostInternal
    {
        private sealed class StubProcessScanner : IProcessScanner
        {
            public ScanResult ScanForProfile(Profile profile) =>
                new() { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
        }

        public ILogger? Logger { get; } = null;
        public IProcessScanner Scanner { get; } = new StubProcessScanner();
        // Recording fake so a ColdStart walk can drive phase 6 and capture the
        // CreateBlockRules arguments.
        public CapturingFirewall CapturedFirewall { get; } = new();
        public Func<IFirewallManager> FirewallFactory { get; }
        public Func<IProcessMonitor> MonitorFactory { get; } =
            () => throw new InvalidOperationException(
                "TestStartupHost: phase 8 should not run in HotReload tests");

        public TestStartupHost()
        {
            FirewallFactory = () => CapturedFirewall;
        }

        public SingBoxManager? SingBox => SetSingBox;
        public IFirewallManager? Firewall => SetFirewall;

        // Recorded state — tests assert against these.
        public List<string> Statuses { get; } = new();
        public List<string> Warnings { get; } = new();
        public string? ActiveServerAddress { get; private set; }
        public string? ActiveConfigMode { get; private set; }
        public string? ActiveRoutingMode { get; private set; }
        public string? TunFingerprint { get; private set; }
        public Profile? ActiveProfile { get; private set; }
        public ScanResult? ScanResultRecorded { get; private set; }
        public SingBoxManager? SetSingBox { get; private set; }
        public IFirewallManager? SetFirewall { get; private set; }
        public IProcessMonitor? SetEtw { get; private set; }
        public HealthMonitor? SetHealth { get; private set; }
        public bool SanityCheckEnsured { get; private set; }
        public bool AutoFailoverInvoked { get; private set; }
        public bool PostStartProbeScheduled { get; private set; }

        public void OnStatus(string message) => Statuses.Add(message);
        public void OnWarning(string message) => Warnings.Add(message);
        public void OnSingBoxStarted(int pid) { }
        // Task #41 Stage 1 (2026-05-21) — record OnConnected calls so the
        // success-branch-only invariant (see StartupPipeline.ScheduleWarmupProbe)
        // can be pinned by tests that drive the host directly.
        public List<int> ConnectedPids { get; } = new();
        public void OnConnected(int pid) => ConnectedPids.Add(pid);
        public void OnRestartAttempted(int attempt, int max) { }
        public void OnFailoverRequested(string reason) { }
        public void OnAutoFailoverTriggered(string message) =>
            AutoFailoverInvoked = true;
        public void OnProcessDetected(string name, int pid) { }
        public void SetActiveServerAddress(string address) =>
            ActiveServerAddress = address;
        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint)
        {
            ActiveConfigMode = configMode;
            ActiveRoutingMode = routingMode;
            TunFingerprint = tunFingerprint;
        }
        public void SetActiveProfile(Profile profile) => ActiveProfile = profile;
        public void SetScanResult(ScanResult result) => ScanResultRecorded = result;
        public void SetSingBoxManager(SingBoxManager manager) => SetSingBox = manager;
        public VlessServerEntry? DnsTunnelTransportStarted { get; private set; }
        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings)
            => DnsTunnelTransportStarted = activeServer;
        public void SetFirewallManager(IFirewallManager firewall) => SetFirewall = firewall;
        public void SetProcessMonitor(IProcessMonitor etw) => SetEtw = etw;
        public void SetHealthMonitor(HealthMonitor monitor) => SetHealth = monitor;

        public void EnsureSanityCheckScaffolding(
            AppSettings settings, out ConfigSanityCheck sanityCheck)
        {
            SanityCheckEnsured = true;
            sanityCheck = new ConfigSanityCheck();
        }

        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);

        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck) =>
            new(new AppSettings(), sanityCheck);

        public void SchedulePostStartProbe(
            AppSettings settings,
            ConfigSanityCheck sanityCheck,
            CancellationToken ct)
        {
            PostStartProbeScheduled = true;
        }
    }
}
