using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>v2.29.0-r4: tests for the text-format parser/serializer
/// used by the Network → Routing → "Custom direct rules" textbox.</summary>
public class CustomDirectRulesParserTests
{
    [Fact]
    public void Parse_EmptyText_NoRules_NoErrors()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("");
        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WhitespaceOnly_NoRules_NoErrors()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("   \n\n  \r\n  ");
        Assert.Empty(result.Rules);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_SimpleRule()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("ip_cidr 10.0.0.0/8");
        Assert.Single(result.Rules);
        Assert.Empty(result.Errors);
        Assert.Equal("ip_cidr", result.Rules[0].Type);
        Assert.Equal("10.0.0.0/8", result.Rules[0].Value);
        Assert.True(result.Rules[0].Enabled);
    }

    [Fact]
    public void Parse_MultipleRulesAndComments()
    {
        var text = """
            # Comment line
            ip_cidr 10.0.0.0/8, 192.168.0.0/16    # Local LANs
            domain_suffix .lan.local
            !port 53                              # disabled
            """;
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Rules.Count);

        Assert.Equal("ip_cidr", result.Rules[0].Type);
        Assert.Equal("10.0.0.0/8, 192.168.0.0/16", result.Rules[0].Value);
        Assert.Equal("Local LANs", result.Rules[0].Comment);
        Assert.True(result.Rules[0].Enabled);

        Assert.Equal("domain_suffix", result.Rules[1].Type);
        Assert.Equal(".lan.local", result.Rules[1].Value);

        Assert.Equal("port", result.Rules[2].Type);
        Assert.Equal("53", result.Rules[2].Value);
        Assert.False(result.Rules[2].Enabled);
        Assert.Equal("disabled", result.Rules[2].Comment);
    }

    [Fact]
    public void Parse_InvalidCidr_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("ip_cidr 999.999.0.0/8");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Equal(1, result.Errors[0].LineNumber);
        Assert.Contains("CIDR", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_InvalidPort_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("port 99999");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Contains("port", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_UnknownType_RaisesError()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("unknown_type foo");
        Assert.Empty(result.Rules);
        Assert.Single(result.Errors);
        Assert.Contains("Unknown type", result.Errors[0].Reason);
    }

    [Fact]
    public void Parse_PartialFailure_KeepsValidRules()
    {
        var text = """
            ip_cidr 10.0.0.0/8
            unknown_type foo
            domain_suffix .lan.local
            """;
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Equal(2, result.Rules.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Serialize_RoundTrips_Correctly()
    {
        var input = new List<VPNRouter.Core.Models.CustomDirectRule>
        {
            new() { Type = "ip_cidr", Value = "10.0.0.0/8, 192.168.0.0/16", Comment = "LANs", Enabled = true },
            new() { Type = "port",    Value = "53",                          Enabled = false },
            new() { Type = "domain_suffix", Value = ".internal" },
        };
        var text = VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(input);
        var roundTrip = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText(text);
        Assert.Empty(roundTrip.Errors);
        Assert.Equal(3, roundTrip.Rules.Count);

        Assert.Equal("ip_cidr", roundTrip.Rules[0].Type);
        Assert.Equal("LANs", roundTrip.Rules[0].Comment);
        Assert.True(roundTrip.Rules[0].Enabled);

        Assert.Equal("port", roundTrip.Rules[1].Type);
        Assert.False(roundTrip.Rules[1].Enabled);
    }

    [Fact]
    public void Serialize_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(null));
        Assert.Equal(string.Empty, VPNRouter.Core.Services.CustomDirectRulesParser.SerializeToText(
            new List<VPNRouter.Core.Models.CustomDirectRule>()));
    }

    [Fact]
    public void Parse_PreservesProcessNameCasing()
    {
        var result = VPNRouter.Core.Services.CustomDirectRulesParser.ParseFromText("process_name Discord.exe, ChromE.exe");
        Assert.Single(result.Rules);
        Assert.Equal("Discord.exe, ChromE.exe", result.Rules[0].Value);
    }
}
