using System.Text.Json;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
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
    public void Apply_EmitsEveryPortRangeAndTransportInRuleOrder()
    {
        var config = NewConfigWithEmptyRoutes();
        ConfigGenerator.ApplyCustomRules(config,
        [
            new() { Action = "block", Type = "port_range", Value = "1-45000, 50000-65535" },
            new() { Action = "proxy", Type = "network", Value = "TCP, udp, tcp" },
            new() { Action = "direct", Type = "network", Value = "udp" },
        ]);

        using var json = JsonDocument.Parse(ConfigGenerator.Serialize(config));
        var rules = json.RootElement.GetProperty("route").GetProperty("rules");
        Assert.Equal(3, rules.GetArrayLength());
        Assert.Equal("reject", rules[0].GetProperty("action").GetString());
        Assert.Equal(new[] { "1:45000", "50000:65535" },
            rules[0].GetProperty("port_range").EnumerateArray().Select(v => v.GetString()));
        Assert.False(rules[0].TryGetProperty("port", out _));
        Assert.Equal("proxy", rules[1].GetProperty("outbound").GetString());
        Assert.Equal(new[] { "tcp", "udp" }, rules[1].GetProperty("network").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("udp", rules[2].GetProperty("network").GetString());

        var reloaded = json.RootElement.Deserialize(VPNRouter.Core.Json.AppJsonContext.Default.SingBoxConfig)!;
        Assert.Equal("tcp,udp", reloaded.Route.Rules[1].Network);
        Assert.Equal("udp", reloaded.Route.Rules[2].Network);
        Assert.Equal(config.Route.Rules[0].PortRange, reloaded.Route.Rules[0].PortRange);
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

    [Theory]
    [InlineData("geosite")]
    [InlineData("geoip")]
    public void Apply_ReferencesOnlyRegisteredSetsAndResolvesEachOnce(string type)
    {
        var config = NewConfigWithEmptyRoutes();
        var calls = new List<string>();
        var prefix = $"user-{type}-";
        string? Resolve(string url, string filename)
        {
            calls.Add(filename);
            Assert.EndsWith("/" + filename["user-".Length..], url);
            return filename == prefix + "missing.srs" ? null : Path.Combine("cache", filename);
        }

        ConfigGenerator.ApplyCustomRules(config,
        [
            new() { Type = type, Action = "direct", Value = "First, missing, FIRST" },
            new() { Type = type, Action = "block", Value = "second, first, second" },
            new() { Type = type, Action = "proxy", Value = "second" },
            new() { Type = type, Action = "direct", Value = "missing" },
            new() { Type = type, Action = "proxy", Value = "disabled", Enabled = false },
        ], Resolve);

        var first = prefix + "first";
        var second = prefix + "second";
        Assert.Equal(new[] { first + ".srs", prefix + "missing.srs", second + ".srs" }, calls);
        Assert.Equal(new[] { first, second }, config.Route.RuleSet!.Select(entry => entry.Tag));
        Assert.All(config.Route.RuleSet!, entry =>
        {
            Assert.Equal("local", entry.Type);
            Assert.Equal("binary", entry.Format);
            Assert.Equal(Path.Combine("cache", entry.Tag + ".srs"), entry.Path);
        });
        Assert.Equal(3, config.Route.Rules.Count);
        Assert.Equal(new[] { first }, config.Route.Rules[0].RuleSet);
        Assert.Equal(new[] { second, first }, config.Route.Rules[1].RuleSet);
        Assert.Equal(new[] { second }, config.Route.Rules[2].RuleSet);
        Assert.Equal("direct", config.Route.Rules[0].Outbound);
        Assert.Equal("reject", config.Route.Rules[1].Action);
        Assert.Equal("proxy", config.Route.Rules[2].Outbound);

        if (type == "geosite")
        {
            Assert.Equal(2, config.Dns.Rules.Count);
            Assert.Equal("reject", config.Dns.Rules[0].Action);
            Assert.Equal(config.Route.Rules[1].RuleSet, config.Dns.Rules[0].RuleSet);
            Assert.Equal("vpn-dns", config.Dns.Rules[1].Server);
            Assert.Equal(config.Route.Rules[2].RuleSet, config.Dns.Rules[1].RuleSet);
        }
        else Assert.Empty(config.Dns.Rules);
    }

    [Fact]
    public void Apply_MissingSetPreviouslyUsedByDirectRule_LeavesOriginalRulesIntact()
    {
        var config = NewConfigWithEmptyRoutes();
        var original = new RouteRule { Action = "sniff" };
        config.Route.Rules.Add(original);
        int resolutions = 0;

        Assert.Throws<InvalidOperationException>(() => ConfigGenerator.ApplyCustomRules(config,
        [
            new() { Action = "block", Type = "domain", Value = "example.org" },
            new() { Action = "direct", Type = "geoip", Value = "missing" },
            new() { Action = "proxy", Type = "geoip", Value = "missing" },
        ], (_, _) => { resolutions++; return null; }));

        Assert.Equal(1, resolutions);
        Assert.Same(original, Assert.Single(config.Route.Rules));
        Assert.Empty(config.Dns.Rules);
        Assert.Null(config.Route.RuleSet);
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
