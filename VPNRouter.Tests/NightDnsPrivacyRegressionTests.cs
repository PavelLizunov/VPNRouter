#nullable enable

using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Regression test suite for overnight audit findings NIGHT-02 and NIGHT-03:
/// <list type="bullet">
///   <item><description>NIGHT-02: Custom WireGuard/AmneziaWG endpoint split/include must preserve
///   wireguard detour in synthesized/resolved DNS servers and not rewrite them to dns-direct.</description></item>
///   <item><description>NIGHT-03: Effective StrictDns must override smart DNS and exclude local process rules,
///   routing all DNS queries through vpn-dns / proxy detour while preserving runtime strict override=false.</description></item>
/// </list>
/// </summary>
public class NightDnsPrivacyRegressionTests
{
    private static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = "include",
                StrictDns = false,
                BypassRussianTraffic = false
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "primary",
                        Server = "198.51.100.1",
                        Port = 443,
                        Uuid = "00000000-0000-0000-0000-000000000001",
                        Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            PublicKey = "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
                            ShortId = "abcd"
                        }
                    }
                }
            }
        };
    }

    private static Profile CreateProfile(string dnsMode = "vpn_only")
    {
        return new Profile
        {
            Name = "NightTestProfile",
            DnsMode = dnsMode,
            Processes = new List<ProcessRule>
            {
                new() { Name = "Firefox.exe", ScanPatterns = new[] { "Firefox.exe" } }
            }
        };
    }

    // Note: inbounds (including tun) are optional in injector unit tests; no real network required.
    private const string PlainWireGuardEndpointConfig = /*lang=json*/ """
    {
      "endpoints": [
        {
          "type": "wireguard",
          "tag": "wg",
          "address": ["10.0.0.2/32"],
          "private_key": "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=",
          "peers": [
            {
              "address": "198.51.100.10",
              "port": 51820,
              "public_key": "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
              "allowed_ips": ["0.0.0.0/0"]
            }
          ]
        }
      ],
      "outbounds": [
        { "type": "direct", "tag": "direct" },
        { "type": "direct", "tag": "dns-direct", "udp_fragment": true }
      ],
      "dns": {
        "servers": [
          { "tag": "local-https", "type": "https", "server": "1.1.1.1", "detour": "dns-direct" }
        ],
        "rules": []
      }
    }
    """;

    private const string VlessOutboundConfig = /*lang=json*/ """
    {
      "outbounds": [
        { "type": "vless", "tag": "proxy", "server": "198.51.100.1", "server_port": 443, "uuid": "00000000-0000-0000-0000-000000000001" },
        { "type": "direct", "tag": "direct" },
        { "type": "direct", "tag": "dns-direct", "udp_fragment": true }
      ],
      "dns": {
        "servers": [
          { "tag": "local-https", "type": "https", "server": "1.1.1.1", "detour": "dns-direct" }
        ],
        "rules": []
      }
    }
    """;

    // ═══════════════════════════════════════════════════════════════════════════
    // NIGHT-03: ConfigGenerator Matrix
    // strict on/off x include/exclude x smart/normal, runtime override
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    // Include mode combinations
    [InlineData("include", "smart", false, null, "local-dns", "dns-direct", "local-dns")]
    [InlineData("include", "smart", true, null, "vpn-dns", "proxy", "vpn-dns")] // NIGHT-03 core bug fix: strict overrides smart include
    [InlineData("include", "smart", true, false, "local-dns", "dns-direct", "local-dns")] // runtime override=false retained
    [InlineData("include", "vpn_only", false, null, "vpn-dns", "proxy", "local-dns")]
    [InlineData("include", "vpn_only", true, null, "vpn-dns", "proxy", "vpn-dns")]
    [InlineData("include", "vpn_only", true, false, "vpn-dns", "proxy", "local-dns")]
    // Exclude mode combinations
    [InlineData("exclude", "normal", false, null, "local-dns", "dns-direct", "vpn-dns")]
    [InlineData("exclude", "normal", true, null, "vpn-dns", "proxy", "vpn-dns")] // NIGHT-03 core bug fix: strict overrides exclude local rule
    [InlineData("exclude", "normal", true, false, "local-dns", "dns-direct", "vpn-dns")] // runtime override=false retained
    [InlineData("exclude", "smart", false, null, "local-dns", "dns-direct", "vpn-dns")]
    [InlineData("exclude", "smart", true, null, "vpn-dns", "proxy", "vpn-dns")] // strict overrides exclude smart
    [InlineData("exclude", "smart", true, false, "local-dns", "dns-direct", "vpn-dns")] // runtime override=false retained
    public void ConfigGenerator_Dns_StrictMatrix_SelectedServerAndDetourMatchContract(
        string appsMode,
        string dnsMode,
        bool strictDnsSetting,
        bool? strictDnsOverride,
        string expectedProcessServerTag,
        string expectedProcessDetour,
        string expectedFinalTag)
    {
        var settings = CreateSettings();
        settings.App.RoutingAppsMode = appsMode;
        settings.App.StrictDns = strictDnsSetting;

        if (appsMode == "exclude")
        {
            settings.App.RoutingAppsExclude = new List<string> { "Firefox.exe" };
        }
        else
        {
            settings.App.RoutingAppsInclude = new List<string> { "Firefox.exe" };
        }

        var profile = CreateProfile(dnsMode == "smart" ? "smart" : "vpn_only");
        var config = ConfigGenerator.Generate(
            profile,
            new[] { "Firefox.exe" },
            settings,
            strictDnsOverride: strictDnsOverride);

        // 1. Assert dns.final
        Assert.Equal(expectedFinalTag, config.Dns.Final);

        // 2. Assert process DNS rule
        var procRule = config.Dns.Rules.FirstOrDefault(r =>
            r.ProcessName != null && r.ProcessName.Contains("Firefox.exe"));
        Assert.NotNull(procRule);
        Assert.Equal(expectedProcessServerTag, procRule!.Server);

        // 3. Assert target resolver and its detour (not only final!)
        var resolverServer = config.Dns.Servers.Single(s => s.Tag == procRule.Server);
        Assert.Equal(expectedProcessDetour, resolverServer.Detour);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NIGHT-02: Full Inject with Plain WireGuard Endpoint
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomConfigInjector_FullInject_PlainWireGuardEndpoint_SplitInclude_DetourRemainsWg()
    {
        // NIGHT-02 specification:
        // Full Inject test must match process DNS rule->resolver->detour for valid WG endpoint-only
        // split/include, existing dns.servers local HTTPS, strict=false, BypassRu=false:
        // detour remains wg.
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.StrictDns = false;
        settings.App.BypassRussianTraffic = false;

        var injectedJson = CustomConfigInjector.Inject(
            PlainWireGuardEndpointConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();

        // 1. Process route rule routes to "wg"
        var routeRules = root["route"]?["rules"]?.AsArray();
        Assert.NotNull(routeRules);
        var procRoute = routeRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procRoute);
        Assert.Equal("wg", (string?)procRoute!["outbound"]);

        // 2. Process DNS rule routes to resolver
        var dnsRules = root["dns"]?["rules"]?.AsArray();
        Assert.NotNull(dnsRules);
        var procDns = dnsRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procDns);
        var resolverTag = (string?)procDns!["server"];
        Assert.False(string.IsNullOrEmpty(resolverTag));

        // 3. Resolver server in dns.servers has detour == "wg" (NOT dns-direct!)
        var dnsServers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(dnsServers);
        var resolverServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == resolverTag);
        Assert.NotNull(resolverServer);
        Assert.Equal("wg", (string?)resolverServer!["detour"]);

        // 4. Since strict=false and split/include, dns.final remains the local resolver
        Assert.Equal("local-https", (string?)root["dns"]?["final"]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Controls: VLESS, Full, Strict, Direct, Unknown
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomConfigInjector_Control_Vless_IncludeSplit_DetourIsProxy()
    {
        // Control: Standard VLESS outbound in include mode routes process DNS through "proxy"
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.StrictDns = false;

        var injectedJson = CustomConfigInjector.Inject(
            VlessOutboundConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();
        var dnsRules = root["dns"]?["rules"]?.AsArray();
        var procDns = dnsRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procDns);
        var resolverTag = (string?)procDns!["server"];

        var dnsServers = root["dns"]?["servers"]?.AsArray();
        var resolverServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == resolverTag);
        Assert.NotNull(resolverServer);
        Assert.Equal("proxy", (string?)resolverServer!["detour"]);
    }

    [Fact]
    public void CustomConfigInjector_Control_WireGuard_FullTunnel_DnsFinalRoutesThroughWg()
    {
        // Control: Full tunnel with WG endpoint sets dns.final to resolver routed through "wg"
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";
        settings.App.StrictDns = false;

        var injectedJson = CustomConfigInjector.Inject(
            PlainWireGuardEndpointConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();
        var dnsFinalTag = (string?)root["dns"]?["final"];
        Assert.False(string.IsNullOrEmpty(dnsFinalTag));

        var dnsServers = root["dns"]?["servers"]?.AsArray();
        var finalServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == dnsFinalTag);
        Assert.NotNull(finalServer);
        Assert.Equal("wg", (string?)finalServer!["detour"]);
    }

    [Fact]
    public void CustomConfigInjector_Control_WireGuard_StrictDns_DnsFinalAndProcessRouteThroughWg()
    {
        // Control: StrictDns=true with WG endpoint ensures both process DNS and dns.final route through "wg"
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "include";
        settings.App.StrictDns = true;

        var injectedJson = CustomConfigInjector.Inject(
            PlainWireGuardEndpointConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();

        var dnsFinalTag = (string?)root["dns"]?["final"];
        Assert.False(string.IsNullOrEmpty(dnsFinalTag));

        var dnsServers = root["dns"]?["servers"]?.AsArray();
        var finalServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == dnsFinalTag);
        Assert.NotNull(finalServer);
        Assert.Equal("wg", (string?)finalServer!["detour"]);

        var dnsRules = root["dns"]?["rules"]?.AsArray();
        var procDns = dnsRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procDns);
        var procServer = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == (string?)procDns!["server"]);
        Assert.NotNull(procServer);
        Assert.Equal("wg", (string?)procServer!["detour"]);
    }

    [Fact]
    public void CustomConfigInjector_Control_DirectAndUnknownDetours_AreTreatedAsLocal()
    {
        // Control: direct/dns-direct, custom-named direct outbounds, and unknown detours
        // must be classified as local (IsLocalDetour = true) and normalized to dns-direct.
        var configJson = """
        {
          "endpoints": [
            { "type": "wireguard", "tag": "wg" }
          ],
          "outbounds": [
            { "type": "direct", "tag": "direct" },
            { "type": "direct", "tag": "custom-direct" },
            { "type": "block", "tag": "custom-block" },
            { "type": "dns", "tag": "custom-dns" },
            { "type": "vless", "tag": "vless-proxy" }
          ]
        }
        """;

        var root = JsonNode.Parse(configJson)!.AsObject();
        var outbounds = root["outbounds"] as JsonArray;
        var endpoints = root["endpoints"] as JsonArray;

        // Direct and shims are local
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "direct"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "dns-direct"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "custom-direct"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "custom-block"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "custom-dns"));

        // Unknown detour is fail-closed local
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "unknown-tag"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, ""));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, null));

        // Proxy outbound and WG endpoint are remote (NOT local)
        Assert.False(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "vless-proxy"));
        Assert.False(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "wg"));

        // Preserve casing: exact tag match required
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "WG"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "Vless-Proxy"));
    }

    [Fact]
    public void CustomConfigInjector_Control_ArbitraryEndpointType_FailsClosedAsLocal()
    {
        // Don't generically trust arbitrary endpoints: only type="wireguard" is remote;
        // arbitrary/unknown endpoints fail closed as local.
        var outbounds = new JsonArray
        {
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" }
        };
        var endpoints = new JsonArray
        {
            new JsonObject { ["type"] = "direct", ["tag"] = "direct-ep" },
            new JsonObject { ["type"] = "custom_transport", ["tag"] = "custom-ep" },
            new JsonObject { ["type"] = "wireguard", ["tag"] = "wg-ep" }
        };

        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "direct-ep"));
        Assert.True(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "custom-ep"));
        Assert.False(CustomConfigInjector.IsLocalDetour(outbounds, endpoints, "wg-ep"));
    }

    [Fact]
    public void CustomConfigInjector_Control_DirectAndUnknownDetours_FinalInject_FailsClosedToSynthesizedRemote()
    {
        // Control: full-Inject behavioral verification for direct, custom-direct, unknown detours,
        // and non-wireguard endpoint types. In full tunnel (wantRemoteDns = true), StripUnsupportedFeatures
        // normalizes all local/unknown detours to dns-direct, FindRemoteDnsTag rejects them as local,
        // and CustomConfigInjector synthesizes a remote DNS server pointing to the valid WG endpoint.
        // Note: inbounds (including tun) are optional in injector unit tests; no real network required.
        var configJson = /*lang=json*/ """
        {
          "endpoints": [
            {
              "type": "wireguard",
              "tag": "wg",
              "address": ["10.0.0.2/32"],
              "private_key": "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=",
              "peers": [
                {
                  "address": "198.51.100.10",
                  "port": 51820,
                  "public_key": "AgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgI=",
                  "allowed_ips": ["0.0.0.0/0"]
                }
              ]
            },
            {
              "type": "custom_transport",
              "tag": "custom-ep"
            }
          ],
          "outbounds": [
            { "type": "direct", "tag": "direct" },
            { "type": "direct", "tag": "custom-direct" },
            { "type": "direct", "tag": "dns-direct", "udp_fragment": true }
          ],
          "dns": {
            "servers": [
              { "tag": "custom-direct-dns", "type": "https", "server": "1.1.1.1", "detour": "custom-direct" },
              { "tag": "unknown-detour-dns", "type": "https", "server": "8.8.8.8", "detour": "unknown-detour" },
              { "tag": "custom-ep-dns", "type": "https", "server": "9.9.9.9", "detour": "custom-ep" }
            ],
            "rules": []
          }
        }
        """;

        var settings = CreateSettings();
        settings.App.RoutingMode = "full";
        settings.App.StrictDns = false;

        var injectedJson = CustomConfigInjector.Inject(
            configJson,
            Array.Empty<string>(),
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();

        // 1. All local and unknown detour servers were rewritten to dns-direct by StripUnsupportedFeatures
        var servers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(servers);

        var customDirectDns = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "custom-direct-dns");
        Assert.NotNull(customDirectDns);
        Assert.Equal("dns-direct", (string?)customDirectDns!["detour"]);

        var unknownDetourDns = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "unknown-detour-dns");
        Assert.NotNull(unknownDetourDns);
        Assert.Equal("dns-direct", (string?)unknownDetourDns!["detour"]);

        var customEpDns = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "custom-ep-dns");
        Assert.NotNull(customEpDns);
        Assert.Equal("dns-direct", (string?)customEpDns!["detour"]);

        // 2. Full tunnel fails closed: synthesized remote DNS server with detour=wg becomes dns.final
        var dnsFinal = (string?)root["dns"]?["final"];
        Assert.Equal("vpnrouter-vpn-dns", dnsFinal);

        var synthServer = servers!.OfType<JsonObject>().FirstOrDefault(s => (string?)s["tag"] == "vpnrouter-vpn-dns");
        Assert.NotNull(synthServer);
        Assert.Equal("wg", (string?)synthServer!["detour"]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Custom Exclude Mode: StrictDns on / off
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(false, "local-https", "dns-direct")]
    [InlineData(true, "vpnrouter-vpn-dns", "wg")] // Under StrictDns, custom exclude routes process to remote resolver
    public void CustomConfigInjector_CustomExclude_StrictDns_TogglesProcessDnsRemote(
        bool strictDns,
        string expectedServerTag,
        string expectedDetour)
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "split";
        settings.App.RoutingAppsMode = "exclude";
        settings.App.RoutingAppsExclude = new List<string> { "Firefox.exe" };
        settings.App.StrictDns = strictDns;

        var injectedJson = CustomConfigInjector.Inject(
            PlainWireGuardEndpointConfig,
            new[] { "Firefox.exe" },
            settings);

        var root = JsonNode.Parse(injectedJson)!.AsObject();

        var dnsRules = root["dns"]?["rules"]?.AsArray();
        Assert.NotNull(dnsRules);
        var procDns = dnsRules!.OfType<JsonObject>().FirstOrDefault(r =>
            r["process_name"] is JsonArray pa && pa.Any(p => (string?)p == "Firefox.exe"));
        Assert.NotNull(procDns);
        Assert.Equal(expectedServerTag, (string?)procDns!["server"]);

        var dnsServers = root["dns"]?["servers"]?.AsArray();
        Assert.NotNull(dnsServers);
        var resolver = dnsServers!.OfType<JsonObject>().FirstOrDefault(s =>
            (string?)s["tag"] == expectedServerTag);
        Assert.NotNull(resolver);
        Assert.Equal(expectedDetour, (string?)resolver!["detour"]);
    }
}
