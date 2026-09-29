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
}
