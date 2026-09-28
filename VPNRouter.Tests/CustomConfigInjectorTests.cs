using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class CustomConfigInjectorTests
{
    private static AppSettings CreateSettings() => new()
    {
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" }
    };

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

    [Fact]
    public void Inject_LegacyConfig_FindsSelectorTag()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
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

    [Fact]
    public void Inject_LegacyConfig_NoActionField()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "test.exe" }, CreateSettings());
        Assert.Contains("\"process_name\"", result);
    }

    [Fact]
    public void Inject_ActionConfig_HasActionField()
    {
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "test.exe" }, CreateSettings());
        Assert.Contains("\"action\": \"route\"", result);
    }

    [Fact]
    public void Inject_LegacyConfig_ProcessRuleAfterSystemRules()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        Assert.NotNull(rules);
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

    [Fact]
    public void Inject_InjectsDnsRuleForRemoteServer()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, new[] { "Discord.exe" }, CreateSettings());
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var dnsRules = StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray;

        Assert.NotNull(dnsRules);
        var firstRule = dnsRules![0] as JsonObject;
        Assert.NotNull(firstRule!["process_name"]);
        Assert.Equal("remote", firstRule["server"]?.ToString());
    }

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

    [Fact]
    public void Inject_EmptyProcesses_NoProcessRulesAdded()
    {
        var result = CustomConfigInjector.Inject(LegacyConfig, Array.Empty<string>(), CreateSettings());
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        foreach (var rule in rules!)
        {
            Assert.Null(rule["process_name"]);
        }
    }

    [Fact]
    public void Inject_FiltersWildcardProcesses()
    {
        var result = CustomConfigInjector.Inject(ActionConfig,
            new[] { "Discord.exe", "chrome*", "fire?.exe" }, CreateSettings());
        Assert.Contains("Discord.exe", result);
        Assert.DoesNotContain("chrome*", result);
        Assert.DoesNotContain("fire?", result);
    }

    [Fact]
    public void Inject_IdempotentReinjection()
    {
        var settings = CreateSettings();
        var first = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var second = CustomConfigInjector.Inject(first, new[] { "Discord.exe", "Telegram.exe" }, settings);

        var json = (JsonNode.Parse(second) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        var processRules = rules!.Where(r => r["process_name"] != null).ToList();
        Assert.Single(processRules);

        var processNameArr = processRules[0]!["process_name"] as JsonArray;
        Assert.NotNull(processNameArr);
        var names = processNameArr!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Discord.exe", names);
        Assert.Contains("Telegram.exe", names);
    }

    [Fact]
    public void Inject_ConfigWithoutRoute_CreatesRouteSection()
    {
        var json = """{"outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""";
        var result = CustomConfigInjector.Inject(json, new[] { "test.exe" }, CreateSettings());
        var parsed = (JsonNode.Parse(result) as JsonObject)!;

        Assert.NotNull(parsed["route"]);
        Assert.NotNull(StjNodeHelpers.SelectToken(parsed, "route.rules"));
    }

    [Fact]
    public void Inject_PreservesProcessNameCase()
    {
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe", "Telegram.exe" }, CreateSettings());
        Assert.Contains("Discord.exe", result);
        Assert.Contains("Telegram.exe", result);
    }

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

    private const string CheckableConfig = """
    {
      "dns": {
        "servers": [
          {"tag": "remote", "address": "tls://1.1.1.1", "detour": "proxy"},
          {"tag": "local", "address": "223.5.5.5", "detour": "direct"}
        ],
        "rules": [],
        "final": "remote",
        "strategy": "prefer_ipv4"
      },
      "inbounds": [{
        "type": "tun", "auto_route": true, "strict_route": true,
        "address": ["172.19.0.1/30"]
      }],
      "outbounds": [
        {"tag": "proxy", "type": "selector", "outbounds": ["vless-reality", "tuic-v5"]},
        {"tag": "vless-reality", "type": "vless", "server": "1.2.3.4", "server_port": 443,
         "uuid": "c947ffd3-d5eb-4888-a54e-ba8fa05ff667", "flow": "xtls-rprx-vision",
         "tls": {"enabled": true, "server_name": "yahoo.com", "utls": {"enabled": true, "fingerprint": "chrome"},
                 "reality": {"enabled": true, "public_key": "hAk-08Tup5L1rQXLL7JwMCGYAM3tytE4S_3iOWD4lmE", "short_id": "0123456789abcdef"}}},
        {"tag": "tuic-v5", "type": "tuic", "server": "1.2.3.4", "server_port": 443,
         "uuid": "c947ffd3-d5eb-4888-a54e-ba8fa05ff667", "password": "testpass",
         "tls": {"enabled": true, "server_name": "yahoo.com"}},
        {"tag": "direct", "type": "direct"},
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
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("ipv4_only", StjNodeHelpers.SelectToken(json, "dns.strategy")?.ToString());

        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.NotEqual("remote", dnsFinal);

        Assert.Equal("direct", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var resolver = StjNodeHelpers.SelectToken(json, "route.default_domain_resolver")?.ToString();
        Assert.NotNull(resolver);
        Assert.NotEqual("remote", resolver);

        var inbounds = json["inbounds"] as JsonArray;
        var tun = inbounds!.OfType<JsonObject>().FirstOrDefault(t => t["type"]?.ToString() == "tun");
        Assert.NotNull(tun);
        Assert.Equal(false, StjNodeHelpers.AsBool(tun["strict_route"]));
        Assert.Equal("system", tun["stack"]?.ToString());

        var outbounds = json["outbounds"] as JsonArray;
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "block");
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "dns");

        var dnsServers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var localDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.Equal("dns-direct", localDnsServer?["detour"]?.ToString());
        var remoteDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("proxy", remoteDnsServer?["detour"]?.ToString());
        var allOutbounds = json["outbounds"] as JsonArray;
        var dnsDirect = allOutbounds!.FirstOrDefault(o => o["tag"]?.ToString() == "dns-direct");
        Assert.NotNull(dnsDirect);
        Assert.Equal("direct", dnsDirect!["type"]?.ToString());

        foreach (var s in dnsServers!)
            Assert.NotNull(s["type"]);

        var remoteDns = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("https", remoteDns?["type"]?.ToString());

        var localDns = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.NotNull(localDns);
        Assert.NotEqual("local", localDns!["type"]?.ToString());
        Assert.Equal("udp", localDns["type"]?.ToString());
    }

    private static AppSettings CreateSettings(bool forceIpv4Only, bool ipv6Enabled)
    {
        var s = CreateSettings();
        s.App.ForceIpv4Only = forceIpv4Only;
        s.Tun.Ipv6Enabled = ipv6Enabled;
        return s;
    }

    [Fact]
    public void IncludeSplit_Ipv6Disabled_ForcesIpv4OnlyDnsStrategy()
    {
        var settings = CreateSettings(forceIpv4Only: false, ipv6Enabled: false);
        var json = (JsonNode.Parse(CustomConfigInjector.Inject(LegacyConfig, new[] { "chrome.exe" }, settings)) as JsonObject)!;
        Assert.Equal("ipv4_only", StjNodeHelpers.SelectToken(json, "dns.strategy")?.ToString());

        var authored = (JsonNode.Parse(CustomConfigInjector.Inject(RealWorldConfig, new[] { "chrome.exe" }, settings)) as JsonObject)!;
        Assert.Equal("prefer_ipv4", StjNodeHelpers.SelectToken(authored, "dns.strategy")?.ToString());
    }

    private const string SelectorWithAutoOutbound = """
    {
      "outbounds": [
        {"type": "selector", "tag": "proxy", "outbounds": ["vless-a", "vless-b"]},
        {"type": "vless", "tag": "vless-a", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "vless", "tag": "vless-b", "server": "5.6.7.8", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "auto"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": {"rules": [], "final": "proxy"}
    }
    """;

    [Fact]
    public void EnsureUrltest_ExistingAutoOutbound_NoDuplicateTag()
    {
        var json = (JsonNode.Parse(CustomConfigInjector.Inject(SelectorWithAutoOutbound, new[] { "chrome.exe" }, CreateSettings())) as JsonObject)!;
        var outbounds = json["outbounds"] as JsonArray;
        Assert.Contains(outbounds!, o => o["type"]?.ToString() == "urltest");

        var tags = outbounds!.Select(o => o["tag"]?.ToString()).Where(t => t != null).ToList();
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Validate_DuplicateOutboundTag_ReportsError()
    {
        var json = """
        {
          "outbounds": [
            {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
            {"type": "direct", "tag": "proxy"}
          ]
        }
        """;

        var (isValid, errors) = CustomConfigInjector.Validate(json);

        Assert.False(isValid);
        Assert.Contains(errors, e => e.Contains("Duplicate outbound tag", StringComparison.Ordinal));
    }

    [Fact]
    public void Inject_ActualCustomConfig_SingBoxCheck()
    {
        var configPath = @"C:\ProgramData\VPNRouter\config\custom-brat-pc.json";
        if (!File.Exists(configPath))
            return;

        var rawJson = File.ReadAllText(configPath);
        var settings = CreateSettings();
        settings.Tun.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        var result = CustomConfigInjector.Inject(rawJson, new[] { "chrome.exe", "Discord.exe" }, settings);

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
        var configPath = @"C:\ProgramData\VPNRouter\config\custom-brat-pc.json";
        if (!File.Exists(configPath))
            return;

        if (!GeoDataDownloader.AreGeoFilesAvailable())
            return;

        var rawJson = File.ReadAllText(configPath);
        var settings = CreateSettings();
        settings.App.BypassRussianTraffic = true;
        settings.Tun.RouteExcludeAddress = new List<string> { "10.9.1.0/24" };
        var result = CustomConfigInjector.Inject(rawJson, new[] { "chrome.exe", "Discord.exe" }, settings);

        Assert.Contains("vpnrouter-geoip-ru", result);
        Assert.Contains("vpnrouter-geosite-ru", result);
        var parsed = (JsonNode.Parse(result) as JsonObject)!;
        var dnsServers = (StjNodeHelpers.SelectToken(parsed, "dns.servers") as JsonArray)!;
        Assert.DoesNotContain(dnsServers.OfType<JsonObject>(), s =>
            s["tag"]?.ToString() == "vpnrouter-dns-ru");
        var dnsRules = (StjNodeHelpers.SelectToken(parsed, "dns.rules") as JsonArray)!;
        var geoRule = dnsRules.OfType<JsonObject>().Single(r =>
            (r["rule_set"] as JsonArray)?.Any(rs =>
                rs?.ToString() == "vpnrouter-geosite-ru") == true);
        var targetTag = geoRule["server"]?.ToString();
        var targetDns = dnsServers.OfType<JsonObject>().Single(s =>
            s["tag"]?.ToString() == targetTag);
        Assert.False(string.IsNullOrWhiteSpace(targetDns["detour"]?.ToString()));
        Assert.NotEqual("direct", targetDns["detour"]?.ToString());
        Assert.NotEqual("dns-direct", targetDns["detour"]?.ToString());

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

    [Theory]
    [InlineData("existing-remote", "vpn-dns")]
    [InlineData("missing-remote", "vpnrouter-vpn-dns")]
    [InlineData("no-dns", "vpnrouter-vpn-dns")]
    public void Inject_GeoBypass_RemovesLegacyCountryDnsAndUsesProxyDns(
        string sourceMode,
        string expectedDnsTag)
    {
        var previousDataDir = AppPaths.DataDir;
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-geo-dns-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(tempDataDir);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, new byte[10 * 1024]);
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, new byte[100]);

            var source = (JsonNode.Parse(ActionConfig) as JsonObject)!;
            if (sourceMode == "no-dns")
            {
                source.Remove("dns");
            }
            else
            {
                var servers = (source["dns"]?["servers"] as JsonArray)!;
                if (sourceMode == "missing-remote")
                {
                    for (int i = servers.Count - 1; i >= 0; i--)
                        if ((servers[i] as JsonObject)?["tag"]?.ToString() == "vpn-dns")
                            servers.RemoveAt(i);
                }
                servers.Add((JsonNode?)new JsonObject
                {
                    ["tag"] = "vpnrouter-dns-ru",
                    ["type"] = "https",
                    ["server"] = "77.88.8.8",
                    ["path"] = "/dns-query",
                    ["tls"] = new JsonObject { ["server_name"] = "common.dot.dns.yandex.net" },
                    ["detour"] = "dns-direct"
                });
                ((source["dns"]?["rules"] as JsonArray)!).Add((JsonNode?)new JsonObject
                {
                    ["rule_set"] = new JsonArray { (JsonNode?)JsonValue.Create("vpnrouter-geosite-ru") },
                    ["action"] = "route",
                    ["server"] = "vpnrouter-dns-ru"
                });
                ((JsonObject)source["dns"]!)["final"] = "vpnrouter-dns-ru";
            }

            var settings = CreateSettings();
            settings.App.BypassRussianTraffic = true;
            settings.App.RoutingAppsMode = "exclude";
            settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };
            var result = CustomConfigInjector.Inject(source.ToJsonString(), Array.Empty<string>(), settings);
            var parsed = (JsonNode.Parse(result) as JsonObject)!;
            var resultServers = (parsed["dns"]?["servers"] as JsonArray)!;
            var resultRules = (parsed["dns"]?["rules"] as JsonArray)!;

            Assert.DoesNotContain("77.88.8.8", result, StringComparison.Ordinal);
            Assert.DoesNotContain("common.dot.dns.yandex.net", result, StringComparison.Ordinal);
            Assert.DoesNotContain(resultServers.OfType<JsonObject>(), s =>
                s["tag"]?.ToString() == "vpnrouter-dns-ru");
            Assert.Equal(expectedDnsTag, parsed["dns"]?["final"]?.ToString());
            if (sourceMode != "no-dns")
            {
                Assert.Contains(resultServers.OfType<JsonObject>(), s =>
                    s["tag"]?.ToString() == "local-dns");
            }

            var geoRule = resultRules.OfType<JsonObject>().Single(r =>
                (r["rule_set"] as JsonArray)?.Any(rs =>
                    rs?.ToString() == "vpnrouter-geosite-ru") == true);
            Assert.Same(geoRule, resultRules[0]);
            Assert.Equal(expectedDnsTag, geoRule["server"]?.ToString());
            var targetDns = resultServers.OfType<JsonObject>().Single(s =>
                s["tag"]?.ToString() == expectedDnsTag);
            Assert.Equal("proxy", targetDns["detour"]?.ToString());

            var processRule = resultRules.OfType<JsonObject>().Single(r =>
                r["process_name"] is JsonArray);
            Assert.Equal(sourceMode == "no-dns" ? expectedDnsTag : "local-dns",
                processRule["server"]?.ToString());
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [Fact]
    public void CustomConfigInjector_OutboundWithoutTag_GetsCustomProxy()
    {
        var json = """
        {
          "outbounds": [
            {"type": "vless", "server": "1.2.3.4", "server_port": 443, "uuid": "x"},
            {"type": "direct", "tag": "direct"}
          ],
          "route": {"rules": [], "final": "direct"}
        }
        """;

        var result = CustomConfigInjector.Inject(json, new[] { "test.exe" }, CreateSettings());
        var parsed = (JsonNode.Parse(result) as JsonObject)!;
        var outbounds = parsed["outbounds"] as JsonArray;
        Assert.NotNull(outbounds);

        var vless = outbounds!.FirstOrDefault(o => o["type"]?.ToString() == "vless");
        Assert.NotNull(vless);
        Assert.Equal("custom-proxy", vless!["tag"]?.ToString());

        Assert.DoesNotContain(outbounds!, o => o["tag"]?.ToString() == "proxy");
    }

    [Fact]
    public void CustomConfigInjector_RouteRulesUseCustomProxy()
    {
        var json = """
        {
          "dns": {
            "servers": [
              {"tag": "remote", "type": "https", "server": "1.1.1.1", "detour": ""},
              {"tag": "local",  "type": "udp",   "server": "1.0.0.1"}
            ],
            "rules": []
          },
          "outbounds": [
            {"type": "vless", "server": "1.2.3.4", "server_port": 443, "uuid": "x"},
            {"type": "direct", "tag": "direct"}
          ],
          "route": {"rules": [], "final": "direct"}
        }
        """;

        var result = CustomConfigInjector.Inject(json, new[] { "Discord.exe" }, CreateSettings());
        var parsed = (JsonNode.Parse(result) as JsonObject)!;

        var routeRules = StjNodeHelpers.SelectToken(parsed, "route.rules") as JsonArray;
        Assert.NotNull(routeRules);
        var processRule = routeRules!.FirstOrDefault(r => r["process_name"] != null);
        Assert.NotNull(processRule);
        Assert.Equal("custom-proxy", processRule!["outbound"]?.ToString());
    }

    [Fact]
    public void Inject_ExcludeMode_Split_ListedAppDirect_FinalProxy()
    {
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = "exclude";
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        var procRule = rules!.FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(procRule);
        Assert.Equal("direct", procRule!["outbound"]?.ToString());
        var names = (procRule["process_name"] as JsonArray)!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Steam.exe", names);
        Assert.DoesNotContain("Discord.exe", names);

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var dnsRule = (StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray)!
            .FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(dnsRule);
        Assert.Equal("local-dns", dnsRule!["server"]?.ToString());

        Assert.Equal("vpn-dns", StjNodeHelpers.SelectToken(json, "dns.final")?.ToString());
    }

    [Fact]
    public void Inject_IncludeMode_ExplicitList_OverridesScannerList()
    {
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { "Firefox.exe" };

        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        var procRule = rules!.FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(procRule);
        Assert.Equal("proxy", procRule!["outbound"]?.ToString());
        var names = (procRule["process_name"] as JsonArray)!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Firefox.exe", names);
        Assert.DoesNotContain("Discord.exe", names);

        Assert.Equal("direct", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var dnsRule = (StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray)!
            .FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(dnsRule);
        Assert.Equal("vpn-dns", dnsRule!["server"]?.ToString());

        Assert.Equal("local-dns", StjNodeHelpers.SelectToken(json, "dns.final")?.ToString());
    }

    [Fact]
    public void Inject_IncludeMode_EmptyExplicitList_FallsBackToScannerList()
    {
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string>();

        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        var procRule = rules!.FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(procRule);
        var names = (procRule!["process_name"] as JsonArray)!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Discord.exe", names);
        Assert.Equal("proxy", procRule["outbound"]?.ToString());
        Assert.Equal("direct", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());
    }

    [Fact]
    public void Inject_FullTunnel_NoProcessRules_FinalProxy()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        Assert.DoesNotContain(rules!, r => r!["process_name"] != null);

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        Assert.Equal("vpn-dns", StjNodeHelpers.SelectToken(json, "dns.final")?.ToString());
    }

    private const string NoProxyDnsConfig = """
    {
      "dns": {
        "servers": [ {"tag": "local", "address": "223.5.5.5", "detour": "direct"} ],
        "rules": []
      },
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    [Theory]
    [InlineData("full", "include")]
    [InlineData("split", "exclude")]
    public void Inject_NoProxyDnsServer_SynthesizesRemoteDns_NoLeak(string routingMode, string appsMode)
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = appsMode;
        if (appsMode == "exclude")
            settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        var result = CustomConfigInjector.Inject(NoProxyDnsConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return;
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-h1-{routingMode}-{appsMode}-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath, Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed for synthesized DNS ({routingMode}/{appsMode}, exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    [Fact]
    public void Inject_IncludeSplit_NoProxyDns_PerAppDnsRuleResolvesThroughProxy()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { "Discord.exe" };

        var result = CustomConfigInjector.Inject(NoProxyDnsConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        var rules = StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray;
        Assert.NotNull(rules);
        var appRule = rules!.OfType<JsonObject>().FirstOrDefault(r => r["process_name"] != null);
        Assert.NotNull(appRule);
        var ruleServerTag = appRule!["server"]?.ToString();
        Assert.False(string.IsNullOrEmpty(ruleServerTag));

        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var ruleServer = servers!.OfType<JsonObject>().FirstOrDefault(s => s["tag"]?.ToString() == ruleServerTag);
        Assert.NotNull(ruleServer);
        Assert.Equal("proxy", ruleServer!["detour"]?.ToString());
    }

    private const string ReservedTagLocalDetourConfig = """
    {
      "dns": {
        "servers": [ {"tag": "vpnrouter-vpn-dns", "detour": "direct"} ],
        "rules": []
      },
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    [Fact]
    public void Inject_ReservedDnsTag_LocalDetour_FullTunnel_CoercedToProxy_NoLeak()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(ReservedTagLocalDetourConfig, Array.Empty<string>(), settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        var reserved = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == "vpnrouter-vpn-dns");
        Assert.NotNull(reserved);
        Assert.Equal("proxy", reserved!["detour"]?.ToString());
        Assert.Equal("https", reserved["type"]?.ToString());

        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return;
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-h1-reserved-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath, Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed for coerced reserved-tag DNS (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private const string NoDnsSectionConfig = """
    {
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    [Theory]
    [InlineData("full", "include")]
    [InlineData("split", "exclude")]
    public void Inject_NoDnsSection_FullOrExclude_SynthesizesRemoteDns_NoLeak(string routingMode, string appsMode)
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = appsMode;
        if (appsMode == "exclude")
            settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        var result = CustomConfigInjector.Inject(NoDnsSectionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        Assert.NotNull(servers);
        Assert.NotEmpty(servers!);

        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return;
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-nodns-{routingMode}-{appsMode}-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath, Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed for synthesized DNS ({routingMode}/{appsMode}, exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private const string CustomNamedDirectDnsConfig = """
    {
      "dns": {
        "servers": [ {"tag": "mydns", "address": "9.9.9.9", "detour": "myedge"} ],
        "rules": []
      },
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "myedge"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    [Theory]
    [InlineData("full", "include")]
    [InlineData("split", "exclude")]
    public void Inject_CustomNamedDirectDnsDetour_FullOrExclude_NoLeak(string routingMode, string appsMode)
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = routingMode;
        settings.App.RoutingAppsMode = appsMode;
        if (appsMode == "exclude")
            settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        var result = CustomConfigInjector.Inject(CustomNamedDirectDnsConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>().FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        AssertSingBoxCheckPasses(result, $"customdirect-{routingMode}-{appsMode}");
    }

    private const string DomainProxyNoDnsConfig = """
    {
      "outbounds": [
        {"type": "vless", "tag": "proxy", "server": "my.vpn.example.com", "server_port": 443, "uuid": "test"},
        {"type": "direct", "tag": "direct"}
      ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    [Fact]
    public void Inject_NoDnsSection_DomainProxy_FullTunnel_DomainResolverIsLocal_NoCircular()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(DomainProxyNoDnsConfig, Array.Empty<string>(), settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        var ddr = StjNodeHelpers.SelectToken(json, "route.default_domain_resolver")?.ToString();
        Assert.Equal("vpnrouter-dns-direct", ddr);
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var ddrServer = servers!.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == ddr);
        Assert.Equal("https", ddrServer["type"]?.ToString());
        Assert.Equal("8.8.8.8", ddrServer["server"]?.ToString());
        Assert.Equal("/dns-query", ddrServer["path"]?.ToString());
        Assert.Equal("dns-direct", ddrServer["detour"]?.ToString());
        Assert.DoesNotContain(servers.OfType<JsonObject>(), s =>
            s["type"]?.ToString() == "udp" && s["server"]?.ToString() == "8.8.8.8");

        AssertSingBoxCheckPasses(result, "domainproxy-nodns-full");
    }

    private static void AssertSingBoxCheckPasses(string configJson, string label)
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return;
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-{label}-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, configJson);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath, Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed ({label}, exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{configJson}");
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    [Fact]
    public void Inject_FullTunnel_OverridesUserFinalDirect_Selector()
    {
        var leaky = LegacyConfig.Replace("\"final\": \"proxy\"", "\"final\": \"direct\"");
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(leaky, Array.Empty<string>(), settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());
    }

    [Theory]
    [InlineData("exclude", "split")]
    [InlineData("include", "split")]
    [InlineData("include", "full")]
    public void Inject_ModePolicies_PassSingBoxCheck(string appsMode, string routingMode)
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return;

        var settings = CreateSettings();
        settings.App.RoutingAppsMode = appsMode;
        settings.App.RoutingMode = routingMode;
        if (appsMode == "exclude")
            settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };
        else
            settings.App.RoutingAppsInclude = new List<string> { "Firefox.exe" };

        var result = CustomConfigInjector.Inject(CheckableConfig, new[] { "chrome.exe" }, settings);

        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-mode-{appsMode}-{routingMode}-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, result);
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
                $"sing-box check failed for {appsMode}/{routingMode} (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{result}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Inject_MissingDnsHijackRule_InjectsHijackDnsAfterSniff()
    {
        var configJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy" }
          ],
          "route": {
            "rules": [
              { "action": "sniff" },
              { "ip_is_private": true, "outbound": "direct" }
            ]
          }
        }
        """;

        var settings = CreateSettings();
        var result = CustomConfigInjector.Inject(configJson, Array.Empty<string>(), settings);
        var doc = JsonDocument.Parse(result);
        var rules = doc.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToList();

        var hijackRule = rules.FirstOrDefault(r =>
            r.TryGetProperty("action", out var a) && a.GetString() == "hijack-dns");

        Assert.True(hijackRule.ValueKind != JsonValueKind.Undefined, "hijack-dns rule must be injected");
        Assert.Equal("dns", hijackRule.GetProperty("protocol").GetString());

        var sniffIdx = rules.FindIndex(r => r.TryGetProperty("action", out var a) && a.GetString() == "sniff");
        var hijackIdx = rules.FindIndex(r => r.TryGetProperty("action", out var a) && a.GetString() == "hijack-dns");
        Assert.True(hijackIdx > sniffIdx, "hijack-dns must be placed after sniff");
    }

    [Fact]
    public void Inject_FullTunnel_SanitizesUserDirectRules_PreventingBypass()
    {
        var configJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": {
            "rules": [
              { "action": "sniff" },
              { "ip_is_private": true, "outbound": "direct" },
              { "domain_suffix": ["leaked.com"], "outbound": "direct" }
            ],
            "final": "direct"
          }
        }
        """;

        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(configJson, Array.Empty<string>(), settings);
        var doc = JsonDocument.Parse(result);
        var route = doc.RootElement.GetProperty("route");

        Assert.Equal("proxy", route.GetProperty("final").GetString());

        var rules = route.GetProperty("rules").EnumerateArray().ToList();

        Assert.Contains(rules, r => r.TryGetProperty("ip_is_private", out var p) && p.GetBoolean());

        var leakedRule = rules.FirstOrDefault(r =>
            r.TryGetProperty("domain_suffix", out var d) && d.EnumerateArray().Any(s => s.GetString() == "leaked.com"));
        Assert.True(leakedRule.ValueKind == JsonValueKind.Undefined, "User direct rule must be sanitized in full tunnel");
    }
}
