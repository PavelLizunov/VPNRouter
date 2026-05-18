using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// F-E (2026-05-11) — pin tests for <see cref="AutoFailoverEngine"/>.
///
/// <para>Covers the candidate-selection logic (subscription pool wins
/// over manual; placeholder fingerprints filtered out; already-tried
/// names skipped), the 3-attempt cap, custom-mode bypass, and that
/// the optional restart delegate is invoked exactly once per call.</para>
///
/// <para><b>3G-1 (v3.0 refactor):</b> migrated from the SafeMode-flipping
/// pattern to <see cref="InMemorySettingsStore"/>. The pre-3G class set
/// <c>SafeMode.Enabled = true</c> for the test lifetime so the engine's
/// <c>SettingsLoader.Save</c> calls no-op'd, but that global flip leaked
/// into parallel test classes — running SettingsLoaderRobustnessTests
/// concurrently with this class would see Load() take the SafeMode early-
/// return path and hand out defaults instead of parsing the test fixture,
/// flaking ~14 cases. <c>InMemorySettingsStore</c> kills the race by
/// giving each test an isolated in-memory store; SafeMode stays untouched.</para>
/// </summary>
public class AutoFailoverEngineTests
{
    private readonly InMemorySettingsStore _store = new();

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static AppSettings BuildSubscribeSettings(
        string activeServer = "active-1",
        params (string name, string server)[] subscriptionServers)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.Vless.ActiveServer = activeServer;
        settings.App.ActiveSubscriptionServer = activeServer;

        var sub = new SubscriptionEntry
        {
            Name = "main",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = subscriptionServers
                .Select(t => new VlessServerEntry
                {
                    Name = t.name,
                    Server = t.server,
                    Port = 443,
                    Uuid = "00000000-0000-0000-0000-000000000001",
                })
                .ToList(),
        };
        settings.App.Subscriptions.Add(sub);
        return settings;
    }

    // ─── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PicksNextSubscriptionServer()
    {
        // 3 subscription entries; active is the first → expect the second.
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"),
            ("srv-3", "1.2.3.3"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("test dead reason");

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);
        Assert.Equal("srv-2", settings.Vless.ActiveServer);
        Assert.Equal("srv-2", settings.App.ActiveSubscriptionServer);
    }

    [Fact]
    public async Task SkipsPlaceholderServersInPool()
    {
        // Pool has one placeholder fingerprint — engine must skip it.
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("placeholder", "195.135.255.216"),  // placeholder IP
            ("srv-3", "1.2.3.3"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("test");

        Assert.True(outcome.Switched);
        Assert.Equal("srv-3", outcome.NewActiveServer);
    }

    [Fact]
    public async Task StopsAfter3Attempts()
    {
        // 5 subscription servers; cycle should give up after 3.
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"),
            ("srv-3", "1.2.3.3"),
            ("srv-4", "1.2.3.4"),
            ("srv-5", "1.2.3.5"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        // Burn through 3 attempts.
        for (int i = 0; i < AutoFailoverEngine.MaxAttempts; i++)
        {
            var ok = await engine.HandleDeadConfigAsync($"dead-{i}");
            Assert.True(ok.Switched, $"Attempt {i + 1} should have switched");
        }

        // 4th call must NOT switch — cap reached.
        var fourth = await engine.HandleDeadConfigAsync("dead-4");
        Assert.False(fourth.Switched);
        Assert.NotNull(fourth.UserFacingMessage);
        Assert.Contains("Все серверы", fourth.UserFacingMessage!);
    }

    [Fact]
    public async Task NoSwitchInCustomMode()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"));
        settings.App.ConfigMode = "custom";

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("test");

        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.NotNull(outcome.UserFacingMessage);
        Assert.Contains("Кастомный", outcome.UserFacingMessage!);
        // Active server must be untouched.
        Assert.Equal("srv-1", settings.Vless.ActiveServer);
    }

    [Fact]
    public async Task SkipsAlreadyTriedServer()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"),
            ("srv-3", "1.2.3.3"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var first = await engine.HandleDeadConfigAsync("first dead");
        Assert.Equal("srv-2", first.NewActiveServer);

        // After the first switch, ActiveServer = srv-2. Another dead-config
        // event should rotate to srv-3, not back to srv-1.
        var second = await engine.HandleDeadConfigAsync("second dead");
        Assert.Equal("srv-3", second.NewActiveServer);
    }

    [Fact]
    public async Task UsesManualPoolWhenNoSubscriptions()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.ActiveServer = "manual-1";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "manual-1", Server = "10.0.0.1", Port = 443, Uuid = "u1" },
            new() { Name = "manual-2", Server = "10.0.0.2", Port = 443, Uuid = "u2" },
        };

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead");

        Assert.True(outcome.Switched);
        Assert.Equal("manual-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task ReturnsFalseWhenPoolEmpty()
    {
        // Single server + active = single server → no alternative.
        var settings = BuildSubscribeSettings(
            activeServer: "srv-only",
            ("srv-only", "1.2.3.4"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead");

        Assert.False(outcome.Switched);
        Assert.NotNull(outcome.UserFacingMessage);
    }

    [Fact]
    public async Task InvokesRestartDelegateExactlyOnce()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"));

        int restartCalls = 0;
        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(
            settings, sanity,
            restart: ct =>
            {
                Interlocked.Increment(ref restartCalls);
                return Task.FromResult(true);
            },
            store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead");

        Assert.True(outcome.Switched);
        Assert.Equal(1, restartCalls);
    }

    [Fact]
    public async Task ResetCycleClearsTriedSet()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        // Run one failover so TriedServers populates.
        _ = await engine.HandleDeadConfigAsync("dead");
        Assert.NotEmpty(engine.TriedServers);

        engine.ResetCycle();
        Assert.Empty(engine.TriedServers);
    }

    // ─── G-2 (r10 r9 audit) Bug-r10-F brat regression: generated + sub + legitimate manual ───
    //
    // Pre-r8, in generated mode with subscription enabled, a probe
    // failure on user's manually-picked Free Config triggered silent
    // swap to subscription's first server. r8 added a gate: if the
    // active is a legitimate manual (in vless.servers AND not a known
    // placeholder per VlessServersResolver.IsPlaceholderEntry), F-E
    // SKIPS auto-swap and surfaces a clear error instead.
    //
    // This pin tests the gate explicitly. Subscribe mode keeps auto-
    // swap, manual-only mode (no sub) keeps auto-switch through manual
    // pool — those are covered by other tests in this class.
    [Fact]
    public async Task GeneratedMode_SubEnabled_LegitimateManual_SkipsAutoSwap_Brat()
    {
        // Build state that matches brat's situation: generated mode,
        // sub enabled with 2 working servers, user picked manual Free
        // Config entry.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "main-brat",
                Url = "https://example.com/sub",
                Enabled = true,
                Servers = new List<VlessServerEntry>
                {
                    new() { Name = "de-01", Server = "1.2.3.4", Port = 443, Uuid = "sub-1" },
                    new() { Name = "is-01", Server = "5.6.7.8", Port = 443, Uuid = "sub-2" },
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            // Real manual entry, NOT in subscription, NOT a placeholder
            new()
            {
                Name = "⚡ [EE] 77.239.126.152:7443",
                Server = "77.239.126.152",
                Port = 7443,
                Uuid = "real-free-uuid",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "real-pubkey-not-placeholder",
                    ShortId = "abcdef01"
                }
            }
        };
        settings.Vless.ActiveServer = "⚡ [EE] 77.239.126.152:7443";

        int restartCalls = 0;
        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(
            settings, sanity,
            restart: _ => { Interlocked.Increment(ref restartCalls); return Task.FromResult(true); },
            store: _store);

        var outcome = await engine.HandleDeadConfigAsync("Clash API HTTP 504");

        // No auto-swap — user-facing error returned instead
        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.NotNull(outcome.UserFacingMessage);
        Assert.Equal(0, restartCalls);
        // ActiveServer must NOT have been clobbered
        Assert.Equal("⚡ [EE] 77.239.126.152:7443", settings.Vless.ActiveServer);
    }

    [Fact]
    public async Task GeneratedMode_NoSubscription_LegitimateManual_StillSwapsAcrossManualPool()
    {
        // Edge case: legacy direct-VLESS mode (no sub). User has 2
        // manual entries. F-E should STILL switch through them when
        // active dies — there's no other pool, "best of manual" is the
        // contract. This pins that the r8 gate doesn't over-block.
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions = new List<SubscriptionEntry>(); // no sub
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "manual-1", Server = "10.0.0.1", Port = 443, Uuid = "u1" },
            new() { Name = "manual-2", Server = "10.0.0.2", Port = 443, Uuid = "u2" },
        };
        settings.Vless.ActiveServer = "manual-1";

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead");

        Assert.True(outcome.Switched);
        Assert.Equal("manual-2", outcome.NewActiveServer);
    }
}
