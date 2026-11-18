using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
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
        Assert.Contains("ads, tracker", csv);
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
    public void SingBoxJson_RejectsMultiMatchRule()
    {
        var sb = "[{\"domain_suffix\":[\".corp\"],\"ip_cidr\":[\"10.0.0.0/8\"],\"outbound\":\"proxy\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Empty(imported.Rules);
        Assert.NotEmpty(imported.Warnings);
    }

    [Fact]
    public void SingBoxJson_StripsRuleSetTagPrefix()
    {
        var sb = "[{\"rule_set\":[\"user-geosite-ads\",\"vpnrouter-geosite-ru\"],\"action\":\"reject\"}]";
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Single(imported.Rules);
        Assert.Equal("block", imported.Rules[0].Action);
        Assert.Equal("ads, tld-ru", imported.Rules[0].Value);
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
        var imported = VPNRouter.Core.Services.CustomRulesImportExport
            .ImportFromText(sb, VPNRouter.Core.Services.CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(2, imported.Rules.Count);
        Assert.Equal("direct", imported.Rules[0].Action);
        Assert.Equal("block", imported.Rules[1].Action);
    }

    [Theory]
    [InlineData(CustomRulesImportExport.Format.Csv)]
    [InlineData(CustomRulesImportExport.Format.VpnrouterJson)]
    [InlineData(CustomRulesImportExport.Format.SingBoxJson)]
    public void RoundTrip_PreservesEveryRangeAndNetwork(CustomRulesImportExport.Format format)
    {
        var original = new List<CustomRule>
        {
            new() { Action = "block", Type = "port_range", Value = "1-32768, 60000-65535" },
            new() { Action = "block", Type = "network", Value = "tcp, udp" },
        };

        var text = CustomRulesImportExport.ExportToText(original, format);
        var imported = CustomRulesImportExport.ImportFromText(text);

        Assert.Empty(imported.Warnings);
        Assert.Equal(format, imported.DetectedFormat);
        Assert.Equal(original.Select(r => (r.Action, r.Type, r.Value)),
            imported.Rules.Select(r => (r.Action, r.Type, r.Value)));

        if (format == CustomRulesImportExport.Format.SingBoxJson)
        {
            using var json = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal(new[] { "1:32768", "60000:65535" },
                json.RootElement[0].GetProperty("port_range").EnumerateArray().Select(el => el.GetString()));
            Assert.Equal(new[] { "tcp", "udp" },
                json.RootElement[1].GetProperty("network").EnumerateArray().Select(el => el.GetString()));
        }
    }

    [Theory]
    [InlineData("""[{"port_range": ["1-24000", "30000:65535"], "action": "reject"}]""",
        "port_range", "1-24000, 30000-65535")]
    [InlineData("""[{"port_range": [":1000", "50000:"], "action": "reject"}]""",
        "port_range", ":1000, 50000:")]
    [InlineData("""[{"network" : ["udp", "tcp"], "action": "reject"}]""", "network", "udp, tcp")]
    [InlineData("""[{"network" : "tcp", "action": "reject"}]""", "network", "tcp")]
    public void SingBoxJson_ImportsLegacyAndNativeMatchValues(string json, string type, string value)
    {
        var imported = CustomRulesImportExport.ImportFromText(json);
        Assert.Empty(imported.Warnings);
        var rule = Assert.Single(imported.Rules);
        Assert.Equal("block", rule.Action);
        Assert.Equal(type, rule.Type);
        Assert.Equal(value, rule.Value);
        Assert.Empty(CustomRulesParser.ParseFromText(CustomRulesParser.SerializeToText(imported.Rules)).Errors);
        var exported = CustomRulesImportExport.ExportToText(imported.Rules, CustomRulesImportExport.Format.SingBoxJson);
        Assert.Equal(value, Assert.Single(CustomRulesImportExport.ImportFromText(exported).Rules).Value);
    }

    [Fact]
    public void SingBoxJson_ImportsGeoipAndGeositeAsTheirOwnFamilies()
    {
        const string json = """[{"rule_set":["user-geoip-us","vpnrouter-geoip-ru","user-geosite-example"],"action":"reject"}]""";
        var imported = CustomRulesImportExport.ImportFromText(json);
        Assert.Empty(imported.Warnings);
        Assert.Equal(new[] { ("geoip", "us, ru"), ("geosite", "example") },
            imported.Rules.Select(r => (r.Type, r.Value)));
    }

    [Theory]
    [InlineData("""{"outbound":"direct","network":"tcp","port_range":"1000:5000"}""")]
    [InlineData("""{"action":"sniff","network":"tcp"}""")]
    [InlineData("""{"action":"resolve","domain":"sample.example"}""")]
    [InlineData("""{"action":"route","network":"udp"}""")]
    [InlineData("""{"outbound":"other","domain":"sample.example"}""")]
    [InlineData("""{"action":"reject","invert":true,"domain":"sample.example"}""")]
    [InlineData("""{"action":"reject","rule_set":"custom-sites"}""")]
    [InlineData("""{"action":"reject","rule_set":"vpnrouter-geosite-unknown"}""")]
    [InlineData("""{"action":"reject","domain":["one.example,two.example"]}""")]
    [InlineData("""{"action":"reject","port":[443,70000]}""")]
    public void NativeImport_UnsupportedRowRejectsWholeBatch(string row)
    {
        var json = """[{"action":"reject","domain":"keep.example"},""" + row + "]";
        var result = CustomRulesImportExport.ImportFromText(json);
        Assert.Empty(result.Rules);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void NativeImport_PreservesSimpleDeclaredDirectButRejectsRedefinedDirection()
    {
        const string json = """
            {"outbounds":[{"type":"direct","tag":"direct"}],
             "route":{"rules":[{"type":"default","invert":false,"outbound":"direct","ip_cidr":"192.0.2.1"}]}}
            """;
        var result = CustomRulesImportExport.ImportFromText(json);
        Assert.Empty(result.Warnings);
        Assert.Equal("192.0.2.1", Assert.Single(result.Rules).Value);
        var changed = json.Replace("\"type\":\"direct\"", "\"type\":\"selector\"");
        var rejected = CustomRulesImportExport.ImportFromText(changed);
        Assert.Empty(rejected.Rules);
        Assert.NotEmpty(rejected.Warnings);
    }

    [Fact]
    public void NativeImport_DeclaredSourcesCannotBeInferredFromTheirTag()
    {
        const string json = """
            {"route":{"rule_set":[{"type":"inline","tag":"user-geosite-example","rules":[{"domain":"other.example"}]}],
             "rules":[{"action":"reject","port":443},{"action":"reject","rule_set":"user-geosite-example"}]}}
            """;
        var result = CustomRulesImportExport.ImportFromText(json);
        Assert.Empty(result.Rules);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("""[{"action":"block","type":"geosite","value":"Example,SECOND"}]""")]
    [InlineData("""[{"action":"reject","rule_set":["USER-GEOSITE-Example","user-geosite-SECOND"]}]""")]
    [InlineData("block,geosite,\"Example,SECOND\"")]
    public void Import_NormalizesSupportedGeoTagsAcrossFormats(string input)
    {
        var result = CustomRulesImportExport.ImportFromText(input);
        Assert.Empty(result.Warnings);
        Assert.Equal("example, second", Assert.Single(result.Rules).Value);
    }

    [Theory]
    [InlineData("""[{"action":"block","type":"geosite","value":"one,"}]""")]
    [InlineData("""[{"type":"geosite","value":"one"}]""")]
    [InlineData("block,geosite,good\ndirect,geoip,bad.name")]
    [InlineData("block,geosite,good\nblock,port,443,,sometimes")]
    [InlineData("block,geosite,good\nblock,geosite,\"unfinished")]
    public void MalformedRowsCannotBePartiallyImported(string input)
    {
        var result = CustomRulesImportExport.ImportFromText(input);
        Assert.Empty(result.Rules);
        Assert.NotEmpty(result.Warnings);
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
