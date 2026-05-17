using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
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
