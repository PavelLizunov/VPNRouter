using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class ConfigGeneratorExcludeModeTests
{
    private static AppSettings BuildSettings(string mode = "exclude",
        List<string>? include = null, List<string>? exclude = null,
        bool blockAds = false, bool strictDns = false)
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = mode,
                RoutingAppsInclude = include ?? new List<string>(),
                RoutingAppsExclude = exclude ?? new List<string>(),
                BlockAds = blockAds,
                StrictDns = strictDns,
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
                        Reality = new VlessRealityConfig { PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", ShortId = "78ca7952" },
                    },
                },
            },
        };
    }

    private static Profile EmptyProfile() => new()
    {
        Name = "EmptyTestProfile",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>(),
    };

    [Fact]
    public void ExcludeMode_RoutesSelectedAppsToDirect_FinalIsProxy()
    {
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe", "bank-client.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("proxy", config.Route.Final);

        var procRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .ToList();
        Assert.NotEmpty(procRules);
        var combined = procRules.SelectMany(r => r.ProcessName!).Distinct().ToList();
        Assert.Contains("Steam.exe", combined);
        Assert.Contains("bank-client.exe", combined);
        Assert.All(procRules, r =>
        {
            Assert.Equal("route", r.Action);
            Assert.Equal("direct", r.Outbound);
        });
    }

    [Fact]
    public void ExcludeMode_DnsRulesRouteSelectedAppsToLocalResolver()
    {
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("vpn-dns", config.Dns.Final);

        var dnsRules = config.Dns.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Contains("Steam.exe"))
            .ToList();
        Assert.NotEmpty(dnsRules);
        Assert.Contains(dnsRules, r => r.Server == "local-dns");
    }

    [Fact]
    public void ExcludeMode_DropsWildcardAndQuestionMarkEntries()
    {
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe", "*.bin", "weird?app.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        var procNames = config.Route.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();
        Assert.Contains("Steam.exe", procNames);
        Assert.DoesNotContain("*.bin", procNames);
        Assert.DoesNotContain("weird?app.exe", procNames);
    }

    [Fact]
    public void ExcludeMode_HasStandardSniffHijackPrivatePrefix()
    {
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.True(config.Route.Rules.Count >= 4);
        Assert.Equal("sniff", config.Route.Rules[0].Action);
        Assert.Equal("hijack-dns", config.Route.Rules[1].Action);
        Assert.True(config.Route.Rules[2].IpIsPrivate);
    }

    [Fact]
    public void ExcludeMode_PassesLeakProtectionValidation()
    {
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        var validation = LeakProtection.ValidateConfig(config, settings);

        Assert.Empty(validation.Errors);
        Assert.DoesNotContain(validation.Warnings, w =>
            w.Contains("Steam.exe") && w.Contains("DNS may leak"));
    }
}
