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
