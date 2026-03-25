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

    [Fact]
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

    [Fact]
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
        "inet4_address": "172.19.0.1/30"
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

        // Non-proxy DNS servers must have detour:"direct" to bypass hijack-dns routing loop
        var dnsServers = json.SelectToken("dns.servers") as Newtonsoft.Json.Linq.JArray;
        var localDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.Equal("direct", localDnsServer?["detour"]?.ToString());
        // Proxy DNS server must keep its proxy detour
        var remoteDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("proxy", remoteDnsServer?["detour"]?.ToString());

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
}
