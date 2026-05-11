using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// AM-1 (2026-05-11) — pin the Include mode branch of
/// <see cref="ConfigGenerator.Generate"/>. Include mode = the legacy
/// behaviour: listed apps are routed THROUGH the proxy, route.final =
/// direct. The plan section §2 calls this the default mode so this
/// suite guards against accidental flips and validates the override
/// path where the user sets <see cref="AppConfig.RoutingAppsInclude"/>
/// to a different list than the profile-resolved one.
/// </summary>
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

        // route.final = direct (split tunnel, include mode)
        Assert.Equal("direct", config.Route.Final);

        // There must be a process_name → proxy rule for the user list.
        var procRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .ToList();
        Assert.NotEmpty(procRules);
        var combined = procRules.SelectMany(r => r.ProcessName!).Distinct().ToList();
        Assert.Contains("Discord.exe", combined);
        Assert.Contains("chrome.exe", combined);
        Assert.All(procRules, r =>
        {
            Assert.Equal("route", r.Action);
            // Outbound must be a proxy variant (proxy or proxy-udp for
            // dual-flow servers; here we have a single flow so just
            // "proxy").
            Assert.True(r.Outbound == "proxy" || r.Outbound == "proxy-udp",
                $"Expected proxy outbound, got {r.Outbound}");
        });
    }

    [Fact]
    public void IncludeMode_EmptyRoutingAppsInclude_FallsBackToLegacyProfileList()
    {
        // Backward compat: when RoutingAppsInclude is empty we use the
        // resolvedProcessNames the caller passed in (the legacy path via
        // Profile.Processes + CustomApps + ExcludedApps).
        var settings = BuildSettings(mode: "include");
        var profile = BuildProfile();
        var resolved = new[] { "legacy-app.exe" };

        var config = ConfigGenerator.Generate(profile, resolved, settings);

        Assert.Equal("direct", config.Route.Final);
        var procNames = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();
        Assert.Contains("legacy-app.exe", procNames);
    }

    [Fact]
    public void IncludeMode_ExplicitRoutingAppsInclude_OverridesResolvedList()
    {
        // When the user has populated RoutingAppsInclude, that list
        // takes precedence over the legacy resolvedProcessNames. This
        // is the path the new Apps tab uses once AM-2 lands.
        var settings = BuildSettings(
            mode: "include",
            include: new List<string> { "new-app.exe" });
        var resolved = new[] { "legacy-app.exe" };

        var config = ConfigGenerator.Generate(BuildProfile(), resolved, settings);

        var procNames = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();
        Assert.Contains("new-app.exe", procNames);
        Assert.DoesNotContain("legacy-app.exe", procNames);
    }

    [Fact]
    public void IncludeMode_DnsRulesPointSelectedAppsToVpnDns()
    {
        // Per-process DNS leak protection in include + vpn_only profile.
        var settings = BuildSettings(
            mode: "include",
            include: new List<string> { "Discord.exe" });

        var config = ConfigGenerator.Generate(BuildProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        // Final DNS = local-dns (other processes use direct resolver
        // since route.final = direct).
        Assert.Equal("local-dns", config.Dns.Final);

        var dnsRules = config.Dns.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe"))
            .ToList();
        Assert.NotEmpty(dnsRules);
        Assert.Contains(dnsRules, r => r.Server == "vpn-dns");
    }
}
