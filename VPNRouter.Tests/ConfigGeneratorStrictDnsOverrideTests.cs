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
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings(strictDns: true),
            strictDnsOverride: false);
        Assert.Equal("local-dns", config.Dns.Final);
    }

    [Fact]
    public void StrictDnsOff_OverrideTrue_FinalIsVpnDns()
    {
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" }, Settings(strictDns: false),
            strictDnsOverride: true);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }

    [Fact]
    public void FullTunnel_OverrideFalse_StaysVpnDns()
    {
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings(strictDns: true, routingMode: "full"), strictDnsOverride: false);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }

    [Fact]
    public void ExcludeMode_OverrideFalse_StaysVpnDns()
    {
        var config = ConfigGenerator.Generate(Profile(), new[] { "Discord.exe" },
            Settings(strictDns: true, appsMode: "exclude"), strictDnsOverride: false);
        Assert.Equal("vpn-dns", config.Dns.Final);
    }
}
