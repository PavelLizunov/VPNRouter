using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

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
        // v2.31.9-r5 migrated geosite/geoip rule-sets from type:remote
        // (with Url) to type:local (with Path), routed through
        // RuleSetCacheManager. The on-disk .srs is pre-fetched in C# with
        // a bounded timeout instead of letting sing-box do a synchronous
        // mandatory fetch at startup (which crashed sing-box on TLS
        // timeout — brat-2026-05-05 P0). Pre-populate the cache file so
        // EnsureLocal returns a path deterministically without hitting
        // the network; clean up after to avoid polluting %ProgramData%.
        var cacheDir = System.IO.Path.Combine(
            VPNRouter.Core.AppPaths.CacheDir,
            VPNRouter.Core.Services.RuleSetCacheManager.CacheSubdir);
        System.IO.Directory.CreateDirectory(cacheDir);
        var stubPath = System.IO.Path.Combine(cacheDir, "user-geosite-ads.srs");
        System.IO.File.WriteAllBytes(stubPath, new byte[] { 0x53, 0x52, 0x53, 0x00 }); // "SRS\0" magic placeholder
        try
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
            Assert.Equal("local", config.Route.RuleSet![0].Type);
            Assert.NotNull(config.Route.RuleSet![0].Path);
            Assert.EndsWith("user-geosite-ads.srs", config.Route.RuleSet![0].Path!);
        }
        finally
        {
            try { System.IO.File.Delete(stubPath); } catch { }
        }
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
