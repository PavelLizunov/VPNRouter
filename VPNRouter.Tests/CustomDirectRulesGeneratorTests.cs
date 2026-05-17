using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
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
