using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// AM-1 (2026-05-11) — pin the Exclude mode branch of
/// <see cref="ConfigGenerator.Generate"/>. Exclude mode =
/// inverted-split-tunnel: listed apps stay on the DIRECT route,
/// everything else routes through the proxy.
///
/// <para>This is the new alternative to legacy split tunnel — useful
/// for users who want VPN-by-default with a handful of exceptions
/// (RU bank, Steam, vendor-specific client). See
/// <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §2.</para>
/// </summary>
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
                        // v2.40.0-r9 (#5/#7): use a valid 32-byte base64url pbk + hex sid
                        // (LeakProtection now fails closed on an unusable Reality public_key).
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

        // The whole point of exclude mode: final = proxy, listed
        // processes = direct.
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
        // When an app is explicitly kept off the tunnel, its DNS
        // lookups must use local-dns — otherwise we'd be tunneling the
        // resolver traffic for an app the user wanted bypassed.
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        // DNS.Final flips to vpn-dns in exclude mode (matches route.final = proxy).
        Assert.Equal("vpn-dns", config.Dns.Final);

        var dnsRules = config.Dns.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Contains("Steam.exe"))
            .ToList();
        Assert.NotEmpty(dnsRules);
        Assert.Contains(dnsRules, r => r.Server == "local-dns");
    }

    [Fact]
    public void ExcludeMode_EmptyExcludeList_StillRoutesEverythingThroughProxy()
    {
        // Even with no exclusions selected, exclude mode still tunnels
        // everything by intent — route.final = proxy.
        var settings = BuildSettings(mode: "exclude");

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("proxy", config.Route.Final);
        // No process_name rules.
        var procRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .ToList();
        Assert.Empty(procRules);
    }

    [Fact]
    public void ExcludeMode_IgnoresRoutingAppsIncludeList()
    {
        // Defensive: if a user has BOTH lists populated (the segmented
        // toggle should prevent this in the UI, but yaml hand-edits
        // happen) the engine respects the active mode and ignores the
        // other list completely. This avoids accidental leak surface
        // where an include entry accidentally enters the picture.
        var settings = BuildSettings(
            mode: "exclude",
            include: new List<string> { "Discord.exe" },
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        var procNames = config.Route.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .Distinct()
            .ToList();
        Assert.Contains("Steam.exe", procNames);
        Assert.DoesNotContain("Discord.exe", procNames);
    }

    [Fact]
    public void ExcludeMode_FullTunnel_IgnoresPerAppList_KeepsFinalProxy()
    {
        // Full tunnel always wins — per-app filtering is meaningless
        // when everything is tunnelled regardless. Exclude list has no
        // effect; final stays proxy because that's full tunnel
        // semantics.
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });
        settings.App.RoutingMode = "full";

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        Assert.Equal("proxy", config.Route.Final);
        // No process-specific rules generated; the per-app list is
        // ignored when routing_mode = full.
        var procRules = config.Route.Rules
            .Where(r => r.ProcessName != null && r.ProcessName.Count > 0)
            .ToList();
        Assert.Empty(procRules);
    }

    [Fact]
    public void ExcludeMode_DropsWildcardAndQuestionMarkEntries()
    {
        // sing-box process_name doesn't support glob/regex — entries
        // with * or ? must be silently filtered out (same as include
        // mode and resolvedProcessNames). Otherwise sing-box rejects
        // the config at startup.
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
    public void ExcludeMode_DeduplicatesCaseInsensitivelyButPreservesCasing()
    {
        // sing-box process_name matching is case-sensitive on Windows
        // (filepath.Base lookup against QueryFullProcessImageName). We
        // de-dupe via OrdinalIgnoreCase but never mutate the input
        // casing — pin this contract.
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe", "steam.exe", "STEAM.EXE" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        var procNames = config.Route.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .ToList();
        Assert.Single(procNames);
        Assert.Equal("Steam.exe", procNames[0]);
    }

    [Fact]
    public void ExcludeMode_HasStandardSniffHijackPrivatePrefix()
    {
        // Verify exclude mode keeps the always-on sniff / hijack-dns /
        // private-ip rules at the top of the rule list — these are
        // independent of the per-app routing direction and must
        // survive both modes.
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
        // Defensive: the new exclude branch must not regress
        // LeakProtection. In particular, the validator's "DNS may leak"
        // warning is wired off (processesInRouteRules ∩ processesInDnsRules
        // must be complete); since exclude mode adds a local-dns rule
        // for the same processes, this assertion holds.
        var settings = BuildSettings(
            mode: "exclude",
            exclude: new List<string> { "Steam.exe" });

        var config = ConfigGenerator.Generate(EmptyProfile(),
            resolvedProcessNames: System.Array.Empty<string>(), settings);

        var validation = LeakProtection.ValidateConfig(config, settings);

        Assert.Empty(validation.Errors);
        // No DNS-leak warnings for excluded processes.
        Assert.DoesNotContain(validation.Warnings, w =>
            w.Contains("Steam.exe") && w.Contains("DNS may leak"));
    }
}
