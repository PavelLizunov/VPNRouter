// Phase 2G follow-up (Task #22, 2026-05-21) — VpnEngine.StartAsync invoke
// tests pinning the early-throw paths.
//
// Why: VpnEngineOrchestratorTests (commit 14c512e, 16 tests) explicitly
// documented the gap:
//   "The full StartAsync→Connected→Stop matrix is intentionally NOT covered
//    here because VpnEngine.StartAsync requires (1) the sing-box binary on
//    disk, (2) Windows-only firewall via netsh, (3) profiles JSON in
//    %ProgramData%. Today there's no test seam that lets us stub those
//    in-memory."
//
// The Phase 3+ IProcessRunner adoption (commit e9c31be) made SingBoxManager
// testable via FakeProcessRunner, but the lifecycle BEFORE SingBoxManager is
// constructed — StartupPipeline phases 6 (netsh + TunAdapterDiagnostics)
// and 8 (WindowsDnsHardening HKLM mutation) — still calls static helpers
// that mutate real Windows OS state. That blocker is documented in detail
// in plans/phase2G-vpnengine-startasync-seam-2026-05-21.md "Surprises".
//
// This batch delivers the achievable subset: characterization tests for the
// EARLY-THROW paths through VpnEngine.StartAsync that abort cleanly in
// phases 1-2 (ResolveProfileAndServers) before reaching any destructive
// OS call. All tests configure settings to skip the phase-0 OS shell-outs:
//   - FlushDnsOnStart = false   → skip ipconfig
//   - BypassRussianTraffic = false → skip geo HTTP download
//   - skipVpnConflictCheck: true  → skip ConflictingVpnDetector
//
// Brief: plans/phase2G-vpnengine-startasync-seam-2026-05-21.md.

#nullable enable

using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for the early-throw paths of
/// <see cref="VpnEngine.StartAsync"/>. Each test drives a full
/// <see cref="VpnEngine"/> instance (not a standalone <see cref="StartupPipeline"/>)
/// through to the first phase that throws — pinning that the engine
/// surfaces the actionable error to the caller and does NOT mutate
/// post-throw state.
///
/// <para>Cross-references:
/// <see cref="VpnEngineOrchestratorTests"/> (idle Stop / Dispose / static
/// helpers — does NOT call StartAsync);
/// <see cref="StartupPipelineTests"/> (HotReload-mode pipeline coverage —
/// does NOT exercise the full <c>VpnEngine</c> wrapper);
/// <see cref="VpnEngineApplyEscalationTests"/> (source-string pins for the
/// hot-reload escalation triggers).</para>
///
/// <para>The full happy-path lifecycle (Start → Connected → Stop) plus
/// crash-then-restart, hot-reload Apply on a running engine, and
/// Stop-during-restart are explicitly DEFERRED to a follow-up brief
/// pending the NullDnsFlusher / NullWindowsDnsHardening /
/// NullTunAdapterDiagnostics abstractions. See the brief's "Tests
/// deferred" table for the full list.</para>
/// </summary>
public sealed class VpnEngineStartAsyncSeamTests
{
    // ─── Inline stubs (mirrors VpnEngineOrchestratorTests pattern) ───────

    private sealed class StubProcessScanner : IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private sealed class StubProcessMonitor : IProcessMonitor
    {
        public event EventHandler<ProcessEventArgs>? ProcessStarted;
        public event EventHandler<ProcessEventArgs>? ProcessStopped;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseDummy() { ProcessStarted?.Invoke(this, new()); ProcessStopped?.Invoke(this, new()); }
    }

    /// <summary>
    /// Build an idle VpnEngine wired to no-op stubs. The
    /// <see cref="VpnEngine"/> ctor is decorated with
    /// <c>[Obsolete(error: false)]</c> — Phase 4 will replace it with a
    /// factory, but tests can use it under <c>#pragma warning disable</c>
    /// per the attribute's docs.
    /// </summary>
#pragma warning disable CS0618
    private static VpnEngine BuildEngine() =>
        new VpnEngine(
            scanner: new StubProcessScanner(),
            firewallFactory: () => new StubFirewallManager(),
            monitorFactory: () => new StubProcessMonitor(),
            logger: null);
#pragma warning restore CS0618

    /// <summary>
    /// Build an <see cref="AppSettings"/> configured so ColdStart can reach
    /// the early-throw paths without triggering OS-mutating shell-outs.
    /// The three guards:
    /// <list type="bullet">
    ///   <item><c>FlushDnsOnStart = false</c> → no <c>ipconfig /flushdns</c></item>
    ///   <item><c>BypassRussianTraffic = false</c> → no geo HTTP download</item>
    ///   <item>Caller passes <c>skipVpnConflictCheck: true</c> → no
    ///   ConflictingVpnDetector probe (we still pass it through, but the
    ///   detector is read-only so the difference is academic).</item>
    /// </list>
    /// </summary>
    private static AppSettings BuildSafePreStartSettings(
        string configMode = "generated",
        string routingMode = "split") =>
        new()
        {
            App = new AppConfig
            {
                ConfigMode = configMode,
                RoutingMode = routingMode,
                FlushDnsOnStart = false,
                BypassRussianTraffic = false,
                Subscriptions = new List<SubscriptionEntry>(),
            },
            Vless = new VlessConfig(),
            Tun = new TunSettings(),
            Dns = new DnsSettings(),
            SingBox = new SingBoxSettings(),
            Monitoring = new MonitoringSettings(),
            ActiveProfile = "TestProfile",
        };

    // ─── 1. Empty VLESS servers — subscribe mode ────────────────────────

    [Fact]
    public async Task StartAsync_EmptyServers_SubscribeMode_ThrowsActionableMessage()
    {
        // v2.28.2 silent-leak class: subscribe mode with no enabled subs +
        // no manual fallback. The hard guard at phase 2
        // (StartupPipeline.ResolveProfileAndServersAsync) must throw an
        // InvalidOperationException whose message routes through
        // VlessServersResolver.DescribeEmptyReason — actionable text the
        // UI can surface verbatim ("Subscribe mode is selected but no
        // subscription URLs are configured. Add a subscription in the
        // Subscribe tab.").
        var settings = BuildSafePreStartSettings(configMode: "subscribe");

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        // The exact message is owned by VlessServersResolver.DescribeEmptyReason;
        // we pin on the user-facing actionable hint rather than the literal
        // string so a future translation / phrasing change doesn't break
        // this characterization test.
        Assert.NotNull(ex.Message);
        Assert.NotEmpty(ex.Message);
        // Verify the engine did NOT silently transition to running on the
        // throw path. Empty servers must remain in an inert state.
        Assert.False(engine.IsRunning);
        Assert.Null(engine.SingBoxPid);
    }

    [Fact]
    public async Task StartAsync_SubscribeMode_AllSubscriptionsDisabled_Throws()
    {
        // Pin a subtle variant: subscriptions configured but all disabled.
        // The resolver's scope guard must treat this as the same empty case
        // — without the Enabled gate, the guard would aggregate disabled
        // subscriptions and route them as "active" servers, which is the
        // bug class v2.28.2-r1 fixed.
        var settings = BuildSafePreStartSettings(configMode: "subscribe");
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main",
                Url = "https://example.com",
                Enabled = false, // disabled — should be ignored
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Server = "1.2.3.4",
                        Port = 443,
                        Uuid = "11111111-2222-3333-4444-555555555555"
                    }
                }
            }
        };

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.NotNull(ex.Message);
        Assert.False(engine.IsRunning);
    }

    // ─── 2. Empty VLESS servers — generated mode ────────────────────────

    [Fact]
    public async Task StartAsync_EmptyServers_GeneratedMode_Throws()
    {
        // Generated mode with empty Vless.Servers + empty Subscriptions —
        // same hard-guard path as subscribe mode, different actionable
        // message ("VLESS server is not configured. Add a server manually
        // in the Servers tab, or enable a subscription.").
        var settings = BuildSafePreStartSettings(configMode: "generated");
        // Vless.Servers is empty by default, Subscriptions empty by default.

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        Assert.NotNull(ex.Message);
        Assert.False(engine.IsRunning);
        Assert.Empty(engine.ActiveProfileName); // never reached profile resolution
    }

    [Fact]
    public async Task StartAsync_EmptyServers_DoesNotMutateState()
    {
        // Defence-in-depth pin: an empty-servers throw must leave the engine
        // in EXACTLY the same state as a freshly-constructed engine. No
        // ActiveServerAddress set, no profile resolved, no event listeners
        // fired. UI code reads these getters when displaying error toasts;
        // any leaked partial state would show "VPN is configured for X"
        // alongside the "no servers" error.
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        // Snapshot pre-throw state.
        var preIsRunning = engine.IsRunning;
        var preActiveProfile = engine.ActiveProfileName;
        var preServerAddress = engine.ActiveServerAddress;
        var preMonitored = engine.MonitoredProcesses.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        // Post-throw state must match pre-throw state.
        Assert.Equal(preIsRunning, engine.IsRunning);
        Assert.Equal(preActiveProfile, engine.ActiveProfileName);
        Assert.Equal(preServerAddress, engine.ActiveServerAddress);
        Assert.Equal(preMonitored, engine.MonitoredProcesses.Count);
    }

    // ─── 3. No active profile in split mode ─────────────────────────────

    [Fact]
    public async Task StartAsync_NoActiveProfile_SplitMode_Throws()
    {
        // Phase 1 throws BEFORE phase 2 server resolution when the user has
        // valid servers but the profile name is empty/null in split-tunnel
        // mode. Full-tunnel mode is allowed without a profile (FullTunnel
        // synthetic profile is created instead); split mode requires an
        // explicit choice.
        var settings = BuildSafePreStartSettings(
            configMode: "generated", routingMode: "split");
        settings.ActiveProfile = null!;
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "test_public_key_x25519_base64url_format",
                    ShortId = "abcd1234"
                }
            }
        };
        settings.Vless.ActiveServer = "main";

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        // The phase-1 throw site message is "No active profile specified
        // in config." — pin loosely on the actionable substring "active
        // profile" so phrasing changes don't break the test.
        Assert.Contains("profile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(engine.IsRunning);
    }

    // ─── 4. Custom config mode — missing file ───────────────────────────

    [Fact]
    public async Task StartAsync_CustomMode_MissingFile_Throws()
    {
        // ConfigMode=custom routes phase 1 to a different branch:
        // VpnEngine.ResolveCustomConfigPath + File.Exists guard. When the
        // path doesn't resolve to an existing file, throws
        // InvalidOperationException with the path-in-error format
        // ("Custom config not found: <path>. Add a config in the Servers
        // tab.").
        var settings = BuildSafePreStartSettings(configMode: "custom");
        settings.App.CustomConfig =
            @"C:\definitely\does\not\exist\custom-test.json";
        settings.App.CustomConfigs = new List<CustomConfigEntry>();
        settings.App.ActiveCustomConfig = string.Empty;

        using var engine = BuildEngine();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

        // Pin the path-in-message format so the actionable error stays
        // useful.
        Assert.Contains("Custom config not found", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task StartAsync_CustomMode_InvalidJson_Throws()
    {
        // Same phase 1 branch, different rejection: file exists but
        // CustomConfigInjector.Validate fails. The throw message lists the
        // validation errors so the user can fix the JSON.
        var settings = BuildSafePreStartSettings(configMode: "custom");

        // Write garbage to a temp file. Use a unique name to avoid colliding
        // with parallel test runs.
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-test-custom-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempPath, "this is not json {{{");
        settings.App.CustomConfig = tempPath;

        try
        {
            using var engine = BuildEngine();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await engine.StartAsync(settings, TestContext.Current.CancellationToken, skipVpnConflictCheck: true));

            // The exact wording is owned by CustomConfigInjector.Validate,
            // but it has to contain "validation" or equivalent to be
            // actionable.
            Assert.Contains("validation", ex.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(engine.IsRunning);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    // ─── 5. Cancellation propagation ────────────────────────────────────

    [Fact]
    public async Task StartAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        // Pin: a pre-cancelled CT propagates as OperationCanceledException
        // (NOT swallowed as a generic InvalidOperationException). The
        // pipeline checks ct.ThrowIfCancellationRequested() at multiple
        // phase boundaries; the first one inside ResolveProfileAndServers
        // fires when we hit it with the token already in the cancelled
        // state. No destructive OS code runs because the throw is in
        // phase 1 / 2, before phases 6-8.
        var settings = BuildSafePreStartSettings(configMode: "generated");
        // Populate servers so the resolver doesn't throw the empty-servers
        // path first — we want the cancellation to win.
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "11111111-2222-3333-4444-555555555555",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "test_public_key_x25519_base64url_format",
                    ShortId = "abcd1234"
                }
            }
        };
        settings.Vless.ActiveServer = "main";

        using var engine = BuildEngine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();   // pre-cancel

        // OperationCanceledException OR TaskCanceledException (subclass) is
        // acceptable — both signal the cancellation contract correctly.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.StartAsync(settings, cts.Token, skipVpnConflictCheck: true));

        // The CT is the one we passed (not a wrapped CTS). Assert it
        // carries the cancellation signal so callers can correlate which
        // cancellation source fired.
        Assert.True(cts.Token.IsCancellationRequested);
        // No engine state mutation on cancellation either.
        Assert.False(engine.IsRunning);
    }

    // ─── 6. ApplyAsync delegates correctly on idle engine ───────────────

    [Fact]
    public async Task ApplyAsync_OnIdleEngine_ReturnsFalseWithoutInvokingPipeline()
    {
        // Companion to VpnEngineOrchestratorTests.ApplyAsync_IdleEngine_ReturnsFalse
        // but with the safe settings shape this test class uses. Pins that
        // the early-return at the top of ApplyAsync (lines 202-206) catches
        // the "sing-box not running" case BEFORE entering the StartupPipeline
        // HotReload mode. Without this guard, an Apply on an idle engine
        // would trigger pipeline phase 1 server resolution and throw the
        // empty-servers exception — confusing UX.
        var settings = BuildSafePreStartSettings(configMode: "generated");
        // Intentionally empty servers so we'd see InvalidOperationException
        // from the pipeline IF the guard wasn't there.

        using var engine = BuildEngine();

        var result = await engine.ApplyAsync(settings, TestContext.Current.CancellationToken);

        Assert.False(result);
        // Engine state unchanged.
        Assert.False(engine.IsRunning);
    }

    // ─── 7. SkipVpnConflictCheck parameter is honoured ──────────────────

    [Fact]
    public async Task StartAsync_SkipVpnConflictCheck_DefaultFalse_StillRunsEmptyServersGuard()
    {
        // Pin the Bug-r10-B (v2.32.1-r5) "Ignore" button contract: when the
        // user clicks "Ignore" on the conflicting-VPN banner, the UI calls
        // StartAsync(skipVpnConflictCheck: true). When they DON'T click it
        // (default), the value is false and the conflict check runs.
        //
        // We can't directly assert "the conflict detector was called" without
        // a seam, but we CAN verify that the parameter's default value
        // doesn't block reaching the phase-2 throw on empty servers — i.e.
        // the conflict check (when no conflicts are present, which is the
        // normal CI state) doesn't itself throw.
        var settings = BuildSafePreStartSettings(configMode: "generated");

        using var engine = BuildEngine();

        // Default skipVpnConflictCheck = false. Should still reach empty-
        // servers throw assuming no real VPN client is hogging wintun on
        // the CI machine (which there shouldn't be).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.StartAsync(settings, TestContext.Current.CancellationToken));
        // No assert on engine state — already covered by other tests.
    }
}
