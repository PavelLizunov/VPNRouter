using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
            Vless = new VlessConfig()
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("104.194.156.93", resolved[0].Server);
        Assert.Equal(443, resolved[0].Port);
        Assert.Equal("xtls-rprx-vision", resolved[0].Flow);

        Assert.Equal(2, settings.Vless.Servers.Count);
        Assert.Equal("104.194.156.93:443", settings.Vless.ActiveServer);
    }

    [Fact]
    public void SubscribeMode_StaleVlessActiveServer_HonoursSubscriptionSelection_NotScopedZero()
    {
        var vless = new VlessServerEntry
        {
            Name = "Germany VLESS ~main-brat", Protocol = "vless",
            Server = "104.194.156.93", Port = 443, Uuid = "u1", Flow = "xtls-rprx-vision",
        };
        var awg = new VlessServerEntry
        {
            Name = "Germany AWG ~main-brat", Protocol = "amneziawg",
            Server = "104.194.156.93", Port = 51820,
        };
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "Germany AWG ~main-brat",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "sub", Url = "https://example.com/x", Enabled = true,
                        Servers = new List<VlessServerEntry> { vless, awg },
                    }
                }
            },
            Vless = new VlessConfig { ActiveServer = "main-brat" }
        };

        VlessServersResolver.Resolve(settings);

        Assert.Equal("Germany AWG ~main-brat", settings.Vless.ActiveServer);
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
                        Enabled = false,
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
                Subscriptions = new List<SubscriptionEntry>()
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry> { MakeServer("manual.example.com") }
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

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
