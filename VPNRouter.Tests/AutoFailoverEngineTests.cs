using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class AutoFailoverEngineTests
{
    private readonly InMemorySettingsStore _store = new();

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

    [Fact]
    public async Task PicksNextSubscriptionServer()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"),
            ("srv-3", "1.2.3.3"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("test dead reason", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);
        Assert.Equal("srv-2", settings.Vless.ActiveServer);
        Assert.Equal("srv-2", settings.App.ActiveSubscriptionServer);
    }

    [Fact]
    public async Task SkipsPlaceholderServersInPool()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("placeholder", "195.135.255.216"),
            ("srv-3", "1.2.3.3"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("test", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-3", outcome.NewActiveServer);
    }

    [Fact]
    public async Task StopsAfter3Attempts()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-1",
            ("srv-1", "1.2.3.1"),
            ("srv-2", "1.2.3.2"),
            ("srv-3", "1.2.3.3"),
            ("srv-4", "1.2.3.4"),
            ("srv-5", "1.2.3.5"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var ct = TestContext.Current.CancellationToken;
        for (int i = 0; i < AutoFailoverEngine.MaxAttempts; i++)
        {
            var ok = await engine.HandleDeadConfigAsync($"dead-{i}", ct);
            Assert.True(ok.Switched, $"Attempt {i + 1} should have switched");
        }

        var fourth = await engine.HandleDeadConfigAsync("dead-4", ct);
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

        var outcome = await engine.HandleDeadConfigAsync("test", TestContext.Current.CancellationToken);

        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.NotNull(outcome.UserFacingMessage);
        Assert.Contains("Кастомный", outcome.UserFacingMessage!);
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

        var ct = TestContext.Current.CancellationToken;
        var first = await engine.HandleDeadConfigAsync("first dead", ct);
        Assert.Equal("srv-2", first.NewActiveServer);

        var second = await engine.HandleDeadConfigAsync("second dead", ct);
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

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("manual-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task ReturnsFalseWhenPoolEmpty()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "srv-only",
            ("srv-only", "1.2.3.4"));

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.False(outcome.Switched);
        Assert.NotNull(outcome.UserFacingMessage);
        Assert.Contains("провайдер", outcome.UserFacingMessage!);
        Assert.Contains("подписк", outcome.UserFacingMessage!);
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

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

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

        _ = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);
        Assert.NotEmpty(engine.TriedServers);

        engine.ResetCycle();
        Assert.Empty(engine.TriedServers);
    }

    [Fact]
    public async Task GeneratedMode_SubEnabled_LegitimateManual_SkipsAutoSwap_Brat()
    {
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

        var outcome = await engine.HandleDeadConfigAsync("Clash API HTTP 504", TestContext.Current.CancellationToken);

        Assert.False(outcome.Switched);
        Assert.Null(outcome.NewActiveServer);
        Assert.NotNull(outcome.UserFacingMessage);
        Assert.Equal(0, restartCalls);
        Assert.Equal("⚡ [EE] 77.239.126.152:7443", settings.Vless.ActiveServer);
    }

    [Fact]
    public async Task GeneratedMode_NoSubscription_LegitimateManual_StillSwapsAcrossManualPool()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions = new List<SubscriptionEntry>();
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "manual-1", Server = "10.0.0.1", Port = 443, Uuid = "u1" },
            new() { Name = "manual-2", Server = "10.0.0.2", Port = 443, Uuid = "u2" },
        };
        settings.Vless.ActiveServer = "manual-1";

        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(settings, sanity, store: _store);

        var outcome = await engine.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.True(outcome.Switched);
        Assert.Equal("manual-2", outcome.NewActiveServer);
    }

    [Fact]
    public async Task HandleDeadConfigAsync_FailedRestart_ExcludesCandidateFromNextAttempt()
    {
        var settings = BuildSubscribeSettings(
            activeServer: "s1",
            ("s1", "1.1.1.1"),
            ("s2", "2.2.2.2"),
            ("s3", "3.3.3.3"));

        int attemptCount = 0;
        var sanity = new ConfigSanityCheck();
        var engine = new AutoFailoverEngine(
            settings, sanity,
            restart: _ =>
            {
                attemptCount++;
                return Task.FromResult(attemptCount > 1);
            },
            store: _store);

        var outcome1 = await engine.HandleDeadConfigAsync("timeout", TestContext.Current.CancellationToken);
        Assert.False(outcome1.Switched);
        Assert.Equal("s1", settings.Vless.ActiveServer);

        var outcome2 = await engine.HandleDeadConfigAsync("timeout", TestContext.Current.CancellationToken);
        Assert.True(outcome2.Switched);
        Assert.Equal("s3", outcome2.NewActiveServer);
        Assert.Equal("s3", settings.Vless.ActiveServer);
    }
}
