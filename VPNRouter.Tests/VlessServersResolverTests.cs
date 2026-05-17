using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// VlessServersResolver — v2.28.2 regression
//
// Triggering bug: a v2.28.1 user had `config_mode: subscribe` with 6 servers
// in `app.subscriptions[0].servers` but `vless.servers: []` (subscription
// servers don't get persisted into Vless.Servers — they live in App.Subscriptions
// and get aggregated into Vless.Servers IN MEMORY only when VPN starts).
// MainWindowViewModel did this aggregation in the Connect handler, but
// VpnEngine.Apply (hot-reload path) did NOT — it called ConfigGenerator
// straight on the freshly-loaded settings with empty Vless.Servers, producing
// a sing-box JSON with route rules pointing at a "proxy" outbound that was
// never emitted. sing-box silently ignored the rules → traffic went direct,
// AND urltest probes still hit the upstream server with raw TCP (no VLESS
// handshake) → server log filled with 249 "flow mismatch" errors per day.
//
// These tests pin the new contract: VlessServersResolver.Resolve() is the
// single source of truth for server aggregation, and ConfigGenerator throws
// loudly if called with no servers (instead of silently producing broken JSON).
// ═══════════════════════════════════════════════════════════════════════════════

public class VlessServersResolverTests
{
    private static SubscriptionEntry MakeSub(string name, params VlessServerEntry[] servers) =>
        new()
        {
            Name = name,
            Url = $"https://example.com/sub/{name}",
            Enabled = true,
            Servers = servers.ToList()
        };

    private static VlessServerEntry MakeServer(string host, int port = 443) =>
        new()
        {
            Name = $"{host}:{port}",
            Server = host,
            Port = port,
            Uuid = "test-uuid-" + host.GetHashCode().ToString("X"),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "test-pbk-" + host.GetHashCode().ToString("X"),
                ShortId = "abcd1234"
            }
        };

    [Fact]
    public void SubscribeMode_AggregatesEnabledSubscriptionServers()
    {
        // Reproduces user's config.yaml: subscribe mode, Vless.Servers empty,
        // 6 servers in subscriptions[0].servers (here we use 2 for brevity).
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "104.194.156.93:443",
                Subscriptions = new List<SubscriptionEntry>
                {
                    MakeSub("simple",
                        MakeServer("104.194.156.93", 443),
                        MakeServer("104.194.156.93", 2083))
                }
            },
            Vless = new VlessConfig() // empty Servers + empty Server
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("104.194.156.93", resolved[0].Server);
        Assert.Equal(443, resolved[0].Port);
        Assert.Equal("xtls-rprx-vision", resolved[0].Flow);

        // Side-effect: settings.Vless.Servers populated for downstream consumers
        Assert.Equal(2, settings.Vless.Servers.Count);
        // ActiveServer carried from App.ActiveSubscriptionServer if Vless.ActiveServer was empty
        Assert.Equal("104.194.156.93:443", settings.Vless.ActiveServer);
    }

    [Fact]
    public void SubscribeMode_SkipsDisabledSubscriptions()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    MakeSub("active", MakeServer("1.1.1.1")),
                    new()
                    {
                        Name = "disabled",
                        Url = "https://example.com/x",
                        Enabled = false, // ← disabled
                        Servers = new List<VlessServerEntry> { MakeServer("2.2.2.2") }
                    }
                }
            },
            Vless = new VlessConfig()
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Single(resolved);
        Assert.Equal("1.1.1.1", resolved[0].Server);
    }

    [Fact]
    public void SubscribeMode_NoSubscriptions_FallsBackToManualVless()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>() // empty
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry> { MakeServer("manual.example.com") }
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        // Subscribe mode + no subs → fallback to Vless.Servers
        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
    }

    [Fact]
    public void GeneratedMode_UsesVlessServersDirectly()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual1.com"),
                    MakeServer("manual2.com")
                }
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("manual1.com", resolved[0].Server);
    }

    [Fact]
    public void EmptyEverything_ReturnsEmptyList()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "subscribe" },
            Vless = new VlessConfig()
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Empty(resolved);
    }

    [Fact]
    public void DescribeEmptyReason_NoSubscriptions()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "subscribe", Subscriptions = new() },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("no subscription URLs are configured", reason!);
    }

    [Fact]
    public void DescribeEmptyReason_AllSubscriptionsDisabled()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new() { Name = "x", Url = "https://x", Enabled = false }
                }
            },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("every subscription is disabled", reason!);
    }

    [Fact]
    public void DescribeEmptyReason_EnabledButNoServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new() { Name = "x", Url = "https://x", Enabled = true, Servers = new() }
                }
            },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("no subscription has fetched any servers yet", reason!);
    }
}
