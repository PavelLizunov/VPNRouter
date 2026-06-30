using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// T4 (2026-06-27), opt-in: in full-tunnel, resolve Roblox/game domains via the real-NIC
/// Cloudflare DoH (local-dns, encrypted -> not RU-poisoned) instead of the congested
/// proxy detour (vpn-dns), so a stalled proxy DoH doesn't hang game joins. Default OFF,
/// so it's an A/B lever with zero risk to existing users. See plans/roblox-277-rca-2026-06-27.md.
/// </summary>
public sealed class GameDnsOffProxyTests
{
    [Fact]
    public void FullTunnel_FlagOn_RoutesGameDnsToLocalDns()
    {
        var cfg = ConfigGenerator.Generate(Profile(), Array.Empty<string>(), Settings(on: true));
        var rule = cfg.Dns.Rules.FirstOrDefault(r =>
            r.Server == "local-dns" && r.DomainSuffix != null && r.DomainSuffix.Contains("roblox.com"));
        Assert.NotNull(rule);
        Assert.Contains("rbxcdn.com", rule!.DomainSuffix!);
        Assert.Contains("steamserver.net", rule.DomainSuffix!);
        Assert.Contains("steampowered.com", rule.DomainSuffix!);
        Assert.Contains("steamstatic.com", rule.DomainSuffix!);
        Assert.Contains("dota2.com", rule.DomainSuffix!);
    }

    [Fact]
    public void FullTunnel_FlagOff_NoGameDnsRule()
    {
        var cfg = ConfigGenerator.Generate(Profile(), Array.Empty<string>(), Settings(on: false));
        Assert.DoesNotContain(cfg.Dns.Rules,
            r => r.DomainSuffix != null && r.DomainSuffix.Contains("roblox.com"));
    }

    private static Profile Profile() => new() { Name = "t", DnsMode = "vpn_only" };

    private static AppSettings Settings(bool on) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full", ResolveGameDnsOffProxy = on },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "s",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "s", Protocol = "vless", Server = "h.example.com", Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111", Flow = "xtls-rprx-vision",
                    Security = "reality", Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" },
                },
            },
        },
    };
}
