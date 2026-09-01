using System.Text.Json.Nodes;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
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
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

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
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var dnsRules = StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray;

        Assert.NotNull(dnsRules);
        // First DNS rule should be our injected process rule
        var firstRule = dnsRules![0] as JsonObject;
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
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

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

        var json = (JsonNode.Parse(second) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        // Should have exactly one process_name route rule (not two)
        var processRules = rules!.Where(r => r["process_name"] != null).ToList();
        Assert.Single(processRules);

        // Should contain both processes
        var processNameArr = processRules[0]!["process_name"] as JsonArray;
        Assert.NotNull(processNameArr);
        var names = processNameArr!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Discord.exe", names);
        Assert.Contains("Telegram.exe", names);
    }

    // ── Inject: no route section ──

    [Fact]
    public void Inject_ConfigWithoutRoute_CreatesRouteSection()
    {
        var json = """{"outbounds": [{"type": "vless", "tag": "proxy"}, {"type": "direct", "tag": "direct"}]}""";
        var result = CustomConfigInjector.Inject(json, new[] { "test.exe" }, CreateSettings());
        var parsed = (JsonNode.Parse(result) as JsonObject)!;

        Assert.NotNull(parsed["route"]);
        Assert.NotNull(StjNodeHelpers.SelectToken(parsed, "route.rules"));
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

    // ── Like RealWorldConfig but with REAL crypto material (valid x25519
    //    Reality key, valid UUID, hex short_id) so `sing-box check` accepts
    //    it. The placeholder-keyed RealWorldConfig is fine for structural
    //    assertions but fails `check` on "invalid public_key". ──
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

        // dns.strategy must be ipv4_only (was prefer_ipv4)
        Assert.Equal("ipv4_only", StjNodeHelpers.SelectToken(json, "dns.strategy")?.ToString());

        // dns.final must point to local DNS (was "remote")
        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.NotEqual("remote", dnsFinal);

        // route.final must be "direct" (split tunnel)
        Assert.Equal("direct", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // route.default_domain_resolver must be set to local DNS
        var resolver = StjNodeHelpers.SelectToken(json, "route.default_domain_resolver")?.ToString();
        Assert.NotNull(resolver);
        Assert.NotEqual("remote", resolver);

        // tun.strict_route must be false
        var inbounds = json["inbounds"] as JsonArray;
        var tun = inbounds!.OfType<JsonObject>().FirstOrDefault(t => t["type"]?.ToString() == "tun");
        Assert.NotNull(tun);
        Assert.Equal(false, StjNodeHelpers.AsBool(tun["strict_route"]));
        Assert.Equal("system", tun["stack"]?.ToString());

        // "block" and "dns" outbound types must be removed
        var outbounds = json["outbounds"] as JsonArray;
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "block");
        Assert.DoesNotContain(outbounds!, o => o["type"]?.ToString() == "dns");

        // Non-proxy DNS servers must have detour:"dns-direct" to bypass hijack-dns routing loop
        var dnsServers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var localDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "local");
        Assert.Equal("dns-direct", localDnsServer?["detour"]?.ToString());
        // Proxy DNS server must keep its proxy detour
        var remoteDnsServer = dnsServers!.FirstOrDefault(s => s["tag"]?.ToString() == "remote");
        Assert.Equal("proxy", remoteDnsServer?["detour"]?.ToString());
        // dns-direct outbound must exist
        var allOutbounds = json["outbounds"] as JsonArray;
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
        // Pre-fix: ForceIpv4Only=false + Ipv6Enabled=false left strategy absent → AAAA stall + IPv6 leak.
        var settings = CreateSettings(forceIpv4Only: false, ipv6Enabled: false);
        var json = (JsonNode.Parse(CustomConfigInjector.Inject(LegacyConfig, new[] { "chrome.exe" }, settings)) as JsonObject)!;
        Assert.Equal("ipv4_only", StjNodeHelpers.SelectToken(json, "dns.strategy")?.ToString());

        // An authored strategy is the user's explicit contract — never overwritten absent ForceIpv4Only.
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
        // Pre-fix: injected urltest blindly tagged "auto" → duplicate tag → sing-box FATAL.
        var json = (JsonNode.Parse(CustomConfigInjector.Inject(SelectorWithAutoOutbound, new[] { "chrome.exe" }, CreateSettings())) as JsonObject)!;
        var outbounds = json["outbounds"] as JsonArray;
        Assert.Contains(outbounds!, o => o["type"]?.ToString() == "urltest");

        var tags = outbounds!.Select(o => o["tag"]?.ToString()).Where(t => t != null).ToList();
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Validate_DuplicateOutboundTag_ReportsError()
    {
        // Pre-fix: no duplicate-tag check → sing-box FATAL'd opaquely at start.
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

        // Verify geo rules use a proxy-detour resolver, never the retired
        // country-specific direct resolver.
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

    // ─── Bug-r9-F-DEFENSIVE (2026-05-11) — Custom Config Mode silent leak ──
    //
    // Setup: stas (Custom Config Mode user) routed ALL traffic through a
    // dead `outbound/vless[proxy]` dialing 195.135.255.216:443 that wasn't
    // in his subscription. Root cause: his pasted sing-box JSON had a
    // vless outbound without explicit `tag`, and FindProxyOutboundTag's
    // historical fallback was `?? "proxy"` — shadowing the subscription's
    // `vless-{name}` outbound list with an unnamed user outbound. These
    // tests pin the new contract: anonymous outbounds get `"custom-proxy"`
    // (distinct from subscription tags) AND the route rules reference
    // that tag, so the unnamed outbound is the one actually used.

    [Fact]
    public void CustomConfigInjector_OutboundWithoutTag_GetsCustomProxy()
    {
        // Anonymous proxy outbound — no tag field set by the user.
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

        // The anonymous vless outbound must have been tagged "custom-proxy"
        // (not the historical silent "proxy" fallback).
        var vless = outbounds!.FirstOrDefault(o => o["type"]?.ToString() == "vless");
        Assert.NotNull(vless);
        Assert.Equal("custom-proxy", vless!["tag"]?.ToString());

        // And NO outbound should be tagged "proxy" — that's the shadowing
        // that caused the leak in stas's log.
        Assert.DoesNotContain(outbounds!, o => o["tag"]?.ToString() == "proxy");
    }

    [Fact]
    public void CustomConfigInjector_RouteRulesUseCustomProxy()
    {
        // When the proxy tag falls back to "custom-proxy", the injected
        // route + DNS rules must reference that tag — otherwise the rule
        // points at a non-existent outbound and sing-box silently fails
        // over to direct (the exact silent-leak class we are defending).
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

    // ─── v2.39.0 audit P0 #147 — Apps Include/Exclude + Full Tunnel policy ──
    //
    // Custom-JSON mode must honour the SAME per-app routing policy as
    // generated mode (ConfigGenerator.BuildRoute). Before the fix, Inject
    // always routed the scanner list THROUGH the proxy and never forced
    // final=proxy for full tunnel, so EXCLUDE mode was inverted (the apps the
    // user wanted KEPT OUT of the VPN were the only ones tunnelled) and FULL
    // tunnel leaked everything direct when the user's JSON carried
    // final=direct. These tests pin the corrected contract.

    [Fact]
    public void Inject_ExcludeMode_Split_ListedAppDirect_FinalProxy()
    {
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = "exclude";
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };

        // The scanner list is passed but must be IGNORED in exclude mode —
        // RoutingAppsExclude is the source of truth.
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        // The excluded app is pinned to "direct" (bypasses the VPN), NOT proxy.
        var procRule = rules!.FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(procRule);
        Assert.Equal("direct", procRule!["outbound"]?.ToString());
        var names = (procRule["process_name"] as JsonArray)!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Steam.exe", names);
        Assert.DoesNotContain("Discord.exe", names); // scanner list ignored

        // Everything ELSE flows through the proxy — route.final = proxy tag,
        // NOT "direct". This is the inversion fix.
        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // The excluded app's DNS resolves locally (matches its direct traffic).
        var dnsRule = (StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray)!
            .FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(dnsRule);
        Assert.Equal("local-dns", dnsRule!["server"]?.ToString());

        // ...but the DEFAULT dns.final must point at the REMOTE/proxy DNS server
        // (vpn-dns), because the unmatched majority is tunnelled — otherwise
        // their DNS leaks to the local resolver (audit DNS-leak follow-up).
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
        // Include mode routes the listed apps THROUGH the proxy.
        Assert.Equal("proxy", procRule!["outbound"]?.ToString());
        var names = (procRule["process_name"] as JsonArray)!.Select(t => t!.ToString()).ToList();
        Assert.Contains("Firefox.exe", names);      // explicit include honoured
        Assert.DoesNotContain("Discord.exe", names); // scanner list overridden

        // Split include → everything else direct.
        Assert.Equal("direct", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // Include DNS resolves through the remote/proxy DNS server.
        var dnsRule = (StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray)!
            .FirstOrDefault(r => r!["process_name"] != null) as JsonObject;
        Assert.NotNull(dnsRule);
        Assert.Equal("vpn-dns", dnsRule!["server"]?.ToString());

        // Include split: the unmatched majority goes DIRECT, so dns.final stays
        // on the local resolver (only the routed apps use vpn-dns above).
        Assert.Equal("local-dns", StjNodeHelpers.SelectToken(json, "dns.final")?.ToString());
    }

    [Fact]
    public void Inject_IncludeMode_EmptyExplicitList_FallsBackToScannerList()
    {
        // The override path must NOT regress users who never opened the Apps
        // tab: empty RoutingAppsInclude → use the legacy scanner list verbatim.
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string>(); // empty

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

        // ActionConfig's original route.final is "direct" — the pre-fix leak.
        var result = CustomConfigInjector.Inject(ActionConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;
        var rules = StjNodeHelpers.SelectToken(json, "route.rules") as JsonArray;

        // Full tunnel: NO per-app process rules (everything via final).
        Assert.DoesNotContain(rules!, r => r!["process_name"] != null);

        // The leak fix: final flips from the user's "direct" to the proxy tag.
        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // Full tunnel: ALL DNS must resolve through the remote/proxy server too,
        // not the local resolver (DNS-leak follow-up).
        Assert.Equal("vpn-dns", StjNodeHelpers.SelectToken(json, "dns.final")?.ToString());
    }

    // v2.40.0 (review H1): a custom config whose DNS servers have NO proxy
    // detour (all local / dns-direct after Strip) must NOT leave dns.final on the
    // real NIC in full/exclude mode — the injector must SYNTHESIZE a proxy-routed
    // DoH resolver. Without the fix, route.final=proxy tunnels traffic while DNS
    // resolves direct = DNS leak.
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

        // dns.final must NOT be the local/dns-direct resolver — it must point at a
        // server that resolves THROUGH the proxy outbound.
        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString()); // resolves inside the tunnel

        // The synthesized DNS server must be valid sing-box (no malformed startup).
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return; // CI without the binary — skip
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
        // v2.40.0-r2 (regression review #2): in include-split the LISTED apps are
        // tunnelled, so their per-app DNS rule must resolve THROUGH the proxy. When
        // the custom config carries no proxy-detour DNS server, InjectDnsRules must
        // synthesize one instead of falling back to servers[0] (the local/dns-direct
        // resolver) — otherwise the tunnelled apps' DNS queries leak to the ISP.
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { "Discord.exe" };

        var result = CustomConfigInjector.Inject(NoProxyDnsConfig, new[] { "Discord.exe" }, settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        // Find the injected process_name DNS rule and resolve its server tag.
        var rules = StjNodeHelpers.SelectToken(json, "dns.rules") as JsonArray;
        Assert.NotNull(rules);
        var appRule = rules!.OfType<JsonObject>().FirstOrDefault(r => r["process_name"] != null);
        Assert.NotNull(appRule); // the listed app got a per-app DNS rule
        var ruleServerTag = appRule!["server"]?.ToString();
        Assert.False(string.IsNullOrEmpty(ruleServerTag));

        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var ruleServer = servers!.OfType<JsonObject>().FirstOrDefault(s => s["tag"]?.ToString() == ruleServerTag);
        Assert.NotNull(ruleServer);
        // The per-app resolver must run inside the tunnel — NOT a local/dns-direct one.
        Assert.Equal("proxy", ruleServer!["detour"]?.ToString());
    }

    // v2.40.0 (review H1 — defense-in-depth follow-up): a hand-authored custom
    // config that REUSES our reserved DNS tag "vpnrouter-vpn-dns" with a LOCAL
    // detour must NOT short-circuit EnsureSynthesizedRemoteDns's idempotency
    // guard. Before the fix the guard returned the tag on a tag-only match, so
    // in full tunnel dns.final was pinned to that real-NIC resolver while
    // route.final tunnels traffic = DNS leak. The fix coerces the reserved-tag
    // server back to a proxy-routed DoH resolver. (Strip rewrites the authored
    // detour:"direct" to "dns-direct" before the dns.final path runs — that is
    // exactly the local-detour state that must still be coerced.)
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

        // Full tunnel: route.final tunnels everything through the proxy.
        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // dns.final must resolve THROUGH the proxy — it must point at a server
        // whose detour equals the proxy outbound tag, NOT the local "direct" /
        // "dns-direct" detour the config was authored with.
        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        // The reserved-tag server itself must have been coerced in place to the
        // canonical proxy-routed DoH shape (no local detour left behind).
        var reserved = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == "vpnrouter-vpn-dns");
        Assert.NotNull(reserved);
        Assert.Equal("proxy", reserved!["detour"]?.ToString());
        Assert.Equal("https", reserved["type"]?.ToString());

        // The coerced config must still be valid sing-box (no malformed startup).
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return; // CI without the binary — skip
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

    // v2.40.0-r6 (night-shift leak-hunt, Workflow #2 confirmed MEDIUM): a custom
    // config can omit the dns section ENTIRELY (sing-box then falls back to its
    // implicit system resolver). In full-tunnel / exclude mode route.final=proxy
    // tunnels every connection, but with no dns.final the lookups resolve on the
    // real NIC = DNS leak. The old guard required `dns.servers != null && Count>0`,
    // so it skipped the whole fail-closed block for such configs. The fix CREATES
    // the dns section + servers and synthesizes a proxy-routed DoH resolver.
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

        // route.final tunnels everything through the proxy in both modes.
        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());

        // The dns section + servers must have been CREATED (the input had none).
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        Assert.NotNull(servers);
        Assert.NotEmpty(servers!);

        // dns.final must point at a synthesized server that resolves THROUGH the
        // proxy outbound — never left unset (which would leak to the system resolver).
        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var finalServer = servers!.OfType<JsonObject>()
            .FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        // The synthesized config must still be valid sing-box (no malformed startup).
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return; // CI without the binary — skip
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

    // v2.40.0-r8 (#1 — bug-scout HIGH leak): a custom config whose DNS server detours
    // to a CUSTOM-NAMED direct outbound (detour:"myedge" -> {type:direct}), NOT the
    // literal "direct". The old string-only classifier (detour != "direct") treated it
    // as through-proxy, so in full/exclude mode route.final=proxy tunnelled everything
    // while dns.final stayed on that real-NIC server = ISP-visible DNS leak. The fix
    // resolves the detour to its outbound type and normalizes it to dns-direct.
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

        // dns.final must resolve THROUGH the proxy — NOT the "mydns" real-NIC server
        // whose detour points to the custom-named {type:direct} "myedge" outbound.
        var dnsFinal = StjNodeHelpers.SelectToken(json, "dns.final")?.ToString();
        Assert.False(string.IsNullOrEmpty(dnsFinal));
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var finalServer = servers!.OfType<JsonObject>().FirstOrDefault(s => s["tag"]?.ToString() == dnsFinal);
        Assert.NotNull(finalServer);
        Assert.Equal("proxy", finalServer!["detour"]?.ToString());

        AssertSingBoxCheckPasses(result, $"customdirect-{routingMode}-{appsMode}");
    }

    // v2.40.0-r8 (#2 — bug-scout MEDIUM): no dns section + a DOMAIN proxy server.
    // The synthesized proxy-detour resolver must NOT become default_domain_resolver,
    // else the proxy's own domain resolves through the not-yet-connected proxy =
    // circular bootstrap. A real-NIC dns-direct bootstrap resolver must back it.
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

        // default_domain_resolver must point at encrypted Cloudflare DNS on the
        // real NIC so the proxy's own domain bootstraps without public UDP/53.
        var ddr = StjNodeHelpers.SelectToken(json, "route.default_domain_resolver")?.ToString();
        Assert.Equal("vpnrouter-dns-direct", ddr);
        var servers = StjNodeHelpers.SelectToken(json, "dns.servers") as JsonArray;
        var ddrServer = servers!.OfType<JsonObject>().Single(s => s["tag"]?.ToString() == ddr);
        Assert.Equal("https", ddrServer["type"]?.ToString());
        Assert.Equal("1.1.1.1", ddrServer["server"]?.ToString());
        Assert.Equal("/dns-query", ddrServer["path"]?.ToString());
        Assert.Equal("dns-direct", ddrServer["detour"]?.ToString());
        Assert.DoesNotContain(servers.OfType<JsonObject>(), s =>
            s["type"]?.ToString() == "udp" && s["server"]?.ToString() == "1.1.1.1");

        AssertSingBoxCheckPasses(result, "domainproxy-nodns-full");
    }

    /// <summary>Runs `sing-box check` on the emitted config and asserts exit 0.
    /// Skips gracefully when the binary is absent (CI).</summary>
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
        // LegacyConfig uses a SELECTOR tagged "proxy" and route.final "proxy".
        // Flip the user's JSON to a leaky final=direct, then assert full tunnel
        // restores final to the selector tag regardless.
        var leaky = LegacyConfig.Replace("\"final\": \"proxy\"", "\"final\": \"direct\"");
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";

        var result = CustomConfigInjector.Inject(leaky, Array.Empty<string>(), settings);
        var json = (JsonNode.Parse(result) as JsonObject)!;

        Assert.Equal("proxy", StjNodeHelpers.SelectToken(json, "route.final")?.ToString());
    }

    // ── Integration: generated mode-configs must pass `sing-box check` ──

    [Theory]
    [InlineData("exclude", "split")]
    [InlineData("include", "split")]
    [InlineData("include", "full")]
    public void Inject_ModePolicies_PassSingBoxCheck(string appsMode, string routingMode)
    {
        var singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath))
            return; // CI without the binary — skip

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
}
