using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// ConfigGenerator
// ═══════════════════════════════════════════════════════════════════════════════

public class ConfigGeneratorTests
{
    private static AppSettings CreateSettings(int serverCount = 1)
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig()
        };

        if (serverCount == 1)
        {
            settings.Vless.Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main",
                    Server = "1.2.3.4",
                    Port = 443,
                    Uuid = "test-uuid",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = "testkey",
                        ShortId = "abcd"
                    }
                }
            };
        }
        else
        {
            settings.Vless.Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main",
                    Server = "1.2.3.4",
                    Port = 443,
                    Uuid = "uuid-1",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "key1", ShortId = "aa" }
                },
                new()
                {
                    Name = "backup",
                    Server = "5.6.7.8",
                    Port = 443,
                    Uuid = "uuid-2",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "key2", ShortId = "bb" }
                }
            };
        }

        return settings;
    }

    private static Profile CreateProfile(string dnsMode = "vpn_only")
    {
        return new Profile
        {
            Name = "TestProfile",
            DnsMode = dnsMode,
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } },
                new() { Name = "firefox.exe", ScanPatterns = new[] { "firefox.exe" } }
            }
        };
    }

    [Fact]
    public void SingleServer_ProxyOutboundIsVless()
    {
        var settings = CreateSettings(serverCount: 1);
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        Assert.Equal("1.2.3.4", proxy.Server);
    }

    /// <summary>
    /// Regression pin for the v2.30.1 ordering bug.
    ///
    /// Bug: when both <c>BypassRussianTraffic</c> AND <c>BlockAds</c> were
    /// enabled, <c>ApplyAdBlock</c> used <c>route.rules.Insert(0, ...)</c>
    /// which placed the adblock rule AHEAD OF SNIFF. The subsequent
    /// <c>ApplyGeoBypass</c> used a sniff/hijack/private-prefix scan to
    /// pick its insertion slot — but that scan stopped at the adblock
    /// rule and returned 0, so the geo-bypass rule was inserted at the
    /// very top, also ahead of sniff.
    ///
    /// Result: the rules list looked like
    ///   [BypassRu, AdBlock, sniff, hijack-dns, private, ..., final=proxy]
    ///
    /// In sing-box, rule_set matching against <c>geosite-ru</c> requires
    /// a destination domain. Without sniff having run, the destination
    /// is just an IP — so the BypassRu rule never matched, and all
    /// Russian-domain traffic fell through to <c>final=proxy</c>.
    ///
    /// User-visible symptom (2026-04-30): "Full Tunnel + RU bypass
    /// enabled, but 2ip.ru / Avito show non-Russian IP." The fix forces
    /// both ApplyAdBlock and ApplyGeoBypass to insert AFTER the
    /// sniff/hijack-dns/private prefix, so the rule order becomes
    ///   [sniff, hijack-dns, private, BypassRu, AdBlock, ..., final=proxy]
    /// and BypassRu has a sniffed domain to match against.
    /// </summary>
    [Fact]
    public void FullTunnel_BypassRuAndBlockAds_PreservesSniffPrefix()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";
        settings.App.BypassRussianTraffic = true;
        settings.App.BlockAds = true;

        // Skip if geo files aren't on this CI host — BypassRu silently
        // disables itself in that case (the gate is intentional, not a
        // bug). Localhost dev VMs typically have them.
        if (!GeoDataDownloader.AreGeoFilesAvailable())
        {
            return;
        }

        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var rules = config.Route.Rules;
        Assert.True(rules.Count >= 5, $"expected ≥5 rules, got {rules.Count}");
        Assert.Equal("sniff", rules[0].Action);
        Assert.Equal("hijack-dns", rules[1].Action);
        Assert.True(rules[2].IpIsPrivate, "rule[2] should be the private-ip → direct rule");

        // BypassRu and AdBlock must come AFTER the sniff/hijack/private
        // prefix — search the tail and assert they exist there.
        var tail = rules.Skip(3).ToList();
        Assert.Contains(tail, r =>
            r.RuleSet != null
            && r.RuleSet.Contains("vpnrouter-geosite-ru")
            && r.Action == "route"
            && r.Outbound == "direct");
        Assert.Contains(tail, r =>
            r.RuleSet != null
            && r.RuleSet.Contains("vpnrouter-adblock")
            && r.Action == "reject");
        Assert.Equal("proxy", config.Route.Final);
    }

    // v2.44.3: rewritten from the pre-subscription multi-server urltest tests.
    // ConfigGenerator now emits a SINGLE ActiveServer outbound by default; the
    // urltest fan-out is opt-in via Vless.AutoSelectBestServer ("Авто-выбор
    // лучшего сервера"). These pin the current contract both ways + the exact
    // urltest shape that lets sing-box auto-select / re-select the fastest node.

    [Fact]
    public void AutoSelect_On_MultiSameProtocol_ProxyIsUrltestWithShape()
    {
        var settings = CreateSettings(serverCount: 2);
        settings.Vless.AutoSelectBestServer = true;
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("urltest", proxy.Type);
        Assert.NotNull(proxy.Outbounds);
        Assert.Equal(2, proxy.Outbounds!.Count);
        Assert.Contains("vless-main", proxy.Outbounds);
        Assert.Contains("vless-backup", proxy.Outbounds);
        // The shape that lets sing-box pick the fastest reachable node AND
        // re-select when the active one dies — pinned so a future change can't
        // silently break auto-select / failover-by-urltest.
        Assert.Equal("http://www.gstatic.com/generate_204", proxy.Url);
        Assert.Equal("3m", proxy.Interval);
        Assert.Equal(150, proxy.Tolerance);
        Assert.False(proxy.InterruptExistConnections);
    }

    [Fact]
    public void AutoSelect_On_ChildVlessOutboundsExist()
    {
        var settings = CreateSettings(serverCount: 2);
        settings.Vless.AutoSelectBestServer = true;
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var main = config.Outbounds.First(o => o.Tag == "vless-main");
        Assert.Equal("vless", main.Type);
        Assert.Equal("1.2.3.4", main.Server);
        Assert.Equal("uuid-1", main.Uuid);

        var backup = config.Outbounds.First(o => o.Tag == "vless-backup");
        Assert.Equal("vless", backup.Type);
        Assert.Equal("5.6.7.8", backup.Server);
        Assert.Equal("uuid-2", backup.Uuid);
    }

    [Fact]
    public void AutoSelect_Off_MultiServer_ProxyIsSingleActive_NotUrltest()
    {
        // Default (opt-out): with 2 servers configured the generator routes through
        // the single ActiveServer (here the first, "main") — NOT a urltest fan-out.
        // This is the contract that retired the old always-urltest multi-server path.
        var settings = CreateSettings(serverCount: 2);
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);            // single outbound, not urltest
        Assert.Equal("1.2.3.4", proxy.Server);        // the active server "main"
        Assert.DoesNotContain(config.Outbounds, o => o.Type == "urltest");
    }

    [Fact]
    public void DnsFinal_IsLocalDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Equal("local-dns", config.Dns.Final);
    }

    [Fact]
    public void DnsRule_VpnOnly_UsesVpnDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "vpn_only");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var dnsRule = config.Dns.Rules.FirstOrDefault(r => r.ProcessName != null);
        Assert.NotNull(dnsRule);
        Assert.Equal("vpn-dns", dnsRule.Server);
    }

    [Fact]
    public void DnsRule_SmartMode_UsesLocalDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "smart");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var dnsRule = config.Dns.Rules.FirstOrDefault(r => r.ProcessName != null);
        Assert.NotNull(dnsRule);
        Assert.Equal("local-dns", dnsRule.Server);
    }

    [Fact]
    public void DnsRule_DirectMode_RoutedAppGetsVpnDnsRule()
    {
        // v2.40.0-r9 (#1 core-audit fix): a routed app in dns_mode=direct previously
        // got NO per-process DNS rule, so its DNS fell through to dns.final=local-dns
        // (real NIC) = a DNS leak for exactly the app the user routed for privacy
        // (reachable via the shipped Privacy_Shell profile). A routed app now ALWAYS
        // gets a per-process DNS rule; only smart mode uses local DoH, everything else
        // (incl. direct) tunnels DNS via vpn-dns.
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "direct");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var procRule = config.Dns.Rules
            .FirstOrDefault(r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe"));
        Assert.NotNull(procRule);
        Assert.Equal("vpn-dns", procRule!.Server);
    }

    [Fact]
    public void ProcessNames_PreservesCase()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "Firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var routeRule = config.Route.Rules.First(r => r.ProcessName != null);
        Assert.Contains("Discord.exe", routeRule.ProcessName!);
        Assert.Contains("Firefox.exe", routeRule.ProcessName!);
    }

    [Fact]
    public void ProcessNames_FiltersWildcards()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "chrome*", "fire?.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var routeRule = config.Route.Rules.First(r => r.ProcessName != null);
        Assert.Contains("Discord.exe", routeRule.ProcessName!);
        Assert.DoesNotContain("chrome*", routeRule.ProcessName!);
        Assert.DoesNotContain("fire?.exe", routeRule.ProcessName!);
    }

    [Fact]
    public void ProcessNames_DeduplicatesCaseInsensitive()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "discord.exe", "DISCORD.EXE" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var routeRule = config.Route.Rules.First(r => r.ProcessName != null);
        Assert.Single(routeRule.ProcessName!);
    }

    [Fact]
    public void RouteRules_SniffRuleIsFirst()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var firstRule = config.Route.Rules[0];
        Assert.Equal("sniff", firstRule.Action);
        Assert.Equal("300ms", firstRule.Timeout);
    }

    [Fact]
    public void RouteRules_HijackDnsIsSecond()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var secondRule = config.Route.Rules[1];
        Assert.Equal("hijack-dns", secondRule.Action);
        Assert.Equal("dns", secondRule.Protocol);
    }

    [Fact]
    public void InboundTun_NoSniffFields()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        // Verify the JSON doesn't contain deprecated sniff fields on inbound
        var json = ConfigGenerator.Serialize(config);
        Assert.DoesNotContain("sniff_override_destination", json);
        // sniff should only appear in route rules (action: "sniff"), not on inbound
        Assert.DoesNotContain("\"sniff\": true", json);
        Assert.DoesNotContain("\"sniff\": false", json);
    }

    [Fact]
    public void DirectOutbound_AlwaysPresent()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Contains(config.Outbounds, o => o.Tag == "direct" && o.Type == "direct");
    }

    [Fact]
    public void Route_DefaultDomainResolver_IsLocalDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Equal("local-dns", config.Route.DefaultDomainResolver);
    }

    [Fact]
    public void Route_FinalIsDirect()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Equal("direct", config.Route.Final);
    }
}
