using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ConfigGeneratorIncludeModeTests
{
    private static AppSettings BuildSettings(string mode = "include",
        List<string>? include = null, List<string>? exclude = null)
    {
        var s = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = mode,
                RoutingAppsInclude = include ?? new List<string>(),
                RoutingAppsExclude = exclude ?? new List<string>(),
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "test",
                        Server = "1.2.3.4",
                        Port = 443,
                        Uuid = "abc",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "pk", ShortId = "sid" },
                    },
                },
            },
        };
        return s;
    }

    private static Profile BuildProfile() => new()
    {
        Name = "TestProfile",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>
        {
            new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } },
            new() { Name = "chrome.exe",  ScanPatterns = new[] { "chrome.exe" } },
        },
    };

    [Fact]
    public void IncludeMode_RoutesSelectedAppsToProxy_FinalIsDirect()
    {
        var settings = BuildSettings(
            mode: "include",
            include: new List<string> { "Discord.exe", "chrome.exe" });

        var config = ConfigGenerator.Generate(BuildProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("direct", config.Route.Final);

        var procRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0 && r.Action == "route")
            .ToList();
        Assert.NotEmpty(procRules);
        var combined = procRules.SelectMany(r => r.ProcessName!).Distinct().ToList();
        Assert.Contains("Discord.exe", combined);
        Assert.Contains("chrome.exe", combined);
        Assert.All(procRules, r =>
        {
            Assert.Equal("route", r.Action);
            Assert.True(r.Outbound == "proxy" || r.Outbound == "proxy-udp",
                $"Expected proxy outbound, got {r.Outbound}");
        });
    }

    [Fact]
    public void IncludeMode_DnsRulesPointSelectedAppsToVpnDns()
    {
        var settings = BuildSettings(
            mode: "include",
            include: new List<string> { "Discord.exe" });

        var config = ConfigGenerator.Generate(BuildProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("local-dns", config.Dns.Final);

        var dnsRules = config.Dns.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe"))
            .ToList();
        Assert.NotEmpty(dnsRules);
        Assert.Contains(dnsRules, r => r.Server == "vpn-dns");
    }
}
