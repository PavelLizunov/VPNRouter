using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class LeakProtectionTests
{
    private static SingBoxConfig CreateValidConfig()
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
                    Server = "1.2.3.4",
                    ServerPort = 443,
                    Uuid = "test-uuid"
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

    [Fact]
    public void ValidConfig_Passes()
    {
        var config = CreateValidConfig();
        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void InvalidDnsStrategy_Fails()
    {
        var config = CreateValidConfig();
        config.Dns.Strategy = "prefer_ipv4";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("strategy"));
    }

    [Fact]
    public void StrictRouteTrue_Fails()
    {
        var config = CreateValidConfig();
        config.Inbounds[0].StrictRoute = true;

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("strict_route"));
    }

    [Fact]
    public void MissingProxyOutbound_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("proxy"));
    }

    [Fact]
    public void MissingDirectOutbound_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "proxy", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("direct"));
    }

    [Fact]
    public void EmptyVlessServer_Fails()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("server is empty"));
    }

    [Fact]
    public void EmptyVlessUuid_Fails()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Uuid = "";

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("uuid is empty"));
    }

    [Fact]
    public void UrltestWithOneChild_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-0", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-0" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("at least 2"));
    }

    [Fact]
    public void UrltestWithValidChildren_Passes()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-main", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid-1" },
            new() { Type = "vless", Tag = "vless-backup", Server = "5.6.7.8", ServerPort = 443, Uuid = "uuid-2" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-main", "vless-backup" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void UrltestWithNonexistentChild_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-0", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-0", "vless-ghost" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("vless-ghost"));
    }

    [Fact]
    public void MissingDnsHijackRule_WarnsButPasses()
    {
        var config = CreateValidConfig();
        config.Route.Rules = config.Route.Rules
            .Where(r => r.Action != "hijack-dns")
            .ToList();

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("hijack-dns"));
    }

    [Fact]
    public void ProcessRoutedButNoDnsRule_WarnsAboutLeak()
    {
        var config = CreateValidConfig();
        config.Dns.Rules.Clear();

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("DNS may leak"));
    }

    [Fact]
    public void Hysteria2_ValidConfig_Passes()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "hysteria2",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Password = "secret"
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Hysteria2_EmptyPassword_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "hysteria2",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Password = ""
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("password is empty"));
    }

    [Fact]
    public void Tuic_EmptyUuid_Fails()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new()
            {
                Type = "tuic",
                Tag = "proxy",
                Server = "1.2.3.4",
                ServerPort = 443,
                Uuid = ""
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("TUIC", StringComparison.OrdinalIgnoreCase) && e.Contains("uuid"));
    }

    [Fact]
    public void MixedProtocolUrltest_VlessAndHysteria2_Passes()
    {
        var config = CreateValidConfig();
        config.Outbounds = new List<SingBoxOutbound>
        {
            new() { Type = "vless", Tag = "vless-1", Server = "1.2.3.4", ServerPort = 443, Uuid = "uuid-1" },
            new() { Type = "hysteria2", Tag = "hy2-1", Server = "5.6.7.8", ServerPort = 443, Password = "secret" },
            new()
            {
                Type = "urltest",
                Tag = "proxy",
                Outbounds = new List<string> { "vless-1", "hy2-1" }
            },
            new() { Type = "direct", Tag = "direct" }
        };

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void SmartMode_LocalDnsServer_DoesNotWarnAboutLeak()
    {
        var config = CreateValidConfig();
        config.Dns.Rules[0].Server = "local-dns";

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("DNS may leak"));
    }

    [Fact]
    public void FullTunnel_DnsFinalNotVpnDns_WarnsButPasses()
    {
        var config = CreateValidConfig();
        config.Route.Final = "proxy";
        config.Dns.Final = "local-dns";

        var result = LeakProtection.ValidateConfig(config);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w =>
            w.Contains("Full tunnel") || w.Contains("DNS may bypass"));
    }

    [Fact]
    public void ProxyUdp_AlsoValidated_FailsOnInvalidChild()
    {
        var config = CreateValidConfig();
        config.Outbounds.Add(new SingBoxOutbound
        {
            Type = "vless",
            Tag = "proxy-udp",
            Server = "1.2.3.4",
            ServerPort = 443,
            Uuid = ""
        });

        var result = LeakProtection.ValidateConfig(config);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("proxy-udp") && e.Contains("uuid"));
    }

    [Fact]
    public void OutboundIpNotInSubscriptions_EmitsError()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "195.135.255.216";

        var settings = new AppSettings();
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "104.194.156.93", Port = 443 },
                new() { Server = "194.87.222.111", Port = 443 },
            }
        });

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("195.135.255.216")
            && (e.Contains("subscription") || e.Contains("scope") || e.Contains("legacy")));
    }

    [Fact]
    public void OutboundIpInSubscriptions_NoWarning()
    {
        var config = CreateValidConfig();
        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        proxy.Server = "104.194.156.93";

        var settings = new AppSettings();
        settings.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "104.194.156.93", Port = 443 },
                new() { Server = "194.87.222.111", Port = 443 },
            }
        });

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("not in your VLESS server list"));
        Assert.DoesNotContain(result.Errors, e =>
            e.Contains("subscription scope") || e.Contains("legacy"));
    }
}
