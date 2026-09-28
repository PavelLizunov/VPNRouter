using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class LeakAuditFixTests
{
    private static AppSettings BuildSettings(string appsMode = "include",
        string routingMode = "split",
        List<string>? include = null, List<string>? exclude = null)
    {
        return new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = routingMode,
                RoutingAppsMode = appsMode,
                RoutingAppsInclude = include ?? new List<string>(),
                RoutingAppsExclude = exclude ?? new List<string>(),
                ConfigMode = "generated",
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
                        Name = "t", Server = "1.2.3.4", Port = 443, Uuid = "abc",
                        Flow = "xtls-rprx-vision", Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "pk", ShortId = "sid" },
                    },
                },
            },
        };
    }

    private static Profile EmptyProfile() => new()
    {
        Name = "P", DnsMode = "vpn_only", Processes = new List<ProcessRule>(),
    };

    [Fact]
    public void Gap2_DirectRoutedProcess_DoesNotWarnDnsLeak_ButProxyRoutedDoes()
    {
        var config = new SingBoxConfig
        {
            Inbounds = new List<SingBoxInbound>(),
            Outbounds = new List<SingBoxOutbound>(),
            Dns = new SingBoxDns
            {
                Strategy = "ipv4_only",
                Final = "local-dns",
                Servers = new List<DnsServer>(),
                Rules = new List<DnsRule>(),
            },
            Route = new SingBoxRoute
            {
                Final = "direct",
                Rules = new List<RouteRule>
                {
                    new() { ProcessName = new List<string> { "proxyapp.exe" }, Action = "route", Outbound = "proxy" },
                    new() { ProcessName = new List<string> { "directapp.exe" }, Action = "route", Outbound = "direct" },
                },
            },
        };

        var result = LeakProtection.ValidateConfig(config, settings: null);

        Assert.Contains(result.Warnings, w => w.Contains("proxyapp.exe") && w.Contains("DNS may leak"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("directapp.exe"));
    }

    [Fact]
    public void Gap2_GeneratedExcludeConfig_NoSpuriousDnsLeakWarning()
    {
        var settings = BuildSettings(appsMode: "exclude", exclude: new List<string> { "Steam.exe" });
        var config = ConfigGenerator.Generate(EmptyProfile(), System.Array.Empty<string>(), settings);

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("Steam.exe") && w.Contains("DNS may leak"));
    }

    [Fact]
    public void Gap1_FinalInverted_ForIncludeSplit_Warns()
    {
        var settings = BuildSettings(appsMode: "include", routingMode: "split",
            include: new List<string> { "Discord.exe" });
        var config = ConfigGenerator.Generate(EmptyProfile(), new[] { "Discord.exe" }, settings);

        Assert.Equal("direct", config.Route.Final);

        config.Route.Final = "proxy";
        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.Contains(result.Warnings, w => w.Contains("routing inversion"));
    }

    [Fact]
    public void Gap1_CorrectFinal_IncludeSplit_NoInversionWarning()
    {
        var settings = BuildSettings(appsMode: "include", routingMode: "split",
            include: new List<string> { "Discord.exe" });
        var config = ConfigGenerator.Generate(EmptyProfile(), new[] { "Discord.exe" }, settings);

        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("routing inversion"));
    }

    [Fact]
    public void Gap1_ExcludeMode_FinalProxy_NoInversionWarning()
    {
        var settings = BuildSettings(appsMode: "exclude", exclude: new List<string> { "Steam.exe" });
        var config = ConfigGenerator.Generate(EmptyProfile(), System.Array.Empty<string>(), settings);

        Assert.Equal("proxy", config.Route.Final);
        var result = LeakProtection.ValidateConfig(config, settings);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("routing inversion"));
    }

    [Fact]
    public void W14a_RemoveInjectedProcessRules_ReportsReplacedCount_AndKeepsOtherRules()
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["process_name"] = new JsonArray { "foreign-a.exe" }, ["outbound"] = "myproxy" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
            new JsonObject { ["process_name"] = new JsonArray { "foreign-b.exe" }, ["outbound"] = "myproxy" },
        };

        var removed = CustomConfigInjector.RemoveInjectedProcessRules(rules);

        Assert.Equal(2, removed);
        Assert.Equal(2, rules.Count);
        Assert.DoesNotContain(rules, n => n is JsonObject o && o["process_name"] != null);
    }

    [Fact]
    public void W14a_Inject_OverridesUserProcessNameRule_WithVpnRouterList()
    {
        const string rawJson = """
        {
          "outbounds": [
            { "type": "vless", "tag": "myproxy", "server": "1.2.3.4", "server_port": 443, "uuid": "x" },
            { "type": "direct", "tag": "direct" }
          ],
          "route": { "rules": [ { "process_name": ["foreign.exe"], "outbound": "myproxy" } ], "final": "direct" }
        }
        """;

        var settings = BuildSettings();
        var outJson = CustomConfigInjector.Inject(rawJson, new[] { "Discord.exe" }, settings);

        var node = JsonNode.Parse(outJson)!;
        var procNames = (node["route"]?["rules"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(o => o["process_name"] is JsonArray)
            .SelectMany(o => (o["process_name"] as JsonArray)!)
            .Select(n => n!.GetValue<string>())
            .ToList();

        Assert.Contains("Discord.exe", procNames);
        Assert.DoesNotContain("foreign.exe", procNames);
    }
}
