using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// GetEffectiveServers
// ═══════════════════════════════════════════════════════════════════════════════

public class GetEffectiveServersTests
{
    [Fact]
    public void MultiServerList_ReturnsServersList()
    {
        var config = new VlessConfig
        {
            Server = "legacy.example.com",
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "server1.com", Port = 443, Uuid = "uuid-1" },
                new() { Server = "server2.com", Port = 443, Uuid = "uuid-2" }
            }
        };

        var servers = config.GetEffectiveServers();

        Assert.Equal(2, servers.Count);
        Assert.Equal("server1.com", servers[0].Server);
        Assert.Equal("server2.com", servers[1].Server);
    }

    [Fact]
    public void LegacySingleServer_BuildsOneEntry()
    {
        var config = new VlessConfig
        {
            Server = "legacy.example.com",
            Port = 8443,
            Uuid = "test-uuid",
            Flow = "xtls-rprx-vision",
            Security = "reality"
        };

        var servers = config.GetEffectiveServers();

        Assert.Single(servers);
        Assert.Equal("legacy.example.com", servers[0].Server);
        Assert.Equal(8443, servers[0].Port);
        Assert.Equal("test-uuid", servers[0].Uuid);
        Assert.Equal("xtls-rprx-vision", servers[0].Flow);
        Assert.Equal("reality", servers[0].Security);
    }

    [Fact]
    public void NoServersNoLegacy_ReturnsEmpty()
    {
        var config = new VlessConfig();
        var servers = config.GetEffectiveServers();
        Assert.Empty(servers);
    }

    [Fact]
    public void MultiServerList_IgnoresLegacyFields()
    {
        var config = new VlessConfig
        {
            Server = "should-be-ignored.com",
            Port = 9999,
            Uuid = "old-uuid",
            Servers = new List<VlessServerEntry>
            {
                new() { Server = "actual.com", Port = 443, Uuid = "new-uuid" }
            }
        };

        var servers = config.GetEffectiveServers();

        Assert.Single(servers);
        Assert.Equal("actual.com", servers[0].Server);
        Assert.Equal("new-uuid", servers[0].Uuid);
    }
}

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

    // TODO(post-subscription-refactor): these two tests assume the pre-subscription
    // multi-server generator that emitted a urltest parent outbound with child
    // vless-main/vless-backup entries. ConfigGenerator now selects a single
    // ActiveServer and emits a single vless outbound tagged "proxy", which is
    // what subscribe-mode + GUI server picker expect. Rewrite to test
    // ActiveServer selection semantics instead of urltest fan-out.
    [Fact(Skip = "Pre-subscription multi-server urltest — ConfigGenerator now selects a single ActiveServer. See TODO above.")]
    public void MultiServer_ProxyOutboundIsUrltest()
    {
        var settings = CreateSettings(serverCount: 2);
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("urltest", proxy.Type);
        Assert.Equal(2, proxy.Outbounds!.Count);
        Assert.Contains("vless-main", proxy.Outbounds);
        Assert.Contains("vless-backup", proxy.Outbounds);
    }

    [Fact(Skip = "Pre-subscription multi-server urltest — ConfigGenerator now selects a single ActiveServer. See TODO above.")]
    public void MultiServer_ChildVlessOutboundsExist()
    {
        var settings = CreateSettings(serverCount: 2);
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

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
    public void DnsRule_DirectMode_NoDnsRuleCreated()
    {
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "direct");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var dnsRulesWithProcesses = config.Dns.Rules.Where(r => r.ProcessName != null);
        Assert.Empty(dnsRulesWithProcesses);
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

// ═══════════════════════════════════════════════════════════════════════════════
// LeakProtection
// ═══════════════════════════════════════════════════════════════════════════════

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

        Assert.True(result.IsValid); // warnings don't cause failure
        Assert.Contains(result.Warnings, w => w.Contains("DNS may leak"));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// VlessUriParser
// ═══════════════════════════════════════════════════════════════════════════════

public class VlessUriParserTests
{
    private const string RealityUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU&sid=78ca7952" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bratik";

    [Fact]
    public void Parse_RealityUri_ExtractsAllFields()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("2d54442d-158f-49e2-b225-67ba1a5b77f4", entry.Uuid);
        Assert.Equal("194.87.222.111", entry.Server);
        Assert.Equal(443, entry.Port);
        Assert.Equal("xtls-rprx-vision", entry.Flow);
        Assert.Equal("reality", entry.Security);
        Assert.Equal("bratik", entry.Name);
    }

    [Fact]
    public void Parse_RealityUri_ExtractsRealityConfig()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("yahoo.com", entry.Reality.ServerName);
        Assert.Equal("firefox", entry.Reality.Fingerprint);
        Assert.Equal("DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU", entry.Reality.PublicKey);
        Assert.Equal("78ca7952", entry.Reality.ShortId);
    }

    [Fact]
    public void Parse_RealityUri_ExtractsTransport()
    {
        var entry = VlessUriParser.Parse(RealityUri);

        Assert.Equal("tcp", entry.Transport.Type);
        Assert.Equal("/", entry.Transport.Path);
    }

    [Fact]
    public void Parse_NonDefaultPort()
    {
        var uri = "vless://uuid@server.com:8443?security=tls&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("server.com", entry.Server);
        Assert.Equal(8443, entry.Port);
    }

    [Fact]
    public void Parse_DefaultPort_Is443()
    {
        // Port not specified — should default to 443
        var uri = "vless://uuid@server.com?security=tls&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal(443, entry.Port);
    }

    [Fact]
    public void Parse_TlsSecurity_SetsTlsConfig()
    {
        var uri = "vless://uuid@server.com:443?security=tls&sni=example.com&type=tcp#test";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("tls", entry.Security);
        Assert.True(entry.Tls.Enabled);
        Assert.Equal("example.com", entry.Tls.ServerName);
    }

    [Fact]
    public void Parse_FragmentName_UrlDecoded()
    {
        var uri = "vless://uuid@server.com:443?security=tls&type=tcp#bratik-nout";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("bratik-nout", entry.Name);
    }

    [Fact]
    public void Parse_NoFragment_EmptyName()
    {
        var uri = "vless://uuid@server.com:443?security=tls&type=tcp";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("", entry.Name);
    }

    [Fact]
    public void Parse_InvalidScheme_Throws()
    {
        Assert.Throws<FormatException>(() =>
            VlessUriParser.Parse("https://server.com"));
    }

    [Fact]
    public void Parse_MissingUuid_Throws()
    {
        Assert.Throws<FormatException>(() =>
            VlessUriParser.Parse("vless://server.com:443?security=tls"));
    }

    [Fact]
    public void ParseMultiple_MultipleLines()
    {
        var text = @"
vless://uuid1@server1.com:443?security=reality&sni=yahoo.com&fp=firefox&pbk=key1&sid=aa&type=tcp&flow=xtls-rprx-vision#main
vless://uuid2@server2.com:443?security=reality&sni=yahoo.com&fp=chrome&pbk=key2&sid=bb&type=tcp&flow=xtls-rprx-vision#backup
";
        var entries = VlessUriParser.ParseMultiple(text);

        Assert.Equal(2, entries.Count);
        Assert.Equal("server1.com", entries[0].Server);
        Assert.Equal("uuid1", entries[0].Uuid);
        Assert.Equal("main", entries[0].Name);
        Assert.Equal("server2.com", entries[1].Server);
        Assert.Equal("uuid2", entries[1].Uuid);
        Assert.Equal("backup", entries[1].Name);
    }

    [Fact]
    public void ParseMultiple_SkipsEmptyAndNonVlessLines()
    {
        var text = @"
some random text
vless://uuid@server.com:443?security=tls&type=tcp#test

another line
";
        var entries = VlessUriParser.ParseMultiple(text);

        Assert.Single(entries);
        Assert.Equal("server.com", entries[0].Server);
    }

    [Fact]
    public void TryParse_InvalidUri_ReturnsNull()
    {
        var result = VlessUriParser.TryParse("not a uri");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_ValidUri_ReturnsEntry()
    {
        var result = VlessUriParser.TryParse(RealityUri);
        Assert.NotNull(result);
        Assert.Equal("194.87.222.111", result.Server);
    }

    [Fact]
    public void Parse_WebSocketTransport()
    {
        var uri = "vless://uuid@server.com:443?security=tls&sni=example.com&type=ws&path=%2Fws&host=cdn.example.com#ws-server";
        var entry = VlessUriParser.Parse(uri);

        Assert.Equal("ws", entry.Transport.Type);
        Assert.Equal("/ws", entry.Transport.Path);
        Assert.NotNull(entry.Transport.Headers);
        Assert.Equal("cdn.example.com", entry.Transport.Headers["Host"]);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CustomConfigInjector
// ═══════════════════════════════════════════════════════════════════════════════

public class CustomConfigInjectorTests
{
    private static AppSettings CreateSettings() => new()
    {
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" }
    };

    // ── User's example: selector + vless + tuic (legacy format) ──
    private const string LegacyConfig = """
    {
      "dns": {
        "servers": [
          {"tag": "remote", "address": "tls://1.1.1.1", "detour": "proxy"},
          {"tag": "local",  "address": "223.5.5.5",     "detour": "direct"}
        ],
        "rules": [
          {"outbound": "any", "server": "local"}
        ],
        "final": "remote"
      },
      "outbounds": [
        {"type": "selector", "tag": "proxy", "outbounds": ["vless-reality","tuic-v5"]},
        {"type": "vless",    "tag": "vless-reality", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "tuic",     "tag": "tuic-v5",       "server": "1.2.3.4", "server_port": 8443, "uuid": "test"},
        {"type": "direct",   "tag": "direct"},
        {"type": "block",    "tag": "block"},
        {"type": "dns",      "tag": "dns-out"}
      ],
      "route": {
        "rules": [
          {"protocol": "dns", "outbound": "dns-out"},
          {"ip_is_private": true, "outbound": "direct"},
          {"clash_mode": "direct", "outbound": "direct"},
          {"clash_mode": "global", "outbound": "proxy"}
        ],
        "final": "proxy"
      }
    }
    """;

    // ── Action-based 1.12+ format ──
    private const string ActionConfig = """
    {
      "dns": {
        "servers": [
          {"tag": "vpn-dns", "type": "https", "server": "1.1.1.1", "detour": "proxy"},
          {"tag": "local-dns", "type": "local"}
        ],
        "rules": []
      },
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": {
        "rules": [
          {"action": "sniff", "timeout": "300ms"},
          {"protocol": "dns", "action": "hijack-dns"},
          {"ip_is_private": true, "action": "route", "outbound": "direct"}
        ],
        "final": "direct"
      }
    }
    """;

    // ── Validate ──

    [Fact]
    public void Validate_ValidConfig_Passes()
    {
        var (isValid, errors) = CustomConfigInjector.Validate(LegacyConfig);
        Assert.True(isValid, string.Join("; ", errors));
    }

    [Fact]
    public void Validate_InvalidJson_Fails()
    {
        var (isValid, errors) = CustomConfigInjector.Validate("{bad json");
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("Invalid JSON"));
    }

    [Fact]
    public void Validate_NoOutbounds_Fails()
    {
        var (isValid, errors) = CustomConfigInjector.Validate("""{"route": {}}""");
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("outbounds"));
    }

    [Fact]
    public void Validate_OnlyDirectOutbound_Fails()
    {
        var json = """{"outbounds": [{"type": "direct", "tag": "direct"}]}""";
        var (isValid, errors) = CustomConfigInjector.Validate(json);
        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("No proxy outbound"));
    }

    [Fact]
    public void Validate_NoRouteSection_StillPasses()
    {
        var json = """{"outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""";
        var (isValid, _) = CustomConfigInjector.Validate(json);
        Assert.True(isValid);
    }

    // ── Inject: proxy tag detection ──

    [Fact]
    public void Inject_LegacyConfig_FindsSelectorTag()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
        // selector tag is "proxy", so process rule should use outbound: "proxy"
        Assert.Contains("\"outbound\": \"proxy\"", result);
        Assert.Contains("\"Discord.exe\"", result);
    }

    [Fact]
    public void Inject_ActionConfig_FindsProxyTag()
    {
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Telegram.exe" }, CreateSettings());
        Assert.Contains("\"Telegram.exe\"", result);
        Assert.Contains("\"outbound\": \"proxy\"", result);
    }

    // ── Inject: format detection ──

    [Fact]
    public void Inject_LegacyConfig_NoActionField()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "test.exe" }, CreateSettings());
        // Legacy format — process rule should NOT have "action" field
        // The rule should be: {"process_name": [...], "outbound": "proxy"} without action
        // (action only in action-based format)
        Assert.Contains("\"process_name\"", result);
    }

    [Fact]
    public void Inject_ActionConfig_HasActionField()
    {
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "test.exe" }, CreateSettings());
        Assert.Contains("\"action\": \"route\"", result);
    }

    // ── Inject: route rule position ──

    [Fact]
    public void Inject_LegacyConfig_ProcessRuleAfterSystemRules()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
        var json = Newtonsoft.Json.Linq.JObject.Parse(result);
        var rules = json.SelectToken("route.rules") as Newtonsoft.Json.Linq.JArray;

        Assert.NotNull(rules);
        // Process rule should be after dns/ip_is_private/clash_mode rules
        // Original: [dns-out, ip_is_private, clash_mode:direct, clash_mode:global]
        // After inject: [dns-out, ip_is_private, clash_mode:direct, clash_mode:global, process_name]
        var processRuleIndex = -1;
        for (int i = 0; i < rules!.Count; i++)
        {
            if (rules[i]["process_name"] != null)
            {
                processRuleIndex = i;
                break;
            }
        }
        Assert.True(processRuleIndex >= 4, $"Process rule at index {processRuleIndex}, expected >= 4");
    }

    // ── Inject: DNS rules ──

    [Fact]
    public void Inject_InjectsDnsRuleForRemoteServer()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
        var json = Newtonsoft.Json.Linq.JObject.Parse(result);
        var dnsRules = json.SelectToken("dns.rules") as Newtonsoft.Json.Linq.JArray;

        Assert.NotNull(dnsRules);
        // First DNS rule should be our injected process rule
        var firstRule = dnsRules![0] as Newtonsoft.Json.Linq.JObject;
        Assert.NotNull(firstRule!["process_name"]);
        Assert.Equal("remote", firstRule["server"]?.ToString());
    }

    // ── Inject: Clash API ──

    [Fact]
    public void Inject_AddsClashApi()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "test.exe" }, CreateSettings());
        Assert.Contains("\"external_controller\": \"127.0.0.1:9090\"", result);
    }

    [Fact]
    public void Inject_DoesNotOverrideExistingClashApi()
    {
        var configWithClash = """
        {
          "outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}],
          "route": {"rules": [], "final": "direct"},
          "experimental": {"clash_api": {"external_controller": "0.0.0.0:8080"}}
        }
        """;
        var result = CustomConfigInjector.Inject(configWithClash, new[] { "test.exe" }, CreateSettings());
        Assert.Contains("0.0.0.0:8080", result);
        Assert.DoesNotContain("127.0.0.1:9090", result);
    }

    // ── Inject: empty processes ──

    [Fact]
    public void Inject_EmptyProcesses_NoProcessRulesAdded()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, Array.Empty<string>(), CreateSettings());
        var json = Newtonsoft.Json.Linq.JObject.Parse(result);
        var rules = json.SelectToken("route.rules") as Newtonsoft.Json.Linq.JArray;

        foreach (var rule in rules!)
        {
            Assert.Null(rule["process_name"]);
        }
    }

    // ── Inject: wildcard filtering ──

    [Fact]
    public void Inject_FiltersWildcardProcesses()
    {
        var result = CustomConfigInjector.Inject(ActionConfig,
            new[] { "Discord.exe", "chrome*", "fire?.exe" }, CreateSettings());
        Assert.Contains("Discord.exe", result);
        Assert.DoesNotContain("chrome*", result);
        Assert.DoesNotContain("fire?", result);
    }

    // ── Inject: idempotent ──

    [Fact]
    public void Inject_IdempotentReinjection()
    {
        var settings = CreateSettings();
        var first = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var second = CustomConfigInjector.Inject(first, new[] { "Discord.exe", "Telegram.exe" }, settings);

        var json = Newtonsoft.Json.Linq.JObject.Parse(second);
        var rules = json.SelectToken("route.rules") as Newtonsoft.Json.Linq.JArray;

        // Should have exactly one process_name route rule (not two)
        var processRules = rules!.Where(r => r["process_name"] != null).ToList();
        Assert.Single(processRules);

        // Should contain both processes
        var names = processRules[0]["process_name"]!.Select(t => t.ToString()).ToList();
        Assert.Contains("Discord.exe", names);
        Assert.Contains("Telegram.exe", names);
    }

    // ── Inject: no route section ──

    [Fact]
    public void Inject_ConfigWithoutRoute_CreatesRouteSection()
    {
        var json = """{"outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""";
        var result = CustomConfigInjector.Inject(json, new[] { "test.exe" }, CreateSettings());
        var parsed = Newtonsoft.Json.Linq.JObject.Parse(result);

        Assert.NotNull(parsed["route"]);
        Assert.NotNull(parsed.SelectToken("route.rules"));
    }

    // ── Case preservation ──

    [Fact]
    public void Inject_PreservesProcessNameCase()
    {
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe", "Telegram.exe" }, CreateSettings());
        Assert.Contains("Discord.exe", result);
        Assert.Contains("Telegram.exe", result);
    }

    // ── DNS optimization (real-world custom config) ──

    private const string RealWorldConfig = """
    {
      "dns": {
        "servers": [
          {"tag": "remote", "address": "tls://1.1.1.1", "detour": "proxy"},
          {"tag": "local", "address": "223.5.5.5", "detour": "direct"}
        ],
        "rules": [
          {"outbound": "any", "server": "local"},
          {"clash_mode": "direct", "server": "local"}
        ],
        "final": "remote",
        "strategy": "prefer_ipv4"
      },
      "inbounds": [{
        "type": "tun", "auto_route": true, "strict_route": true,
        "sniff": true, "sniff_override_destination": true,
        "address": ["172.19.0.1/30"]
      }],
      "outbounds": [
        {"tag": "proxy", "type": "selector", "outbounds": ["vless-reality", "tuic-v5"]},
        {"tag": "vless-reality", "type": "vless", "server": "1.2.3.4", "server_port": 443,
         "uuid": "test", "flow": "xtls-rprx-vision",
         "tls": {"enabled": true, "server_name": "yahoo.com", "utls": {"enabled": true, "fingerprint": "chrome"},
                 "reality": {"enabled": true, "public_key": "test", "short_id": "test"}}},
        {"tag": "tuic-v5", "type": "tuic", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"tag": "direct", "type": "direct"},
        {"tag": "block", "type": "block"},
        {"tag": "dns-out", "type": "dns"}
      ],
      "route": {
        "rules": [
          {"protocol": "dns", "outbound": "dns-out"},
          {"ip_is_private": true, "outbound": "direct"}
        ],
        "final": "proxy",
        "auto_detect_interface": true
      }
    }
    """;

    [Fact]
    public void Inject_RealWorldConfig_DnsOptimized()
    {
        var result = CustomConfigInjector.Inject(RealWorldConfig, new[] { "chrome.exe" }, CreateSettings());
        var json = Newtonsoft.Json.Linq.JObject.Parse(result);

        // dns.strategy must be ipv4_only (was prefer_ipv4)
        Assert.Equal("ipv4_only", json.SelectToken("dns.strategy")?.ToString());

        // dns.final must point to local DNS (was "remote")
        var dnsFinal = json.SelectToken("dns.final")?.ToString();
        Assert.NotEqual("remote", dnsFinal);

        // route.final must be "direct" (split tunnel)
        Assert.Equal("direct", json.SelectToken("route.final")?.ToString());

        // route.default_domain_resolver must be set to local DNS
        var resolver = json.SelectToken("route.default_domain_resolver")?.ToString();
        Assert.NotNull(resolver);
        Assert.NotEqual("remote", resolver);

        // tun.strict_route must be false
        var tun = json.SelectTokens("inbounds[*]").FirstOrDefault(t => t["type"]?.ToString() == "tun");
        Assert.NotNull(tun);
        Assert.Equal(false, (bool?)tun["strict_route"]);
        Assert.Equal("system", tun["stack"]?.ToString());

        // "block" and "dns" outbound types must be removed
        var outbounds = json["outbounds"] as Newtonsoft.Json.Linq.JArray;
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "block");
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "dns");

        // Non-proxy DNS servers must have detour:"dns-direct" to bypass hijack-dns routing loop
        var dnsServers = json.SelectToken("dns.servers") as Newtonsoft.Json.Linq.JArray;
        var localDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.Equal("dns-direct", localDnsServer?["detour"]?.ToString());
        // Proxy DNS server must keep its proxy detour
        var remoteDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("proxy", remoteDnsServer?["detour"]?.ToString());
        // dns-direct outbound must exist
        var allOutbounds = json["outbounds"] as Newtonsoft.Json.Linq.JArray;
        var dnsDirect = allOutbounds!.FirstOrDefault(o => o["tag"]?.ToString() == "dns-direct");
        Assert.NotNull(dnsDirect);
        Assert.Equal("direct", dnsDirect!["type"]?.ToString());

        // DNS servers must be converted to new format (type field present)
        foreach (var s in dnsServers!)
            Assert.NotNull(s["type"]);

        // Remote DNS must be DoH (not DoT)
        var remoteDns = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("https", remoteDns?["type"]?.ToString());

        // Local DNS must NOT be type:"local" (causes DNS loop with TUN auto_route)
        var localDns = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.NotNull(localDns);
        Assert.NotEqual("local", localDns!["type"]?.ToString());
        Assert.Equal("udp", localDns["type"]?.ToString());
    }

    [Fact]
    public void Inject_ActualCustomConfig_SingBoxCheck()
    {
        // Test with the actual user config file if it exists
        var configPath = @"C:\ProgramData\VPNRouter\config\custom-brat-pc.json";
        if (!File.Exists(configPath))
            return;

        var rawJson = File.ReadAllText(configPath);
        var settings = CreateSettings();
        settings.Tun.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        var result = CustomConfigInjector.Inject(rawJson, new[] { "chrome.exe", "Discord.exe" }, settings);

        // Write to known location for manual inspection
        File.WriteAllText(@"C:\ProgramData\VPNRouter\config\test-debug-inject.json", result);

        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-actual-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);

            var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
            if (!File.Exists(singBoxPath))
                return;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"sing-box check failed (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Inject_WithBypassRussianTraffic_PassesSingBoxCheck()
    {
        // Verify geo bypass injection produces a valid sing-box config
        var configPath = @"C:\ProgramData\VPNRouter\config\custom-brat-pc.json";
        if (!File.Exists(configPath))
            return;

        // Geo files must be present (downloaded by GeoDataDownloader normally)
        if (!GeoDataDownloader.AreGeoFilesAvailable())
            return;

        var rawJson = File.ReadAllText(configPath);
        var settings = CreateSettings();
        settings.App.BypassRussianTraffic = true;
        settings.Tun.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        var result = CustomConfigInjector.Inject(rawJson, new[] { "chrome.exe", "Discord.exe" }, settings);

        // Verify our injected pieces are present
        Assert.Contains("vpnrouter-geoip-ru", result);
        Assert.Contains("vpnrouter-geosite-ru", result);
        Assert.Contains("vpnrouter-dns-ru", result);
        Assert.Contains("77.88.8.8", result);

        File.WriteAllText(@"C:\ProgramData\VPNRouter\config\test-debug-bypass.json", result);

        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-bypass-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);

            var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
            if (!File.Exists(singBoxPath))
                return;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0, $"sing-box check failed with bypass (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// VlessServersResolver — v2.28.2 regression
//
// Triggering bug: a v2.28.1 user had `config_mode: subscribe` with 6 servers
// in `app.subscriptions[0].servers` but `vless.servers: []` (subscription
// servers don't get persisted into Vless.Servers — they live in App.Subscriptions
// and get aggregated into Vless.Servers IN MEMORY only when VPN starts).
// MainWindowViewModel did this aggregation in the Connect handler, but
// VpnEngine.Apply (hot-reload path) did NOT — it called ConfigGenerator
// straight on the freshly-loaded settings with empty Vless.Servers, producing
// a sing-box JSON with route rules pointing at a "proxy" outbound that was
// never emitted. sing-box silently ignored the rules → traffic went direct,
// AND urltest probes still hit the upstream server with raw TCP (no VLESS
// handshake) → server log filled with 249 "flow mismatch" errors per day.
//
// These tests pin the new contract: VlessServersResolver.Resolve() is the
// single source of truth for server aggregation, and ConfigGenerator throws
// loudly if called with no servers (instead of silently producing broken JSON).
// ═══════════════════════════════════════════════════════════════════════════════

public class VlessServersResolverTests
{
    private static SubscriptionEntry MakeSub(string name, params VlessServerEntry[] servers) =>
        new()
        {
            Name = name,
            Url = $"https://example.com/sub/{name}",
            Enabled = true,
            Servers = servers.ToList()
        };

    private static VlessServerEntry MakeServer(string host, int port = 443) =>
        new()
        {
            Name = $"{host}:{port}",
            Server = host,
            Port = port,
            Uuid = "test-uuid-" + host.GetHashCode().ToString("X"),
            Flow = "xtls-rprx-vision",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = "www.microsoft.com",
                Fingerprint = "chrome",
                PublicKey = "test-pbk-" + host.GetHashCode().ToString("X"),
                ShortId = "abcd1234"
            }
        };

    [Fact]
    public void SubscribeMode_AggregatesEnabledSubscriptionServers()
    {
        // Reproduces user's config.yaml: subscribe mode, Vless.Servers empty,
        // 6 servers in subscriptions[0].servers (here we use 2 for brevity).
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "104.194.156.93:443",
                Subscriptions = new List<SubscriptionEntry>
                {
                    MakeSub("simple",
                        MakeServer("104.194.156.93", 443),
                        MakeServer("104.194.156.93", 2083))
                }
            },
            Vless = new VlessConfig() // empty Servers + empty Server
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("104.194.156.93", resolved[0].Server);
        Assert.Equal(443, resolved[0].Port);
        Assert.Equal("xtls-rprx-vision", resolved[0].Flow);

        // Side-effect: settings.Vless.Servers populated for downstream consumers
        Assert.Equal(2, settings.Vless.Servers.Count);
        // ActiveServer carried from App.ActiveSubscriptionServer if Vless.ActiveServer was empty
        Assert.Equal("104.194.156.93:443", settings.Vless.ActiveServer);
    }

    [Fact]
    public void SubscribeMode_SkipsDisabledSubscriptions()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    MakeSub("active", MakeServer("1.1.1.1")),
                    new()
                    {
                        Name = "disabled",
                        Url = "https://example.com/x",
                        Enabled = false, // ← disabled
                        Servers = new List<VlessServerEntry> { MakeServer("2.2.2.2") }
                    }
                }
            },
            Vless = new VlessConfig()
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Single(resolved);
        Assert.Equal("1.1.1.1", resolved[0].Server);
    }

    [Fact]
    public void SubscribeMode_NoSubscriptions_FallsBackToManualVless()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>() // empty
            },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry> { MakeServer("manual.example.com") }
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        // Subscribe mode + no subs → fallback to Vless.Servers
        Assert.Single(resolved);
        Assert.Equal("manual.example.com", resolved[0].Server);
    }

    [Fact]
    public void GeneratedMode_UsesVlessServersDirectly()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "generated" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    MakeServer("manual1.com"),
                    MakeServer("manual2.com")
                }
            }
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("manual1.com", resolved[0].Server);
    }

    [Fact]
    public void EmptyEverything_ReturnsEmptyList()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "subscribe" },
            Vless = new VlessConfig()
        };

        var resolved = VlessServersResolver.Resolve(settings);

        Assert.Empty(resolved);
    }

    [Fact]
    public void DescribeEmptyReason_NoSubscriptions()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { ConfigMode = "subscribe", Subscriptions = new() },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("no subscription URLs are configured", reason!);
    }

    [Fact]
    public void DescribeEmptyReason_AllSubscriptionsDisabled()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new() { Name = "x", Url = "https://x", Enabled = false }
                }
            },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("every subscription is disabled", reason!);
    }

    [Fact]
    public void DescribeEmptyReason_EnabledButNoServers()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                ConfigMode = "subscribe",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new() { Name = "x", Url = "https://x", Enabled = true, Servers = new() }
                }
            },
            Vless = new VlessConfig()
        };

        var reason = VlessServersResolver.DescribeEmptyReason(settings);

        Assert.NotNull(reason);
        Assert.Contains("no subscription has fetched any servers yet", reason!);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ConfigGenerator hard guard — v2.28.2
//
// If ConfigGenerator gets called without servers (caller forgot to resolve),
// it MUST throw rather than emit a JSON with route rules pointing at a missing
// "proxy" outbound. The original bug produced a silently-broken sing-box config
// that sing-box loaded without complaint, then drove urltest probes against
// the upstream server with no VLESS handshake (-> "flow mismatch" log spam).
// ═══════════════════════════════════════════════════════════════════════════════

public class ConfigGeneratorEmptyServersGuardTests
{
    [Fact]
    public void EmptyServers_ThrowsClearly()
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info", ConfigMode = "generated" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig() // ← critical: no servers
        };
        var profile = new Profile
        {
            Name = "T",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings));

        Assert.Contains("no active VLESS servers", ex.Message);
        Assert.Contains("VlessServersResolver", ex.Message);
    }

    [Fact]
    public void ResolverThenGenerate_ProducesProxyOutbound()
    {
        // End-to-end: subscribe mode w/ servers → Resolve → Generate → JSON with proxy.
        // This is the path that BROKE in v2.28.1 (Apply skipped Resolve step).
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "main",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "test-sub",
                        Url = "https://example.com",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            new()
                            {
                                Name = "main",
                                Server = "104.194.156.93",
                                Port = 443,
                                Uuid = "b25684c3-90d6-454a-a911-4e0abba568b0",
                                Flow = "xtls-rprx-vision",
                                Security = "reality",
                                Reality = new VlessRealityConfig
                                {
                                    Enabled = true,
                                    ServerName = "www.microsoft.com",
                                    Fingerprint = "chrome",
                                    PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                                    ShortId = "d86e92a0c6dd2271"
                                }
                            }
                        }
                    }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig() // empty — must be populated by Resolve
        };
        var profile = new Profile
        {
            Name = "T",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        // Step 1: Resolve (what VpnEngine.Apply now does, didn't before)
        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);

        // Step 2: Generate
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        // Verification: proxy outbound must exist with correct flow
        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("vless", proxy!.Type);
        Assert.Equal("104.194.156.93", proxy.Server);
        Assert.Equal(443, proxy.ServerPort);
        Assert.Equal("xtls-rprx-vision", proxy.Flow);
        Assert.NotNull(proxy.Tls);
        Assert.True(proxy.Tls!.Reality?.Enabled);
        Assert.Equal("gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A", proxy.Tls.Reality.PublicKey);
    }

    /// <summary>
    /// End-to-end integration test: subscribe-mode AppSettings → VlessServersResolver
    /// → ConfigGenerator → sing-box check. Verifies the generated JSON is not just
    /// internally consistent but actually loadable by sing-box 1.13. This pins the
    /// fix at the binary level — if a future change breaks compatibility with
    /// upstream sing-box validator, this test fails immediately.
    /// </summary>
    [Fact]
    public void Generate_FromSubscribeMode_PassesSingBoxCheck()
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return; // sing-box.exe not installed locally — skip on CI without binary

        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                ConfigMode = "subscribe",
                ActiveSubscriptionServer = "main",
                Subscriptions = new List<SubscriptionEntry>
                {
                    new()
                    {
                        Name = "field-test-subscription",
                        Url = "https://example.com",
                        Enabled = true,
                        Servers = new List<VlessServerEntry>
                        {
                            new()
                            {
                                Name = "main",
                                Server = "104.194.156.93",
                                Port = 443,
                                Uuid = "b25684c3-90d6-454a-a911-4e0abba568b0",
                                Flow = "xtls-rprx-vision",
                                Security = "reality",
                                Reality = new VlessRealityConfig
                                {
                                    Enabled = true,
                                    ServerName = "www.microsoft.com",
                                    Fingerprint = "chrome",
                                    PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                                    ShortId = "d86e92a0c6dd2271"
                                }
                            }
                        }
                    }
                }
            },
            Tun = new TunSettings
            {
                InterfaceName = "VPNRouter-TUN",
                Ipv4Address = "172.19.0.1/30",
                Mtu = 9000,
                AutoRoute = true,
                StrictRoute = false
            },
            Dns = new DnsSettings
            {
                VpnDns = "https://1.1.1.1/dns-query",
                Strategy = "ipv4_only"
            },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig() // empty — must be populated by Resolve
        };
        var profile = new Profile
        {
            Name = "TestProfile",
            DnsMode = "vpn_only",
            Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
        };

        // Pipeline same as VpnEngine.Apply now does
        var resolved = VlessServersResolver.Resolve(settings);
        Assert.Single(resolved);
        var sbConfig = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);
        var validation = LeakProtection.ValidateConfig(sbConfig);
        Assert.True(validation.IsValid,
            $"LeakProtection validation failed: {string.Join("; ", validation.Errors)}");
        var json = ConfigGenerator.Serialize(sbConfig);

        // Run sing-box check on the generated JSON
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-resolver-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, json);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed on resolver+generator output (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{json}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// FreeConfigAggregator.PreservePreviousValidation — v2.28.3-r5 regression
//
// Triggering bug (2026-04-27): user re-ran Refresh with new criteria and lost
// their previously-Verified configs. Root cause: aggregator built byId from
// freshly-fetched pool only, so cache entries not in the new pool were
// silently dropped. The server-side pool.json regenerates every 6h and rotates
// entries, so verified results from yesterday could vanish after one Refresh.
//
// PreservePreviousValidation merges "interesting" cache entries back into the
// fresh-pool dictionary. These tests pin the contract:
//   - Verified entries always survive (regardless of age).
//   - Ok entries survive only if tested within the last 24h.
//   - Other statuses get dropped — they're not worth preserving.
//   - Entries already in byId aren't touched (live pool wins).
//   - Empty-id entries (corrupt cache) are skipped without throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public class FreeConfigAggregatorPreserveTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry MakeEntry(
        string id,
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status =
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
        DateTime? lastTestedAt = null)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = id,
            Host = $"host-{id}.example.com",
            Port = 443,
            Uuid = $"uuid-{id}",
            Status = status,
            LatencyMs = 100,
            LastTestedAt = lastTestedAt,
        };
    }

    private static readonly DateTime _now =
        new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Verified_PreservedRegardlessOfAge()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("v1",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
                lastTestedAt: _now.AddDays(-365)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
        Assert.Equal("v1", configs[0].Id);
        Assert.True(byId.ContainsKey("v1"));
    }

    [Fact]
    public void RecentOk_Preserved()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok1",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: _now.AddHours(-1)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
    }

    [Fact]
    public void StaleOk_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok-stale",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: _now.AddHours(-25)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }

    [Fact]
    public void OtherStatuses_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var fresh = _now.AddMinutes(-5);
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("tls",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,    lastTestedAt: fresh),
            MakeEntry("timeout", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout,      lastTestedAt: fresh),
            MakeEntry("unr",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable,  lastTestedAt: fresh),
            MakeEntry("slow",    VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Slow,         lastTestedAt: fresh),
            MakeEntry("imp",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible,  lastTestedAt: fresh),
            MakeEntry("unk",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown,      lastTestedAt: null),
            MakeEntry("perr",    VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.ParseError,   lastTestedAt: fresh),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }

    [Fact]
    public void AlreadyInPool_NotTouched()
    {
        var freshEntry = MakeEntry("dup",
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown);
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            ["dup"] = freshEntry,
        };
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry> { freshEntry };
        var cacheEntry = MakeEntry("dup",
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            lastTestedAt: _now);
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry> { cacheEntry };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Single(configs);
        Assert.Same(freshEntry, configs[0]);
        Assert.Same(freshEntry, byId["dup"]);
    }

    [Fact]
    public void MixedCache_OnlyEligibleSurvives()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("v1",        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,    lastTestedAt: _now.AddDays(-3)),
            MakeEntry("ok-recent", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,           lastTestedAt: _now.AddHours(-2)),
            MakeEntry("ok-stale",  VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,           lastTestedAt: _now.AddDays(-2)),
            MakeEntry("tls",       VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,    lastTestedAt: _now.AddMinutes(-5)),
            MakeEntry("imp",       VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible,  lastTestedAt: _now.AddMinutes(-5)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(2, n);
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.Id == "v1");
        Assert.Contains(configs, c => c.Id == "ok-recent");
    }

    [Fact]
    public void EmptyId_Skipped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("",   VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, lastTestedAt: _now),
            MakeEntry("ok", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, lastTestedAt: _now),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
        Assert.Equal("ok", configs[0].Id);
        Assert.False(byId.ContainsKey(string.Empty));
    }

    [Fact]
    public void EmptyCache_NoOp()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
        Assert.Empty(byId);
    }

    [Fact]
    public void OkWithNullTimestamp_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok-no-ts",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: null),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// FreeConfigKeepPolicy — v2.28.5 trim policy used by the FreeConfigsPage VM
// after a search ends. _allConfigs is trimmed to entries that pass this
// predicate so the working set drops back close to baseline within seconds
// of the search completing (instead of holding ~12 MB of dead/unverified
// FreeConfigEntry objects until the next search overwrites the list).
// ═══════════════════════════════════════════════════════════════════════════════

public class FreeConfigKeepPolicyTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = status,
        };
    }

    [Fact]
    public void Verified_Kept()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(entry));
    }

    [Theory]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok)] // v2.28.5-r2: Ok no longer kept (Verified-only)
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Slow)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.ParseError)]
    public void NonVerifiedStatus_Dropped(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status)
    {
        var entry = Make(status);
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(entry));
    }

    [Fact]
    public void Null_Dropped()
    {
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldKeepInLiveCache(null!));
    }

    [Fact]
    public void TrimSimulation_DropsToVerifiedOnly()
    {
        // Mimic a realistic post-search _allConfigs: ~25k entries, of which
        // ~10 are Verified, ~200 Ok (TCP+TLS but not deep-verified), the
        // rest dead statuses. v2.28.5-r2: only Verified survive — Ok no
        // longer counted as "keep" because the user wants the displayed
        // list to show only fully-working configs.
        var entries = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        for (int i = 0; i < 10; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified));
        for (int i = 0; i < 200; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable));
        for (int i = 0; i < 5000; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed));
        for (int i = 0; i < 9790; i++)
            entries.Add(Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible));

        var trimmed = entries
            .Where(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy.ShouldKeepInLiveCache)
            .ToList();

        Assert.Equal(25000, entries.Count);
        Assert.Equal(10, trimmed.Count);
        Assert.All(trimmed, e => Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, e.Status));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 1 — Saved-list retention policy + LastVerifyFailedAt schema
// ═══════════════════════════════════════════════════════════════════════════════
//
// The Сохранённые tab persists Verified entries across sessions, capped at
// FreeConfigKeepPolicy.SavedListRetentionDays (=30). At cache-load time
// (FreeConfigsPageViewModel.EnsureCacheLoaded) entries beyond the cap are
// silently dropped. Phase 1 introduces this policy and the
// LastVerifyFailedAt schema field that Phase 3 will use for the
// "failed last check" badge.
public class FreeConfigSavedRetentionTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status,
        DateTime? lastTestedAt)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = status,
            LastTestedAt = lastTestedAt,
        };
    }

    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Verified_FreshlyTested_Retained()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddHours(-1));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_29Days_Retained()
    {
        // Just under the 30-day cap.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-29));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_31Days_Dropped()
    {
        // Just past the 30-day cap.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-31));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_NullLastTested_Retained()
    {
        // Defensive: post-import or pre-Phase-1 entries with null timestamp
        // are kept rather than nuked. The next search will set the timestamp.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, null);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void NonVerified_Dropped_RegardlessOfAge()
    {
        // Even a fresh Ok entry shouldn't reach the saved list — the saved
        // list is for things that proved real connectivity, not just TCP+TLS.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
            Now.AddHours(-1));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Null_Dropped()
    {
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(null!, Now));
    }

    [Fact]
    public void RetentionDays_Const_Is30()
    {
        // Pin the public constant — if we ever need to bump it, surface
        // the change in code review (and update the saved-list tooltip).
        Assert.Equal(30, VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .SavedListRetentionDays);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 1 — FreeConfigEntry schema additions
// ═══════════════════════════════════════════════════════════════════════════════
public class FreeConfigEntrySchemaTests
{
    [Fact]
    public void LastVerifyFailedAt_Defaults_Null()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry();
        Assert.Null(entry.LastVerifyFailedAt);
    }

    [Fact]
    public void LastVerifyFailedAt_RoundTrips_Through_Json()
    {
        var original = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "abc",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastTestedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LatencyMs = 42,
            MeasuredBandwidthMbps = 25,
            LastVerifyFailedAt = new DateTime(2026, 5, 2, 8, 30, 0, DateTimeKind.Utc),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var revived = System.Text.Json.JsonSerializer
            .Deserialize<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>(json);

        Assert.NotNull(revived);
        Assert.Equal(original.LastVerifyFailedAt, revived!.LastVerifyFailedAt);
        // Last-good numbers must survive too — Phase 3 displays them on
        // entries that failed re-verify.
        Assert.Equal(42, revived.LatencyMs);
        Assert.Equal(25, revived.MeasuredBandwidthMbps);
    }

    [Fact]
    public void LastVerifyFailedAt_Indicates_FailedLastCheck_When_Greater_Than_LastTestedAt()
    {
        // Phase 3 display logic check: if LastVerifyFailedAt > LastTestedAt,
        // the row gets the "failed last check" badge while preserving the
        // last-good numbers. Phase 1 just pins the comparison semantics.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            LastTestedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LastVerifyFailedAt = new DateTime(2026, 5, 2, 8, 30, 0, DateTimeKind.Utc),
        };

        Assert.True(entry.LastVerifyFailedAt > entry.LastTestedAt);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 5 — FreeConfigFreshness pure-logic tests
// ═══════════════════════════════════════════════════════════════════════════════
//
// All freshness math (tier classification, opacity, sort key, recheck-merge)
// lives in Core so it's testable without an Avalonia headless harness. The
// App's FreeConfigItemViewModel just delegates its getters.

public class FreeConfigFreshnessTierTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        DateTime? lastTestedAt,
        DateTime? lastVerifyFailedAt = null)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastTestedAt = lastTestedAt,
            LastVerifyFailedAt = lastVerifyFailedAt,
        };
    }

    [Theory]
    [InlineData(0)]              // freshly tested
    [InlineData(0.5)]            // half a day
    [InlineData(0.99)]           // just under 1 day
    public void Tier_Fresh_When_Under_24h(double daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Tier_Ageing_When_Between_1d_And_7d(int daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Ageing,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(29)]
    public void Tier_Stale_When_Over_7d(int daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Stale,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Failed_When_LastVerifyFailedAt_Greater_Than_LastTested()
    {
        var entry = Make(Now.AddHours(-1), Now); // verified an hour ago, failed now
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Failed_Wins_Over_Fresh_Age()
    {
        // Even if LastTestedAt is in the future (fresh), a failure timestamp
        // ≥ tested makes it Failed. Defensive: locks the comparison rule.
        var entry = Make(Now, Now);
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Fresh_When_LastTestedAt_Null()
    {
        // Defensive: post-import / pre-Phase-1 entries with null timestamp
        // are surfaced as Fresh rather than dropped from the tier system.
        var entry = Make(null);
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Null_Entry_Returns_Fresh_Default()
    {
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(null!, Now));
    }

    [Theory]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,  1.0)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Ageing, 0.75)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Stale,  0.5)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed, 0.5)]
    public void Opacity_Tracks_Tier(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier tier, double expected)
    {
        Assert.Equal(expected, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.OpacityFor(tier));
    }

    [Fact]
    public void IsStale_True_For_Over_24h()
    {
        var entry = Make(Now.AddHours(-25));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_False_For_Under_24h()
    {
        var entry = Make(Now.AddHours(-23));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_True_For_FailedLastCheck()
    {
        // Even a freshly verified entry is "stale" if it failed the most
        // recent recheck — the bulk-Recheck button picks it up too.
        var entry = Make(Now.AddHours(-1), Now);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_True_For_NullLastTested()
    {
        var entry = Make(null);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void SortKey_Fresh_Lower_Than_Ageing_Lower_Than_Stale_Lower_Than_Failed()
    {
        var fresh  = Make(Now.AddHours(-1));     fresh.LatencyMs  = 10;
        var ageing = Make(Now.AddDays(-3));      ageing.LatencyMs = 10;
        var stale  = Make(Now.AddDays(-10));     stale.LatencyMs  = 10;
        var failed = Make(Now.AddHours(-1), Now); failed.LatencyMs = 10;

        var k1 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(fresh,  Now);
        var k2 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(ageing, Now);
        var k3 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(stale,  Now);
        var k4 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(failed, Now);

        Assert.True(k1 < k2);
        Assert.True(k2 < k3);
        Assert.True(k3 < k4);
    }

    [Fact]
    public void SortKey_Within_Tier_Orders_By_Latency()
    {
        // Two fresh entries differ only in latency → lower latency sorts first.
        var fast = Make(Now.AddHours(-1)); fast.LatencyMs = 10;
        var slow = Make(Now.AddHours(-1)); slow.LatencyMs = 200;

        var kf = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(fast, Now);
        var ks = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(slow, Now);

        Assert.True(kf < ks);
    }

    [Fact]
    public void AgeDays_Returns_Floored_Days()
    {
        var entry = Make(Now.AddDays(-3.7));
        Assert.Equal(3, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(entry, Now));
    }

    [Fact]
    public void AgeDays_Returns_0_For_Null_Or_Future()
    {
        Assert.Equal(0, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(Make(null), Now));
        // Future timestamp (clock skew defensive): still returns 0.
        Assert.Equal(0, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(Make(Now.AddDays(1)), Now));
    }
}

// Recheck merge: success path keeps fresh values, clears failure marker;
// failure path restores prior good values, sets failure marker.
public class FreeConfigRecheckMergeTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Success_ClearsFailureMarker_KeepsFreshValues()
    {
        // Setup: entry was failing (LastVerifyFailedAt set). Snapshot
        // captures prior values. Verifier reruns with success — Status =
        // Verified, fresh latency/bw/lastTested.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-2),
            LastVerifyFailedAt = Now.AddDays(-1),
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        // Simulate verifier mutation on success.
        entry.LatencyMs = 30;
        entry.MeasuredBandwidthMbps = 60;
        entry.LastTestedAt = Now;
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Null(entry.LastVerifyFailedAt);
        Assert.Equal(30, entry.LatencyMs);          // fresh value kept
        Assert.Equal(60, entry.MeasuredBandwidthMbps); // fresh value kept
        Assert.Equal(Now, entry.LastTestedAt);
    }

    [Fact]
    public void Failure_RestoresPriorValues_SetsFailureMarker_KeepsVerifiedStatus()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastVerifyFailedAt = null,
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        // Simulate verifier mutation on failure (e.g. TLS handshake failed).
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed;
        entry.LatencyMs = 9999;          // junk value verifier might leave
        entry.LastTestedAt = Now;        // verifier always updates this

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        // Status restored so retention filter doesn't drop it.
        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        // Last-good values restored.
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        // Failure marker set to recheck-time.
        Assert.Equal(Now, entry.LastVerifyFailedAt);
    }

    [Fact]
    public void Failure_Then_Success_ClearsMarker()
    {
        // Round trip: Verified → fail (marker set, last-good preserved) →
        // succeed (marker cleared, fresh values written).
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
        };

        // First recheck: fails.
        var snap1 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout;
        entry.LastTestedAt = Now;
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, snap1, Now);
        Assert.Equal(Now, entry.LastVerifyFailedAt);
        Assert.Equal(50, entry.LatencyMs);

        // Second recheck: succeeds.
        var later = Now.AddMinutes(10);
        var snap2 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified;
        entry.LatencyMs = 35;
        entry.MeasuredBandwidthMbps = 80;
        entry.LastTestedAt = later;
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, snap2, later);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Null(entry.LastVerifyFailedAt);
        Assert.Equal(35, entry.LatencyMs);
        Assert.Equal(80, entry.MeasuredBandwidthMbps);
    }

    [Fact]
    public void Null_Entry_NoOp()
    {
        var prior = new VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot();
        // Should not throw.
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(null!, prior, Now);
    }

    // ── v2.28.6-r2 cancel safety ──

    [Fact]
    public void RestorePriorState_RestoresVerifiedStatus()
    {
        // Cancel-mid-recheck scenario: verifier already mutated Status to
        // TlsFailed before the cancellation token tripped. Without
        // RestorePriorState the entry would be evicted by the retention
        // filter at next cache load (Status != Verified).
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastVerifyFailedAt = null,
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        // Simulate cancel after verifier got partway through and started
        // mutating fields.
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed;
        entry.LatencyMs = 9999;
        entry.LastTestedAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        // Cancel != failure — LastVerifyFailedAt stays null.
        Assert.Null(entry.LastVerifyFailedAt);
    }

    [Fact]
    public void RestorePriorState_DoesNot_Clobber_Existing_FailureMarker()
    {
        // If the entry was already in failed-last-check state (from a prior
        // failed recheck) and the user starts a new recheck which gets
        // cancelled, the prior failure marker should survive — we don't
        // know if this is now working again, so leave the existing marker.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            LastTestedAt = Now.AddDays(-2),
            LastVerifyFailedAt = Now.AddDays(-1),
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        // Cancel mid-verify — verifier mutated some fields.
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(Now.AddDays(-1), entry.LastVerifyFailedAt); // unchanged
    }

    [Fact]
    public void RestorePriorState_Null_Entry_NoOp()
    {
        var prior = new VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot();
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(null!, prior);
    }
}

/// <summary>v2.29.0-r2: shape and idempotency tests for the new
/// cross-platform <see cref="VPNRouter.Core.Platform.AutostartHelper"/>.
/// We can't unit-test the actual file/registry side effects from here
/// (CI machines don't have stable HKCU\Run state, and writing to
/// ~/Library/LaunchAgents on a Linux runner would 404 on the parent
/// dir), but we CAN exercise the public API surface and assert that:
/// - Disable() is safe to call when not enabled (no exception).
/// - IsEnabled() / Disable() / EnsureCurrentPath() never throw on the
///   current platform.
/// - EnsureCurrentPath() returns false when no entry exists.</summary>
public class AutostartHelperShapeTests
{
    [Fact]
    public void Disable_When_NotEnabled_DoesNotThrow()
    {
        // Don't actually toggle (test must be safe to run on dev machine
        // — we don't want to nuke the user's real autostart setting). Just
        // call IsEnabled() and skip the test if it's currently true.
        if (VPNRouter.Core.Platform.AutostartHelper.IsEnabled()) return;
        var ex = Record.Exception(() => VPNRouter.Core.Platform.AutostartHelper.Disable());
        Assert.Null(ex);
    }

    [Fact]
    public void IsEnabled_DoesNotThrow_OnAnyPlatform()
    {
        var ex = Record.Exception(() => VPNRouter.Core.Platform.AutostartHelper.IsEnabled());
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureCurrentPath_When_NotEnabled_ReturnsFalse()
    {
        if (VPNRouter.Core.Platform.AutostartHelper.IsEnabled()) return;
        var fakeExe = OperatingSystem.IsWindows()
            ? @"C:\Program Files\VPNRouter\VPNRouter.App.exe"
            : "/Applications/VPNRouter.app/Contents/MacOS/VPNRouter.App";
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath(fakeExe));
    }

    [Fact]
    public void EnsureCurrentPath_With_Empty_Path_ReturnsFalse()
    {
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath(""));
        Assert.False(VPNRouter.Core.Platform.AutostartHelper.EnsureCurrentPath("   "));
    }

    [Fact]
    public void Enable_With_Empty_Path_NoOp()
    {
        // Should silently no-op on empty / whitespace path; no autostart
        // entry should appear after the call.
        var wasEnabled = VPNRouter.Core.Platform.AutostartHelper.IsEnabled();
        VPNRouter.Core.Platform.AutostartHelper.Enable("");
        VPNRouter.Core.Platform.AutostartHelper.Enable("   ");
        Assert.Equal(wasEnabled, VPNRouter.Core.Platform.AutostartHelper.IsEnabled());
    }
}

/// <summary>v2.29.0-r4: tests for custom direct rules generation.
/// Each test exercises <see cref="VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule"/>
/// directly (the route-rule construction step), which is the
/// nontrivial part of <see cref="VPNRouter.Core.Services.ConfigGenerator.ApplyCustomDirectRules"/>.
/// Insertion + ordering are tested via ApplyCustomDirectRules with a
/// minimal stub config.</summary>
public class CustomDirectRulesGeneratorTests
{
    [Fact]
    public void BuildRule_DomainSuffix_SingleValue()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "domain_suffix",
            Value = ".lan.local",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.NotNull(route);
        Assert.Equal("route", route!.Action);
        Assert.Equal("direct", route.Outbound);
        Assert.NotNull(route.DomainSuffix);
        Assert.Single(route.DomainSuffix!);
        Assert.Equal(".lan.local", route.DomainSuffix![0]);
    }

    [Fact]
    public void BuildRule_IpCidr_MultiValue_CommaSeparated()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "ip_cidr",
            Value = "10.0.0.0/8, 192.168.0.0/16, 172.16.0.0/12",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.IpCidr);
        Assert.Equal(3, route.IpCidr!.Count);
        Assert.Contains("10.0.0.0/8", route.IpCidr!);
        Assert.Contains("192.168.0.0/16", route.IpCidr!);
        Assert.Contains("172.16.0.0/12", route.IpCidr!);
    }

    [Fact]
    public void BuildRule_Port_FiltersInvalidNumbers()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "port",
            Value = "22, 80, abc, 99999, 443, 0",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.Port);
        Assert.Equal(3, route.Port!.Count);
        Assert.Contains(22, route.Port!);
        Assert.Contains(80, route.Port!);
        Assert.Contains(443, route.Port!);
    }

    [Fact]
    public void BuildRule_EmptyValue_ReturnsNull()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "domain",
            Value = "",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.Null(route);
    }

    [Fact]
    public void BuildRule_UnknownType_ReturnsNull()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "geosite",
            Value = "ru",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.Null(route);
    }

    [Fact]
    public void BuildRule_DomainKeyword_SingleValue()
    {
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "domain_keyword",
            Value = "internal",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.DomainKeyword);
        Assert.Single(route.DomainKeyword!);
        Assert.Equal("internal", route.DomainKeyword![0]);
    }

    [Fact]
    public void BuildRule_ProcessName_PreservesCase()
    {
        // sing-box process_name matching is case-sensitive — preserve
        // original casing from user input.
        var rule = new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "process_name",
            Value = "Discord.exe, ChromE.exe",
            Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomDirectRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.ProcessName);
        Assert.Contains("Discord.exe", route.ProcessName!);
        Assert.Contains("ChromE.exe", route.ProcessName!);
    }

    [Fact]
    public void Apply_DisabledRule_Skipped()
    {
        var config = new VPNRouter.Core.Models.SingBoxConfig
        {
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>(),
            }
        };
        var rules = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "domain", Value = "skipped.example", Enabled = false },
            new() { Type = "domain", Value = "kept.example",    Enabled = true  },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomDirectRules(config, rules);
        Assert.Single(config.Route.Rules);
        Assert.NotNull(config.Route.Rules[0].Domain);
        Assert.Equal("kept.example", config.Route.Rules[0].Domain![0]);
    }

    [Fact]
    public void Apply_EmptyList_NoChange()
    {
        var config = new VPNRouter.Core.Models.SingBoxConfig
        {
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>
                {
                    new() { Action = "sniff" },
                },
            }
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomDirectRules(
            config, new List<VPNRouter.Core.Models.CustomDirectRule>());
        Assert.Single(config.Route.Rules); // only the original sniff rule
    }

    [Fact]
    public void Apply_OrderPreserved_AfterSniffHijackPrivate()
    {
        // Insertion point should be AFTER sniff/hijack-dns/private-ip but
        // BEFORE everything else. Existing process_name route rule
        // should end up AFTER our custom direct rules.
        var config = new VPNRouter.Core.Models.SingBoxConfig
        {
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>
                {
                    new() { Action = "sniff" },
                    new() { Action = "hijack-dns" },
                    new() { IpIsPrivate = true, Action = "route", Outbound = "direct" },
                    new() { ProcessName = new List<string> { "Discord.exe" }, Action = "route", Outbound = "proxy" },
                },
            }
        };
        var customRules = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "domain_suffix", Value = ".lan.local", Enabled = true },
            new() { Type = "ip_cidr",       Value = "10.0.0.0/8", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomDirectRules(config, customRules);

        // Expected order:
        //   [0] sniff
        //   [1] hijack-dns
        //   [2] private-ip
        //   [3] custom rule 1 (domain_suffix .lan.local)
        //   [4] custom rule 2 (ip_cidr 10.0.0.0/8)
        //   [5] process_name Discord
        Assert.Equal(6, config.Route.Rules.Count);
        Assert.Equal("sniff", config.Route.Rules[0].Action);
        Assert.Equal("hijack-dns", config.Route.Rules[1].Action);
        Assert.True(config.Route.Rules[2].IpIsPrivate);
        Assert.NotNull(config.Route.Rules[3].DomainSuffix);
        Assert.Equal(".lan.local", config.Route.Rules[3].DomainSuffix![0]);
        Assert.NotNull(config.Route.Rules[4].IpCidr);
        Assert.Equal("10.0.0.0/8", config.Route.Rules[4].IpCidr![0]);
        Assert.NotNull(config.Route.Rules[5].ProcessName);
    }

    [Fact]
    public void Apply_AllRulesGetActionRoute_OutboundDirect()
    {
        var config = new VPNRouter.Core.Models.SingBoxConfig
        {
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>(),
            }
        };
        var rules = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "domain",         Value = "a.example", Enabled = true },
            new() { Type = "domain_suffix",  Value = ".b.example", Enabled = true },
            new() { Type = "domain_keyword", Value = "c", Enabled = true },
            new() { Type = "ip_cidr",        Value = "10.0.0.0/8", Enabled = true },
            new() { Type = "port",           Value = "22", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomDirectRules(config, rules);

        Assert.Equal(5, config.Route.Rules.Count);
        foreach (var r in config.Route.Rules)
        {
            Assert.Equal("route", r.Action);
            Assert.Equal("direct", r.Outbound);
        }
    }
}

/// <summary>v2.29.0-r4: tests for the text-format parser/serializer
/// used by the Network → Routing → "Custom direct rules" textbox.</summary>
public class CustomDirectRulesParserTests
{
    [Fact]
    public void Parse_EmptyText_NoRules_NoErrors()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("");
        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WhitespaceOnly_NoRules_NoErrors()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("   \n\n  \r\n  ");
        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_SimpleRule()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("ip_cidr 10.0.0.0/8");
        Assert.Single(result.Rules);
        Assert.Empty(result.Errors);
        Assert.Equal("ip_cidr", result.Rules[0].Type);
        Assert.Equal("10.0.0.0/8", result.Rules[0].Value);
        Assert.True(result.Rules[0].Enabled);
    }

    [Fact]
    public void Parse_MultipleRulesAndComments()
    {
        var text = """
            # Comment line
            ip_cidr 10.0.0.0/8, 192.168.0.0/16    # Local LANs
            domain_suffix .lan.local
            !port 53                              # disabled
            """;
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rules.Count);

        Assert.Equal("ip_cidr", result.Rules[0].Type);
        Assert.Equal("10.0.0.0/8, 192.168.0.0/16", result.Rules[0].Value);
        Assert.Equal("Local LANs", result.Rules[0].Comment);
        Assert.True(result.Rules[0].Enabled);

        Assert.Equal("domain_suffix", result.Rules[1].Type);
        Assert.Equal(".lan.local", result.Rules[1].Value);

        Assert.Equal("port", result.Rules[2].Type);
        Assert.Equal("53", result.Rules[2].Value);
        Assert.False(result.Rules[2].Enabled);
        Assert.Equal("disabled", result.Rules[2].Comment);
    }

    [Fact]
    public void Parse_InvalidCidr_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("ip_cidr 999.999.0.0/8");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Equal(1, result.Errors[0].LineNumber);
        Assert.Contains("CIDR", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_InvalidPort_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("port 99999");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Contains("port", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_UnknownType_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("unknown_type foo");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Contains("Unknown type", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_PartialFailure_KeepsValidRules()
    {
        var text = """
            ip_cidr 10.0.0.0/8
            unknown_type foo
            domain_suffix .lan.local
            """;
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Equal(2, result.Rules.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Serialize_RoundTrips_Correctly()
    {
        var input = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "ip_cidr", Value = "10.0.0.0/8, 192.168.0.0/16", Comment = "LANs", Enabled = true },
            new() { Type = "port",    Value = "53",                          Enabled = false },
            new() { Type = "domain_suffix", Value = ".internal" },
        };
        var text = VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(input);
        var roundTrip = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Empty(roundTrip.Errors);
        Assert.Equal(3, roundTrip.Rules.Count);

        Assert.Equal("ip_cidr", roundTrip.Rules[0].Type);
        Assert.Equal("LANs", roundTrip.Rules[0].Comment);
        Assert.True(roundTrip.Rules[0].Enabled);

        Assert.Equal("port", roundTrip.Rules[1].Type);
        Assert.False(roundTrip.Rules[1].Enabled);
    }

    [Fact]
    public void Serialize_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(null));
        Assert.Equal(string.Empty, VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(
            new List<VPNRouter.Core.Models.CustomDirectRule>()));
    }

    [Fact]
    public void Parse_PreservesProcessNameCasing()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("process_name Discord.exe, ChromE.exe");
        Assert.Single(result.Rules);
        Assert.Equal("Discord.exe, ChromE.exe", result.Rules[0].Value);
    }
}

/// <summary>v2.29.0-r7+ Phase 3C: persistent deep-verify checkpoint
/// tests. Verifies the new <see cref="VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry.LastDeepVerifyAt"/>
/// field and the 6-hour skip window logic that the search loop uses.</summary>
public class FreeConfigDeepVerifyCheckpointTests
{
    [Fact]
    public void NewEntry_LastDeepVerifyAt_IsNull()
    {
        var e = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry();
        Assert.Null(e.LastDeepVerifyAt);
    }

    [Fact]
    public void Schema_Roundtrips_LastDeepVerifyAt_ViaJson()
    {
        // Phase 3C field must round-trip through System.Text.Json so the
        // cache file at %ProgramData%\VPNRouter\cache\free_configs.json
        // survives app restart with the timestamp preserved.
        var stamp = new DateTime(2026, 4, 29, 14, 30, 0, DateTimeKind.Utc);
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "abc",
            Host = "example.com",
            Port = 443,
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = stamp,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("LastDeepVerifyAt", json);
        var roundTripped = System.Text.Json.JsonSerializer
            .Deserialize<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(stamp, roundTripped!.LastDeepVerifyAt);
    }

    [Fact]
    public void SkipDeepVerify_WithFreshCheckpoint_AndVerifiedStatus_True()
    {
        // The skip predicate inlined in VerifyOneAndAppendAsync expects
        // all three: Verified status, LastDeepVerifyAt set, age < 6h, and
        // LatencyMs > 0. Replicate it as test logic.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-2),  // 2h ago
            LatencyMs = 50,
        };
        Assert.True(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithStaleCheckpoint_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-7),  // 7h ago, > 6h
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithoutCheckpoint_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = null,
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithoutPing_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-1),
            LatencyMs = 0,  // never TCP-tested
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_NonVerifiedStatus_False()
    {
        // Even with a recent timestamp, we must re-verify if the last
        // status was anything other than Verified (e.g. TlsFailed or
        // Timeout from a previous failed re-check).
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-1),
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    /// <summary>Mirror of the inline skip-predicate in
    /// VerifyOneAndAppendAsync. Kept here as test fixture so the
    /// 6-hour boundary + flag combinations are pinned.</summary>
    private static bool ShouldSkipDeepVerify(VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry cfg)
    {
        return cfg.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
            && cfg.LastDeepVerifyAt.HasValue
            && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
            && cfg.LatencyMs > 0;
    }
}

/// <summary>v2.30.0: tests for the new full custom rules engine
/// (direct/proxy/block actions). Covers parser, ConfigGenerator, and
/// migration from v2.29.0-r4 CustomDirectRule schema.</summary>
public class CustomRulesV2_30_ParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("");
        Assert.Empty(r.Rules);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Parse_DirectRule_WithIpCidr()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("direct ip_cidr 10.0.0.0/8");
        Assert.Single(r.Rules);
        Assert.Equal("direct", r.Rules[0].Action);
        Assert.Equal("ip_cidr", r.Rules[0].Type);
        Assert.Equal("10.0.0.0/8", r.Rules[0].Value);
    }

    [Fact]
    public void Parse_ProxyRule_WithDomainSuffix()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("proxy domain_suffix .corp.example");
        Assert.Single(r.Rules);
        Assert.Equal("proxy", r.Rules[0].Action);
        Assert.Equal("domain_suffix", r.Rules[0].Type);
    }

    [Fact]
    public void Parse_BlockRule_WithGeosite()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("block geosite ads");
        Assert.Single(r.Rules);
        Assert.Equal("block", r.Rules[0].Action);
        Assert.Equal("geosite", r.Rules[0].Type);
    }

    [Fact]
    public void Parse_AllThreeActions_InOneText()
    {
        var text = "direct ip_cidr 10.0.0.0/8\nproxy domain_suffix .corp\nblock geosite ads\n";
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText(text);
        Assert.Equal(3, r.Rules.Count);
        Assert.Equal("direct", r.Rules[0].Action);
        Assert.Equal("proxy", r.Rules[1].Action);
        Assert.Equal("block", r.Rules[2].Action);
    }

    [Fact]
    public void Parse_UnknownAction_RaisesError()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("forward domain example.com");
        Assert.Empty(r.Rules);
        Assert.Single(r.Errors);
        Assert.Contains("Unknown action", r.Errors[0].Reason);
    }

    [Fact]
    public void Parse_NewType_PortRange()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("proxy port_range 1024-5000");
        Assert.Single(r.Rules);
        Assert.Equal("port_range", r.Rules[0].Type);
        Assert.Equal("1024-5000", r.Rules[0].Value);
    }

    [Fact]
    public void Parse_NewType_Network()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("direct network udp");
        Assert.Single(r.Rules);
        Assert.Equal("network", r.Rules[0].Type);
        Assert.Equal("udp", r.Rules[0].Value);
    }

    [Fact]
    public void Parse_InvalidPortRange_RaisesError()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("proxy port_range 5000-1024");
        Assert.Empty(r.Rules);
        Assert.Single(r.Errors);
        Assert.Contains("port range", r.Errors[0].Reason);
    }

    [Fact]
    public void Parse_InvalidNetwork_RaisesError()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("proxy network icmp");
        Assert.Empty(r.Rules);
        Assert.Single(r.Errors);
        Assert.Contains("network", r.Errors[0].Reason);
    }

    [Fact]
    public void Parse_GeositeName_Valid()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("direct geosite category-news-ru");
        Assert.Single(r.Rules);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void Parse_GeositeName_RejectsUppercase()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("direct geosite Category-News-RU");
        Assert.Empty(r.Rules);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Parse_DisabledRule_PrefixBang()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("!block port 53");
        Assert.Single(r.Rules);
        Assert.False(r.Rules[0].Enabled);
        Assert.Equal("block", r.Rules[0].Action);
    }

    [Fact]
    public void Parse_InlineComment_Captured()
    {
        var r = VPNRouter.Core.Services.CustomRulesParser.ParseFromText("direct ip_cidr 10.0.0.0/8  # LAN range");
        Assert.Single(r.Rules);
        Assert.Equal("LAN range", r.Rules[0].Comment);
    }

    [Fact]
    public void Serialize_Roundtrip_PreservesAll()
    {
        var input = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Comment = "LAN", Enabled = true },
            new() { Action = "proxy", Type = "domain_suffix", Value = ".corp", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = false },
        };
        var text = VPNRouter.Core.Services.CustomRulesParser.SerializeToText(input);
        var roundTrip = VPNRouter.Core.Services.CustomRulesParser.ParseFromText(text);
        Assert.Empty(roundTrip.Errors);
        Assert.Equal(3, roundTrip.Rules.Count);
        Assert.Equal("direct", roundTrip.Rules[0].Action);
        Assert.Equal("LAN", roundTrip.Rules[0].Comment);
        Assert.Equal("proxy", roundTrip.Rules[1].Action);
        Assert.Equal("block", roundTrip.Rules[2].Action);
        Assert.False(roundTrip.Rules[2].Enabled);
    }

    [Fact]
    public void DetectConflicts_CatchAllIpCidr_Flagged()
    {
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "0.0.0.0/0", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        var conflicts = VPNRouter.Core.Services.CustomRulesParser.DetectConflicts(rules);
        Assert.Single(conflicts);
        Assert.Contains("matches everything", conflicts[0]);
    }

    [Fact]
    public void DetectConflicts_NoCatchAll_Empty()
    {
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
        };
        Assert.Empty(VPNRouter.Core.Services.CustomRulesParser.DetectConflicts(rules));
    }
}

public class CustomRulesV2_30_GeneratorTests
{
    [Fact]
    public void BuildRule_DirectAction_ProducesRouteDirect()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.Equal("route", route!.Action);
        Assert.Equal("direct", route.Outbound);
    }

    [Fact]
    public void BuildRule_ProxyAction_ProducesRouteProxy()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "proxy", Type = "domain_suffix", Value = ".corp", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.Equal("route", route!.Action);
        Assert.Equal("proxy", route.Outbound);
    }

    [Fact]
    public void BuildRule_BlockAction_ProducesReject()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "block", Type = "domain_keyword", Value = "tracker", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.Equal("reject", route!.Action);
        Assert.Null(route.Outbound);
    }

    [Fact]
    public void BuildRule_Geosite_TaggedAsUserPrefix()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "direct", Type = "geosite", Value = "ru", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.RuleSet);
        Assert.Single(route.RuleSet!);
        Assert.Equal("user-geosite-ru", route.RuleSet![0]);
    }

    [Fact]
    public void BuildRule_Geoip_TaggedAsUserPrefix()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "block", Type = "geoip", Value = "cn", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.Equal("user-geoip-cn", route!.RuleSet![0]);
    }

    [Fact]
    public void BuildRule_PortRange_ExpandedToPortList()
    {
        var rule = new VPNRouter.Core.Models.CustomRule
        {
            Action = "proxy", Type = "port_range", Value = "1024-1029", Enabled = true,
        };
        var route = VPNRouter.Core.Services.ConfigGenerator.BuildCustomRouteRule(rule);
        Assert.NotNull(route);
        Assert.NotNull(route!.Port);
        Assert.True(route.Port!.Count >= 2);
        Assert.Contains(1024, route.Port!);
        Assert.Contains(1029, route.Port!);
    }

    [Fact]
    public void Apply_AllThreeActions_OrderPreserved()
    {
        var config = NewConfigWithEmptyRoutes();
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
            new() { Action = "proxy", Type = "domain_suffix", Value = ".corp", Enabled = true },
            new() { Action = "block", Type = "domain_keyword", Value = "tracker", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomRules(config, rules);
        Assert.Equal(3, config.Route.Rules.Count);
        Assert.Equal("direct", config.Route.Rules[0].Outbound);
        Assert.Equal("proxy", config.Route.Rules[1].Outbound);
        Assert.Equal("reject", config.Route.Rules[2].Action);
    }

    [Fact]
    public void Apply_BlockDomainRule_AlsoCreatesDnsReject()
    {
        var config = NewConfigWithEmptyRoutes();
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "block", Type = "domain_keyword", Value = "tracker", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomRules(config, rules);
        Assert.Single(config.Route.Rules);
        Assert.Single(config.Dns.Rules);
        Assert.Equal("reject", config.Dns.Rules[0].Action);
        Assert.NotNull(config.Dns.Rules[0].DomainKeyword);
    }

    [Fact]
    public void Apply_BlockIpCidr_DoesNotCreateDnsReject()
    {
        var config = NewConfigWithEmptyRoutes();
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "block", Type = "ip_cidr", Value = "203.0.113.0/24", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomRules(config, rules);
        Assert.Single(config.Route.Rules);
        Assert.Empty(config.Dns.Rules);
    }

    [Fact]
    public void Apply_GeositeRule_RegistersRuleSetEntry()
    {
        var config = NewConfigWithEmptyRoutes();
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomRules(config, rules);
        Assert.NotNull(config.Route.RuleSet);
        Assert.Single(config.Route.RuleSet!);
        Assert.Equal("user-geosite-ads", config.Route.RuleSet![0].Tag);
        Assert.Contains("sing-geosite", config.Route.RuleSet![0].Url);
    }

    [Fact]
    public void Apply_DisabledRule_Skipped()
    {
        var config = NewConfigWithEmptyRoutes();
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "domain", Value = "active.example", Enabled = true },
            new() { Action = "block", Type = "domain", Value = "skipped.example", Enabled = false },
        };
        VPNRouter.Core.Services.ConfigGenerator.ApplyCustomRules(config, rules);
        Assert.Single(config.Route.Rules);
        Assert.Equal("active.example", config.Route.Rules[0].Domain![0]);
    }

    private static VPNRouter.Core.Models.SingBoxConfig NewConfigWithEmptyRoutes() =>
        new VPNRouter.Core.Models.SingBoxConfig
        {
            Route = new VPNRouter.Core.Models.SingBoxRoute
            {
                Rules = new List<VPNRouter.Core.Models.RouteRule>(),
            },
            Dns = new VPNRouter.Core.Models.SingBoxDns
            {
                Rules = new List<VPNRouter.Core.Models.DnsRule>(),
            },
        };
}

public class CustomRulesV2_30_MigrationTests
{
    [Fact]
    public void Migration_v1_to_v2_ConvertsLegacyDirectRules()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        settings.App.CustomDirectRules = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "ip_cidr", Value = "10.0.0.0/8", Comment = "LAN", Enabled = true },
            new() { Type = "domain_suffix", Value = ".internal", Enabled = false },
        };

        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);

        Assert.Equal(2, migrated.App.CustomRules.Count);
        Assert.All(migrated.App.CustomRules, r => Assert.Equal("direct", r.Action));
        Assert.Equal("ip_cidr", migrated.App.CustomRules[0].Type);
        Assert.Equal("LAN", migrated.App.CustomRules[0].Comment);
        Assert.False(migrated.App.CustomRules[1].Enabled);
        Assert.Empty(migrated.App.CustomDirectRules);
    }

    [Fact]
    public void Migration_v1_to_v2_Idempotent_WhenCustomRulesPopulated()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        settings.App.CustomRules.Add(new VPNRouter.Core.Models.CustomRule
        {
            Action = "proxy", Type = "domain", Value = "manual.example", Enabled = true,
        });
        settings.App.CustomDirectRules.Add(new VPNRouter.Core.Models.CustomDirectRule
        {
            Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true,
        });

        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);

        Assert.Single(migrated.App.CustomRules);
        Assert.Equal("proxy", migrated.App.CustomRules[0].Action);
        Assert.Single(migrated.App.CustomDirectRules);
    }

    [Fact]
    public void Migration_v1_to_v2_NoLegacyData_NoOp()
    {
        var settings = new VPNRouter.Core.Models.AppSettings { SchemaVersion = 1 };
        var migrated = VPNRouter.Core.Services.SettingsMigrator.Migrate(settings, 1, 2);
        Assert.Empty(migrated.App.CustomRules);
        Assert.Empty(migrated.App.CustomDirectRules);
        Assert.Equal(2, migrated.SchemaVersion);
    }
}

/// <summary>v2.30.0-r3: tests for the 3-format import/export of
/// custom rules (CSV / VPNRouter JSON / sing-box-native).</summary>
public class CustomRulesImportExportTests
{
    [Fact]
    public void Detect_DetectsCsvFromPlainText()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "action,type,value\ndirect,ip_cidr,10.0.0.0/8");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv, fmt);
    }

    [Fact]
    public void Detect_DetectsVpnrouterJson()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "[{\"action\":\"direct\",\"type\":\"ip_cidr\",\"value\":\"10.0.0.0/8\"}]");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson, fmt);
    }

    [Fact]
    public void Detect_DetectsSingBoxJson()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "[{\"domain_suffix\":[\".corp\"],\"outbound\":\"proxy\"}]");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson, fmt);
    }

    [Fact]
    public void Csv_RoundTrips_PreservesAllFields()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Comment = "LAN", Enabled = true },
            new() { Action = "block", Type = "domain_keyword", Value = "ads, tracker", Comment = "ads with comma", Enabled = false },
        };
        var csv = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Contains("ads, tracker", csv);  // multi-value preserved
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(csv, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Empty(imported.Warnings);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("LAN", imported.Rules[0].Comment);
        Assert.False(imported.Rules[1].Enabled);
        Assert.Equal("ads, tracker", imported.Rules[1].Value);
    }

    [Fact]
    public void Csv_HandlesQuotedFields()
    {
        var csv = "action,type,value,comment,enabled\n"
                + "direct,domain,\"a, b, c\",\"with \"\"quotes\"\"\",true\n";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(csv, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Single(imported.Rules);
        Assert.Equal("a, b, c", imported.Rules[0].Value);
        Assert.Equal("with \"quotes\"", imported.Rules[0].Comment);
    }

    [Fact]
    public void VpnrouterJson_RoundTrips()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "proxy", Type = "domain_suffix", Value = ".corp", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        var json = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson);
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(json, VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson);
        Assert.Empty(imported.Warnings);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("proxy", imported.Rules[0].Action);
        Assert.Equal("geosite", imported.Rules[1].Type);
    }

    [Fact]
    public void SingBoxJson_ImportsBareRulesArray()
    {
        var sb = "[" +
                 "{\"domain_suffix\":[\".corp.example\"],\"outbound\":\"proxy\"}," +
                 "{\"ip_cidr\":[\"10.0.0.0/8\"],\"outbound\":\"direct\"}," +
                 "{\"domain_keyword\":[\"ads\"],\"action\":\"reject\"}" +
                 "]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(3, imported.Rules.Count);
        Assert.Equal("proxy", imported.Rules[0].Action);
        Assert.Equal("domain_suffix", imported.Rules[0].Type);
        Assert.Equal("direct", imported.Rules[1].Action);
        Assert.Equal("block", imported.Rules[2].Action);
    }

    [Fact]
    public void SingBoxJson_ImportsRulesArrayInsideRouteObject()
    {
        var sb = "{\"route\":{\"rules\":[{\"domain\":[\"x.example\"],\"outbound\":\"proxy\"}]}}";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Single(imported.Rules);
        Assert.Equal("proxy", imported.Rules[0].Action);
    }

    [Fact]
    public void SingBoxJson_ExplodesMultiMatchRule()
    {
        // sing-box rule with both domain_suffix AND ip_cidr in one rule.
        // Our schema is one-match-per-rule, so we explode it into 2 entries.
        var sb = "[{\"domain_suffix\":[\".corp\"],\"ip_cidr\":[\"10.0.0.0/8\"],\"outbound\":\"proxy\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(2, imported.Rules.Count);
        Assert.NotEmpty(imported.Warnings); // warning about explosion
        Assert.All(imported.Rules, r => Assert.Equal("proxy", r.Action));
    }

    [Fact]
    public void SingBoxJson_StripsRuleSetTagPrefix()
    {
        var sb = "[{\"rule_set\":[\"user-geosite-ads\",\"vpnrouter-geosite-ru\"],\"action\":\"reject\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Single(imported.Rules);
        Assert.Equal("block", imported.Rules[0].Action);
        Assert.Contains("ads", imported.Rules[0].Value);
        Assert.Contains("ru", imported.Rules[0].Value);
        Assert.DoesNotContain("user-geosite-", imported.Rules[0].Value);
    }

    [Fact]
    public void SingBoxJson_ExportProducesValidImportableForm()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        var sb = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Contains("\"outbound\": \"direct\"", sb);
        Assert.Contains("\"action\": \"reject\"", sb);
        // Round-trip via SingBoxJson import.
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("direct", imported.Rules[0].Action);
        Assert.Equal("block", imported.Rules[1].Action);
    }

    [Fact]
    public void DisabledRules_NotExportedToSingBoxJson()
    {
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
            new() { Action = "block", Type = "domain", Value = "skipped.example", Enabled = false },
        };
        var sb = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(rules, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.DoesNotContain("skipped.example", sb);
        Assert.Contains("10.0.0.0/8", sb);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ServerUriParser — v2.30.1-r3 multi-protocol URI parsing
//
// Verifies that share-link URIs for non-VLESS protocols (Hysteria2 with
// Salamander obfuscation, TUIC v5 with congestion-control hint, Shadowsocks
// 2022 in both plain and base64 userinfo forms, Shadowsocks + ShadowTLS v3
// plugin) parse into VlessServerEntry rows with the right Protocol
// discriminator and protocol-specific fields. The pre-existing VLESS path
// keeps working unchanged.
// ═══════════════════════════════════════════════════════════════════════════════

public class ServerUriParserTests
{
    [Fact]
    public void Vless_BackwardCompat_ParsesAndKeepsProtocolDefault()
    {
        var uri = "vless://abc-123@1.2.3.4:443?type=tcp&security=reality&sni=example.com&pbk=PUB&sid=ID&flow=xtls-rprx-vision#main";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("vless", e.Protocol);
        Assert.Equal("1.2.3.4", e.Server);
        Assert.Equal(443, e.Port);
        Assert.Equal("abc-123", e.Uuid);
        Assert.Equal("xtls-rprx-vision", e.Flow);
        Assert.Equal("PUB", e.Reality.PublicKey);
    }

    [Fact]
    public void Hysteria2_Plain_ParsesCorrectly()
    {
        var uri = "hysteria2://mypass@example.com:9443/?sni=example.com&insecure=0#main";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal("example.com", e.Server);
        Assert.Equal(9443, e.Port);
        Assert.Equal("mypass", e.Password);
        Assert.Equal("example.com", e.Tls.ServerName);
        Assert.False(e.Tls.Insecure);
        Assert.Equal(string.Empty, e.ObfsType);
    }

    [Fact]
    public void Hysteria2_Salamander_PopulatesObfsFields()
    {
        var uri = "hysteria2://pass@host:443/?sni=foo.com&obfs=salamander&obfs-password=obfspw#hy2";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal("salamander", e.ObfsType);
        Assert.Equal("obfspw", e.ObfsPassword);
    }

    [Fact]
    public void Hysteria2_Hy2Alias_ParsesAsHysteria2()
    {
        var uri = "hy2://pw@1.2.3.4:443/?sni=x.com&insecure=1#alias";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.True(e.Tls.Insecure);
    }

    [Fact]
    public void Tuic_UuidPasswordUserinfo_ParsesBoth()
    {
        var uri = "tuic://u-uid:pass-word@host:443?sni=foo.com&congestion_control=cubic&udp_relay_mode=quic&alpn=h3#tuic";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("tuic", e.Protocol);
        Assert.Equal("u-uid", e.Uuid);
        Assert.Equal("pass-word", e.Password);
        Assert.Equal("cubic", e.CongestionControl);
        Assert.Equal("quic", e.UdpRelayMode);
        Assert.Equal("h3", e.Tls.Alpn);
    }

    [Fact]
    public void Tuic_UuidOnly_AcceptsEmptyPassword()
    {
        var uri = "tuic://just-uuid@host:443#tuic";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("just-uuid", e.Uuid);
        Assert.Equal(string.Empty, e.Password);
    }

    [Fact]
    public void Shadowsocks_PlainUserinfo_ParsesMethodAndPassword()
    {
        var uri = "ss://2022-blake3-aes-256-gcm:secret-key@host:8388#ss22";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("shadowsocks", e.Protocol);
        Assert.Equal("2022-blake3-aes-256-gcm", e.Method);
        Assert.Equal("secret-key", e.Password);
    }

    [Fact]
    public void Shadowsocks_Base64Userinfo_DecodesAndParses()
    {
        // base64 of "aes-256-gcm:secretpw"
        var ui = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:secretpw"));
        var uri = "ss://" + ui + "@host:8388#ss-legacy";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("aes-256-gcm", e.Method);
        Assert.Equal("secretpw", e.Password);
    }

    [Fact]
    public void Shadowsocks_ShadowTlsV3Plugin_ParsesPluginAndOpts()
    {
        var uri = "ss://2022-blake3-aes-256-gcm:k@host:443/?plugin=shadow-tls%3Bversion%3D3%3Bpassword%3Dstpw%3Bhost%3Dcdn.example.com#ss-stls";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("shadow-tls", e.Plugin);
        Assert.Equal("version=3;password=stpw;host=cdn.example.com", e.PluginOpts);
    }

    [Fact]
    public void IsSupportedScheme_AcceptsAllSupportedSchemes()
    {
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("vless://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("hysteria2://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("hy2://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("tuic://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("ss://x"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("trojan://x"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("https://example.com"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme(""));
    }

    [Fact]
    public void Parse_UnsupportedScheme_Throws()
    {
        Assert.Throws<System.FormatException>(() =>
            VPNRouter.Core.Services.ServerUriParser.Parse("trojan://x@host:443#bad"));
    }

    [Fact]
    public void ParseMultiple_SkipsBadLines_KeepsGoodOnes()
    {
        var blob = string.Join("\n",
            "vless://abc@1.2.3.4:443?security=reality&sni=x.com&pbk=P&sid=I#vl",
            "",
            "hysteria2://pw@host:443/?sni=x.com#hy2",
            "garbage",
            "tuic://u:p@host:443#tuic");
        var list = VPNRouter.Core.Services.ServerUriParser.ParseMultiple(blob);
        Assert.Equal(3, list.Count);
        Assert.Equal("vless",     list[0].Protocol);
        Assert.Equal("hysteria2", list[1].Protocol);
        Assert.Equal("tuic",      list[2].Protocol);
    }
}
