using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>v2.30.0-r3: tests for the 3-format import/export of
/// custom rules (CSV / VPNRouter JSON / sing-box-native).</summary>
public class CustomRulesImportExportTests
{
    [Fact]
    public void Detect_DetectsCsvFromPlainText()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "action,type,value\ndirect,ip_cidr,10.0.0.0/8");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv, fmt);
    }

    [Fact]
    public void Detect_DetectsVpnrouterJson()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "[{\"action\":\"direct\",\"type\":\"ip_cidr\",\"value\":\"10.0.0.0/8\"}]");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson, fmt);
    }

    [Fact]
    public void Detect_DetectsSingBoxJson()
    {
        var fmt = VPNRouter.Core.Services.CustomRulesImportExport.Detect(
            "[{\"domain_suffix\":[\".corp\"],\"outbound\":\"proxy\"}]");
        Assert.Equal(VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson, fmt);
    }

    [Fact]
    public void Csv_RoundTrips_PreservesAllFields()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Comment = "LAN", Enabled = true },
            new() { Action = "block", Type = "domain_keyword", Value = "ads, tracker", Comment = "ads with comma", Enabled = false },
        };
        var csv = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Contains("ads, tracker", csv);  // multi-value preserved
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(csv, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Empty(imported.Warnings);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("LAN", imported.Rules[0].Comment);
        Assert.False(imported.Rules[1].Enabled);
        Assert.Equal("ads, tracker", imported.Rules[1].Value);
    }

    [Fact]
    public void Csv_HandlesQuotedFields()
    {
        var csv = "action,type,value,comment,enabled\n"
                + "direct,domain,\"a, b, c\",\"with \"\"quotes\"\"\",true\n";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(csv, VPNRouter.Core.Services.CustomRulesImportExport.Format.Csv);
        Assert.Single(imported.Rules);
        Assert.Equal("a, b, c", imported.Rules[0].Value);
        Assert.Equal("with \"quotes\"", imported.Rules[0].Comment);
    }

    [Fact]
    public void VpnrouterJson_RoundTrips()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "proxy", Type = "domain_suffix", Value = ".corp", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        var json = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson);
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(json, VPNRouter.Core.Services.CustomRulesImportExport.Format.VpnrouterJson);
        Assert.Empty(imported.Warnings);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("proxy", imported.Rules[0].Action);
        Assert.Equal("geosite", imported.Rules[1].Type);
    }

    [Fact]
    public void SingBoxJson_ImportsBareRulesArray()
    {
        var sb = "[" +
                 "{\"domain_suffix\":[\".corp.example\"],\"outbound\":\"proxy\"}," +
                 "{\"ip_cidr\":[\"10.0.0.0/8\"],\"outbound\":\"direct\"}," +
                 "{\"domain_keyword\":[\"ads\"],\"action\":\"reject\"}" +
                 "]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(3, imported.Rules.Count);
        Assert.Equal("proxy", imported.Rules[0].Action);
        Assert.Equal("domain_suffix", imported.Rules[0].Type);
        Assert.Equal("direct", imported.Rules[1].Action);
        Assert.Equal("block", imported.Rules[2].Action);
    }

    [Fact]
    public void SingBoxJson_ImportsRulesArrayInsideRouteObject()
    {
        var sb = "{\"route\":{\"rules\":[{\"domain\":[\"x.example\"],\"outbound\":\"proxy\"}]}}";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Single(imported.Rules);
        Assert.Equal("proxy", imported.Rules[0].Action);
    }

    [Fact]
    public void SingBoxJson_ExplodesMultiMatchRule()
    {
        // sing-box rule with both domain_suffix AND ip_cidr in one rule.
        // Our schema is one-match-per-rule, so we explode it into 2 entries.
        var sb = "[{\"domain_suffix\":[\".corp\"],\"ip_cidr\":[\"10.0.0.0/8\"],\"outbound\":\"proxy\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(2, imported.Rules.Count);
        Assert.NotEmpty(imported.Warnings); // warning about explosion
        Assert.All(imported.Rules, r => Assert.Equal("proxy", r.Action));
    }

    [Fact]
    public void SingBoxJson_StripsRuleSetTagPrefix()
    {
        var sb = "[{\"rule_set\":[\"user-geosite-ads\",\"vpnrouter-geosite-ru\"],\"action\":\"reject\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Single(imported.Rules);
        Assert.Equal("block", imported.Rules[0].Action);
        Assert.Contains("ads", imported.Rules[0].Value);
        Assert.Contains("ru", imported.Rules[0].Value);
        Assert.DoesNotContain("user-geosite-", imported.Rules[0].Value);
    }

    [Fact]
    public void SingBoxJson_ExportProducesValidImportableForm()
    {
        var original = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
            new() { Action = "block", Type = "geosite", Value = "ads", Enabled = true },
        };
        var sb = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(original, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Contains("\"outbound\": \"direct\"", sb);
        Assert.Contains("\"action\": \"reject\"", sb);
        // Round-trip via SingBoxJson import.
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("direct", imported.Rules[0].Action);
        Assert.Equal("block", imported.Rules[1].Action);
    }

    [Fact]
    public void DisabledRules_NotExportedToSingBoxJson()
    {
        var rules = new List<VPNRouter.Core.Models.CustomRule>
        {
            new() { Action = "direct", Type = "ip_cidr", Value = "10.0.0.0/8", Enabled = true },
            new() { Action = "block", Type = "domain", Value = "skipped.example", Enabled = false },
        };
        var sb = VPNRouter.Core.Services.CustomRulesImportExport
            .ExportToText(rules, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.DoesNotContain("skipped.example", sb);
        Assert.Contains("10.0.0.0/8", sb);
    }
}
