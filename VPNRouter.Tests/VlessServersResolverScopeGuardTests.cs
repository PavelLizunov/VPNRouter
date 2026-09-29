using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class VlessServersResolverScopeGuardTests
{
    private const string StasPlaceholderServer = "195.135.255.216";
    private const string StasStaleTestEntry = "93.95.226.167";
    private const string StasPlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";

    private static VlessServerEntry MakeServer(string name, string host, int port = 443) =>
        new()
        {
            Name = name,
            Server = host,
            Port = port,
            Uuid = "uuid-" + host.GetHashCode().ToString("X"),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "pbk-" + host.GetHashCode().ToString("X"),
                ShortId = "abcd1234"
            }
        };

    private static AppSettings BuildStasEvidenceSettings(string activeServer = "khunrath_ln")
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                ActiveSubscriptionServer = "de-01 443 Khunrath",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "simple",
                        Url = "https://example.invalid/redacted-test-subscription",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("de-01 443 Khunrath", "104.194.156.93", 443),
                            MakeServer("is-01 443 Khunrath", "93.95.226.167", 443),
                            MakeServer("nk-01 8443 Khunrath", "194.87.222.111", 8443)
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Server = StasPlaceholderServer,
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                Flow = "xtls-rprx-vision",
                Security = "reality",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    ServerName = "yahoo.com",
                    Fingerprint = "firefox",
                    PublicKey = StasPlaceholderPubkey,
                    ShortId = "78ca7952"
                },
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "khunrath_ln",
                        Server = StasPlaceholderServer,
                        Port = 443,
                        Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "yahoo.com",
                            Fingerprint = "firefox",
                            PublicKey = StasPlaceholderPubkey,
                            ShortId = "78ca7952"
                        }
                    },
                    MakeServer("is-01-grpc-test", StasStaleTestEntry, 8444)
                },
                ActiveServer = activeServer
            }
        };
    }

    [Fact]
    public void GeneratedMode_WithEnabledSubscription_IgnoresLegacyVlessServers()
    {
        var settings = BuildStasEvidenceSettings();

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);

        Assert.DoesNotContain(resolved, s => s.Server == StasPlaceholderServer);
        Assert.DoesNotContain(resolved, s =>
            s.Name == "khunrath_ln" || s.Name == "is-01-grpc-test");

        Assert.DoesNotContain(resolved, s => s.Reality?.PublicKey == StasPlaceholderPubkey);

        Assert.Contains(resolved, s => s.Server == "104.194.156.93");
        Assert.Contains(resolved, s => s.Server == "93.95.226.167");
        Assert.Contains(resolved, s => s.Server == "194.87.222.111");

        Assert.Equal(3, settings.Vless.Servers.Count);
        Assert.DoesNotContain(settings.Vless.Servers, s => s.Name == "khunrath_ln");
    }

    [Fact]
    public void GeneratedMode_NoSubscriptions_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>()
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("A", "manual1.example.com"),
                    MakeServer("B", "manual2.example.com")
                },
                ActiveServer = "A"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("manual1.example.com", resolved[0].Server);
        Assert.Equal("manual2.example.com", resolved[1].Server);

        Assert.Equal("A", settings.Vless.ActiveServer);
    }

    [Fact]
    public void GeneratedMode_StaleActiveServer_FallsBackToFirstScoped()
    {
        var settings = BuildStasEvidenceSettings(activeServer: "khunrath_ln");

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);

        Assert.Equal("de-01 443 Khunrath", settings.Vless.ActiveServer);

        var activeServers = settings.Vless.GetActiveServers();
        Assert.NotEmpty(activeServers);
        Assert.Equal("104.194.156.93", activeServers[0].Server);
        Assert.NotEqual(StasPlaceholderServer, activeServers[0].Server);
    }

    [Fact]
    public void GeneratedMode_DisabledSubscription_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "disabled-sub",
                        Url = "https://example.com/sub",
                        Enabled = false,
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("sub-A", "sub.example.com")
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual", "manual.example.com")
                },
                ActiveServer = "manual"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
        Assert.DoesNotContain(resolved, s => s.Server == "sub.example.com");
    }

    [Fact]
    public void GeneratedMode_EnabledSubscriptionWithoutServers_FallsBackToVlessServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "fresh-not-refreshed",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>()
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual", "manual.example.com")
                },
                ActiveServer = "manual"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
    }

    [Fact]
    public void SubscribeMode_StaleActiveServer_FallsBackToFirstScoped()
    {
        var settings = BuildStasEvidenceSettings();
        settings.App.ConfigMode = "subscribe";
        settings.Vless.ActiveServer = "khunrath_ln";

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);
        Assert.Equal("de-01 443 Khunrath", settings.Vless.ActiveServer);
    }

    [Fact]
    public void GeneratedMode_ValidActiveServer_NotOverwritten()
    {
        var settings = BuildStasEvidenceSettings(activeServer: "nk-01 8443 Khunrath");

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);
        Assert.Equal("nk-01 8443 Khunrath", settings.Vless.ActiveServer);
    }

    [Fact]
    public void GeneratedMode_LegitimateManualChoice_RespectsUserSelection_BratRegression()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "generated",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "main-brat",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            MakeServer("de-01 443 main-brat", "1.2.3.4", 443),
                            MakeServer("is-01 443 main-brat", "5.6.7.8", 443),
                        }
                    }
                }
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "⚡ [US] 193.233.217.174:443",
                        Server = "193.233.217.174",
                        Port = 443,
                        Uuid = "f0e1d2c3-1234-5678-9abc-def012345678",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true,
                            ServerName = "www.cloudflare.com",
                            Fingerprint = "chrome",
                            PublicKey = "free-config-real-pubkey-not-placeholder",
                            ShortId = "deadbeef"
                        }
                    }
                },
                ActiveServer = "⚡ [US] 193.233.217.174:443"
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Single(resolved);
        Assert.Equal("193.233.217.174", resolved[0].Server);
        Assert.Equal("⚡ [US] 193.233.217.174:443", resolved[0].Name);

        Assert.Equal("⚡ [US] 193.233.217.174:443", settings.Vless.ActiveServer);

        Assert.Single(settings.Vless.Servers);
        Assert.Equal("193.233.217.174", settings.Vless.Servers[0].Server);
        Assert.DoesNotContain(settings.Vless.Servers, s => s.Server == "1.2.3.4");
    }

    [Fact]
    public void GeneratedMode_PlaceholderActiveEvenIfInVlessServers_FallsBackToSubscription()
    {
        var settings = BuildStasEvidenceSettings(activeServer: "khunrath_ln");

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(3, resolved.Count);
        Assert.DoesNotContain(resolved, s => s.Server == StasPlaceholderServer);
        Assert.NotEqual("khunrath_ln", settings.Vless.ActiveServer);
    }

    private static AppSettings SubscribeWith(string activeSubName, params (string Name, string Ip)[] servers)
        => new()
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = activeSubName,
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "sub",
                        Url = "https://example.com/sub",
                        Enabled = true,
                        Servers = servers.Select(s => MakeServer(s.Name, s.Ip, 443)).ToList(),
                    },
                },
            },
            Vless = new VlessConfig { ActiveServer = activeSubName, Servers = new List<VlessServerEntry>() },
        };

    [Fact]
    public void SubscribeMode_StaleActiveSubscriptionServer_CorrectedInBothSelectors()
    {
        var settings = SubscribeWith("old-server", ("new-server", "1.2.3.4"));

        VlessServersResolver.Resolve(settings);

        Assert.Equal("new-server", settings.Vless.ActiveServer);
        Assert.Equal("new-server", settings.App.ActiveSubscriptionServer);
    }

    [Fact]
    public void SubscribeMode_InScopeActiveSubscriptionServer_Unchanged()
    {
        var settings = SubscribeWith("new-server", ("new-server", "1.2.3.4"), ("other", "5.6.7.8"));

        VlessServersResolver.Resolve(settings);

        Assert.Equal("new-server", settings.Vless.ActiveServer);
        Assert.Equal("new-server", settings.App.ActiveSubscriptionServer);
    }
}
