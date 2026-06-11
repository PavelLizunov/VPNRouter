// v2.42.0 — ConfigGenerator strictDnsOverride threading.
//
// HealthMonitor's StrictDns failover suppresses "all DNS via tunnel" at runtime
// by regenerating with strictDnsOverride=false, which must flip dns.final from
// vpn-dns (DoH through the proxy) to local-dns (DoH on the real NIC). These pin
// that the override actually moves dns.final AND that full-tunnel / exclude mode
// stay on vpn-dns regardless (there StrictDns isn't the sole driver, so the
// failover must never disturb them).

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ConfigGeneratorStrictDnsOverrideTests
{
    private static AppSettings Settings(bool strictDns, string routingMode = "split", string appsMode = "include")
    {
        var s = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                StrictDns = strictDns,
                RoutingMode = routingMode,
                RoutingAppsMode = appsMode,
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig(),
        };
        s.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "main", Server = "1.2.3.4", Port = 443, Uuid = "test-uuid",
                    Security = "reality", Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" } }
        };
        return s;
    }

    private static Profile Profile() => new()
    {
        Name = "T",
        Processes = new List<ProcessRule> { new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
    };

    [Fact]
    public void StrictDnsOn_NoOverride_FinalIsVpnDns()
    {
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings(strictDns: true));
        Assert.Equal("vpn-dns", config.Dns.Final);
    }

    [Fact]
    public void StrictDnsOn_OverrideFalse_FinalFailsOverToLocalDns()
    {
        // The HealthMonitor failover path: StrictDns is on in settings but the
        // proxy is unreachable, so it regenerates with override=false → DNS must
        // resolve on the real NIC (local-dns) instead of hanging on the tunnel.
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings(strictDns: true),
            strictDnsOverride: false);
        Assert.Equal("local-dns", config.Dns.Final);
    }

    [Fact]
    public void StrictDnsOff_OverrideTrue_FinalIsVpnDns()
    {
        // Symmetric: override=true forces StrictDns on even when the setting is off.
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings(strictDns: false),
            strictDnsOverride: true);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }

    [Fact]
    public void FullTunnel_OverrideFalse_StaysVpnDns()
    {
        // Full tunnel routes ALL traffic + DNS through the tunnel by design;
        // StrictDns isn't the sole driver, so suppressing it must NOT flip
        // dns.final (HealthMonitor never fails these over — soleDriver=false).
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings(strictDns: true, routingMode: "full"), strictDnsOverride: false);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }

    [Fact]
    public void ExcludeMode_OverrideFalse_StaysVpnDns()
    {
        // Exclude (inverted split) mode also forces vpn-dns by default — unmatched
        // apps ride the tunnel — so the failover must leave dns.final alone.
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings(strictDns: true, appsMode: "exclude"), strictDnsOverride: false);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }
}
