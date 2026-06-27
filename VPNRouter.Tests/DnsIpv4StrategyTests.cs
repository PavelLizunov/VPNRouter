using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// G5 (2026-06-27): when the TUN carries no IPv6 (<see cref="TunSettings.Ipv6Enabled"/>
/// = false), the generated <c>dns.strategy</c> must be "ipv4_only" so sing-box
/// never returns AAAA records that can't traverse the IPv4-only tunnel — the
/// "address not valid in its context" dial-fails + per-query stall seen in user
/// diags. This holds independent of the legacy ForceIpv4Only toggle.
/// </summary>
public class DnsIpv4StrategyTests
{
    private static AppSettings Make(bool ipv6Tun, bool forceIpv4)
        => new()
        {
            App = new AppConfig { LogLevel = "info", RoutingMode = "split", ForceIpv4Only = forceIpv4 },
            Tun = new TunSettings { Ipv6Enabled = ipv6Tun },
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "m", Server = "1.2.3.4", Port = 443, Uuid = "u",
                        Flow = "xtls-rprx-vision", Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" }
                    }
                }
            }
        };

    private static SingBoxConfig Gen(AppSettings s)
        => ConfigGenerator.Generate(
            new Profile { Name = "P", Processes = new List<ProcessRule>() },
            Array.Empty<string>(), s);

    [Fact]
    public void Ipv6DisabledTun_ForcesIpv4Only_EvenWhenForceIpv4OnlyOff()
    {
        var cfg = Gen(Make(ipv6Tun: false, forceIpv4: false));
        Assert.Equal("ipv4_only", cfg.Dns.Strategy);
    }

    [Fact]
    public void Ipv6EnabledTun_AndForceOff_NoStrategyOverride()
    {
        var cfg = Gen(Make(ipv6Tun: true, forceIpv4: false));
        Assert.Null(cfg.Dns.Strategy);
    }

    [Fact]
    public void ForceIpv4Only_AlwaysIpv4Only_RegardlessOfTun()
    {
        var cfg = Gen(Make(ipv6Tun: true, forceIpv4: true));
        Assert.Equal("ipv4_only", cfg.Dns.Strategy);
    }
}
