using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class LeakProtectionScopeAwareTests
{
    private static SingBoxConfig CreateValidConfig(
        string proxyServer = "1.2.3.4",
        int proxyPort = 443,
        string proxyUuid = "test-uuid")
    {
        return new SingBoxConfig
        {
            Dns = new SingBoxDns
            {
                Strategy = "ipv4_only",
                Final = "local-dns",
                Servers = new List<DnsServer>
                {
                    new() { Tag = "vpn-dns", Type = "https", Server = "1.1.1.1", Detour = "proxy" },
                    new() { Tag = "local-dns", Type = "local" }
                },
                Rules = new List<DnsRule>
                {
                    new() { ProcessName = new List<string> { "Discord.exe" }, Action = "route", Server = "vpn-dns" }
                }
            },
            Inbounds = new List<SingBoxInbound>
            {
                new()
                {
                    Type = "tun",
                    Tag = "tun-in",
                    StrictRoute = false,
                    Address = new List<string> { "172.19.0.1/30" }
                }
            },
            Outbounds = new List<SingBoxOutbound>
            {
                new()
                {
                    Type = "vless",
                    Tag = "proxy",
                    Server = proxyServer,
                    ServerPort = proxyPort,
                    Uuid = proxyUuid,
                },
                new() { Type = "direct", Tag = "direct" }
            },
            Route = new SingBoxRoute
            {
                Rules = new List<RouteRule>
                {
                    new() { Action = "sniff", Timeout = "300ms" },
                    new() { Protocol = "dns", Action = "hijack-dns" },
                    new()
                    {
                        ProcessName = new List<string> { "Discord.exe" },
                        Action = "route",
                        Outbound = "proxy"
                    }
                },
                Final = "direct"
            }
        };
    }

    private static AppSettings BuildStasLikeSettings()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";

        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "simple",
            Url = "https://example.com/api/v1/app/config/abc",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "de-01 443 Khunrath",
                    Server = "104.194.156.93",
                    Port = 443,
                    Uuid = "9029d44f-232f-4283-b055-d39f8448f43b",
                },
                new()
                {
                    Name = "is-01 443 Khunrath",
                    Server = "93.95.226.167",
                    Port = 443,
                    Uuid = "b9c26f53-d1bf-4f8e-8aa4-68684aa0e0f0",
                },
            }
        });

        settings.Vless.Server = "195.135.255.216";
        settings.Vless.Port = 443;
        settings.Vless.Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "khunrath_ln",
                Server = "195.135.255.216",
                Port = 443,
                Uuid = "352714f4-7ecc-4c22-805f-ed5c5239f5bb",
            },
        };
        settings.Vless.ActiveServer = "khunrath_ln";

        return settings;
    }

    [Fact]
    public void GeneratedMode_WithSubscription_LegacyVlessServerOutbound_FailsValidation()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "195.135.255.216",
            proxyPort: 443,
            proxyUuid: "352714f4-7ecc-4c22-805f-ed5c5239f5bb");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("195.135.255.216")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_ValidOutbound_Passes()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("scope") || e.Contains("legacy vless.servers") ||
            e.Contains("subscription"));
    }

    [Fact]
    public void GeneratedMode_NoSubscriptions_VlessServerOutbound_Passes()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "main",
                Server = "1.2.3.4",
                Port = 443,
                Uuid = "test-uuid",
            },
        };

        var config = CreateValidConfig(
            proxyServer: "1.2.3.4",
            proxyPort: 443,
            proxyUuid: "test-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
    }

    [Fact]
    public void CustomMode_WellFormedProxyOutbound_Passes()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        var config = CreateValidConfig(
            proxyServer: "203.0.113.42",
            proxyPort: 443,
            proxyUuid: "custom-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Errors, e => e.Contains("scope")
            || e.Contains("legacy") || e.Contains("config_mode=custom"));
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
    }

    [Fact]
    public void CustomMode_MissingProxyOutbound_Fails()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "direct", Tag = "direct" },
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("config_mode=custom") && e.Contains("proxy"));
    }

    [Fact]
    public void CustomMode_NullOutbounds_FailsClosed()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";
        var config = CreateValidConfig();
        config.Outbounds = null!;

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("config_mode=custom") && e.Contains("proxy"));
    }

    [Fact]
    public void CustomMode_EmptyProxyServer_Fails()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "custom";

        var config = CreateValidConfig(proxyServer: "");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("config_mode=custom")
            && (e.Contains("server") || e.Contains("empty")));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_SameIpDifferentUuid_FailsValidation()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Server = "104.194.156.93",
                    Port = 443,
                    Uuid = "9029d44f-232f-4283-b055-d39f8448f43b",
                },
            }
        });

        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "00000000-0000-0000-0000-000000000000");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("104.194.156.93")
            && (e.Contains("scope") || e.Contains("legacy")));
    }

    [Fact]
    public void GeneratedMode_CachedSubscriptionServersAllowed()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.SubscriptionServers = new List<VlessServerEntry>
        {
            new()
            {
                Server = "10.20.30.40",
                Port = 443,
                Uuid = "cached-uuid",
            },
        };

        var config = CreateValidConfig(
            proxyServer: "10.20.30.40",
            proxyPort: 443,
            proxyUuid: "cached-uuid");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_NonPlaceholderManualVlessServer_Passes_BratRegression()
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
                    new() { Name = "de-01 main-brat", Server = "1.2.3.4", Port = 443, Uuid = "sub-uuid-1" },
                }
            }
        };
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new()
            {
                Name = "⚡ [US] 193.233.217.174:443",
                Server = "193.233.217.174",
                Port = 443,
                Uuid = "real-uuid-free-config",
                Reality = new VlessRealityConfig
                {
                    Enabled = true,
                    PublicKey = "real-pubkey-not-placeholder",
                    ShortId = "deadbeef"
                }
            }
        };
        settings.Vless.ActiveServer = "⚡ [US] 193.233.217.174:443";

        var config = CreateValidConfig(
            proxyServer: "193.233.217.174",
            proxyPort: 443,
            proxyUuid: "real-uuid-free-config");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, "Validation should pass for legitimate manual Free Config entry in generated mode. Errors: " + string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("193.233.217.174")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_DnsTunnelLoopbackOutbound_Passes()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main-brat",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Name = "lv-01 main-brat", Server = "213.155.15.93", Port = 443, Uuid = "sub-uuid-lv" },
            }
        });

        var config = CreateValidConfig(
            proxyServer: "127.0.0.1",
            proxyPort: 7001,
            proxyUuid: "sub-uuid-lv");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid,
            "DNS-tunnel loopback proxy outbound must pass scope validation. Errors: "
            + string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("127.0.0.1")
            && (e.Contains("scope") || e.Contains("legacy") || e.Contains("subscription")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.5.6.7")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void GeneratedMode_WithSubscription_LoopbackVariants_AllExempt(string loopback)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "1.2.3.4", Port = 443, Uuid = "sub-uuid" },
            }
        });

        var config = CreateValidConfig(proxyServer: loopback, proxyPort: 7001, proxyUuid: "any");

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("scope") || e.Contains("not in the active subscription"));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_AwgEndpointOutOfScope_FailsValidation()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        config.Endpoints = new List<SingBoxEndpoint>
        {
            new()
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Address = new List<string> { "10.66.0.2/32" },
                PrivateKey = "aPrivateKeyBase64==",
                Peers = new List<WireGuardPeer>
                {
                    new() { Address = "203.0.113.99", Port = 51820, PublicKey = "peerPubKey==" }
                }
            }
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("203.0.113.99") && e.Contains("AWG endpoint") && e.Contains("active subscription"));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_AwgEndpointInScope_PassesValidation()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig(
            proxyServer: "104.194.156.93",
            proxyPort: 443,
            proxyUuid: "9029d44f-232f-4283-b055-d39f8448f43b");

        config.Endpoints = new List<SingBoxEndpoint>
        {
            new()
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Address = new List<string> { "10.66.0.2/32" },
                PrivateKey = "aPrivateKeyBase64==",
                Peers = new List<WireGuardPeer>
                {
                    new() { Address = "104.194.156.93", Port = 443, PublicKey = "peerPubKey==" }
                }
            }
        };

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void GeneratedMode_NoSubscriptions_AwgEndpointOutOfScope_Warns()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "generated";
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Server = "104.194.156.93", Port = 443, Uuid = "test-uuid" },
        };
        var config = CreateValidConfig("104.194.156.93", 443, "test-uuid");
        config.Endpoints =
        [
            new SingBoxEndpoint
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Peers = [new WireGuardPeer { Address = "203.0.113.99", Port = 51820 }],
            },
        ];

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("AWG endpoint") && w.Contains("203.0.113.99"));
    }

    [Fact]
    public void GeneratedMode_WithSubscription_AwgLoopbackPeer_IsExempt()
    {
        var settings = BuildStasLikeSettings();
        var config = CreateValidConfig("104.194.156.93", 443, "9029d44f-232f-4283-b055-d39f8448f43b");
        config.Endpoints =
        [
            new SingBoxEndpoint
            {
                Type = "wireguard",
                Tag = "proxy-awg",
                Peers = [new WireGuardPeer { Address = "127.0.0.1", Port = 51820 }],
            },
        ];

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Errors, e => e.Contains("AWG endpoint"));
    }
}
