using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorRemoteRuleSetGuardTests
{
    private static AppSettings BuildSettings(bool blockAds, bool bypassRu, List<CustomRule>? customRules = null)
    {
        var s = new AppSettings();
        s.App.RoutingMode = "full";
        s.App.BlockAds = blockAds;
        s.App.BypassRussianTraffic = bypassRu;
        s.App.CustomRules = customRules ?? new List<CustomRule>();
        var server = new VlessServerEntry
        {
            Server = "test.example.com", Port = 443,
            Uuid = "00000000-0000-0000-0000-000000000001",
            Flow = "xtls-rprx-vision", Security = "reality",
        };
        server.Reality.Enabled = true;
        server.Reality.ServerName = "google.com";
        server.Reality.PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        server.Reality.ShortId = "deadbeef";
        server.Reality.Fingerprint = "chrome";
        s.Vless.Servers = new List<VlessServerEntry> { server };
        return s;
    }

    private static Profile BuildProfile() =>
        new() { Name = "TestProfile", Processes = new List<ProcessRule>() };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Generate_ToggleMatrix_NoRemoteRuleSets(bool blockAds, bool bypassRu)
    {
        var settings = BuildSettings(blockAds, bypassRu);
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            var path = Path.Combine(AppPaths.CacheDir, "adblock_reject.srs");
            Directory.CreateDirectory(AppPaths.CacheDir);
            File.WriteAllBytes(path, RuleSetSamples.KeywordRule());
            var stale = DateTime.UtcNow.AddDays(-30);
            File.SetLastWriteTimeUtc(path, stale);
            File.SetLastWriteTimeUtc(AppPaths.GeoIpRuPath, stale);
            File.SetLastWriteTimeUtc(AppPaths.GeoSiteRuPath, stale);

            string Resolve(string _, string filename)
            {
                Assert.Equal("adblock_reject.srs", filename);
                return path;
            }

            var config = ConfigGenerator.Generate(BuildProfile(), ["Discord.exe"], settings, null, null, Resolve,
                onMissingRuleSet: ConfigGenerator.RejectMissingRuleSet, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
            var entries = config.Route.RuleSet ?? [];
            Assert.All(entries, entry => Assert.Equal("local", entry.Type));
            const string adTag = "vpnrouter-adblock";
            Assert.Equal(blockAds, entries.Any(entry => entry.Tag == adTag && entry.Path == path));
            Assert.Equal(blockAds, config.Route.Rules.Any(r => r.Action == "reject" && r.RuleSet?.Contains(adTag) == true));
            Assert.Equal(blockAds, config.Dns.Rules.Any(r => r.Action == "reject" && r.RuleSet?.Contains(adTag) == true));
            Assert.Equal(bypassRu, entries.Any(entry => entry.Tag == "vpnrouter-geoip-ru"));
            Assert.Equal(bypassRu, entries.Any(entry => entry.Tag == "vpnrouter-geosite-ru"));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HotApplyRejectsUnreadableBuiltInSetInGeneratedAndImportedConfig(bool damageGeoIp)
    {
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            File.WriteAllBytes(damageGeoIp ? AppPaths.GeoIpRuPath : AppPaths.GeoSiteRuPath,
                RuleSetSamples.IntactEnvelope(12 * 1024));
            var settings = BuildSettings(blockAds: false, bypassRu: true);
            Assert.Throws<InvalidOperationException>(() =>
                ConfigGenerator.Generate(BuildProfile(), [], settings, null, null, resolveRuleSet: null,
                    onMissingRuleSet: ConfigGenerator.RejectMissingRuleSet, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));
            Assert.Throws<InvalidOperationException>(() =>
                CustomConfigInjector.Inject("{}", [], settings, onMissingRuleSet: ConfigGenerator.RejectMissingRuleSet,
                    checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));

#if VPNROUTER_BUSINESS
            Assert.Throws<InvalidOperationException>(() =>
                ConfigGenerator.Generate(BuildProfile(), [], settings, null, null, resolveRuleSet: null,
                    checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));
            Assert.Throws<InvalidOperationException>(() =>
                CustomConfigInjector.Inject("{}", [], settings, onMissingRuleSet: null,
                    checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));
#else
            var missing = new List<string>();
            var cold = ConfigGenerator.Generate(BuildProfile(), [], settings, null, null, resolveRuleSet: null,
                onMissingRuleSet: missing.Add, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
            Assert.Single(missing);
            Assert.DoesNotContain(cold.Route.RuleSet ?? [], entry => entry.Tag == "vpnrouter-geoip-ru");
            missing.Clear();
            var imported = CustomConfigInjector.Inject("{}", [], settings, onMissingRuleSet: missing.Add,
                checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
            Assert.Single(missing);
            Assert.DoesNotContain("vpnrouter-geosite-ru", imported);
#endif
        });
    }

    private sealed class UnreadableRefresh : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(RuleSetSamples.IntactEnvelope())
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedAdFilterKeepsValidatedCacheAndBothRejects(bool stale)
    {
        const string imported = """
            {
              "outbounds": [
                {"type":"socks","tag":"proxy","server":"10.0.0.2","server_port":1080},
                {"type":"direct","tag":"direct"}
              ],
              "route": {
                "rule_set": [{"type":"inline","tag":"vpnrouter-adblock","rules":[{"domain":"example.test"}]}],
                "rules": [{"action":"sniff"},{"rule_set":"vpnrouter-adblock","action":"route","outbound":"proxy"}]
              },
              "dns": {"rules":[{"domain":"example.test","action":"reject"}]}
            }
            """;
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            var dir = Directory.CreateDirectory(Path.Combine(AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir));
            var path = Path.Combine(dir.FullName, "adblock_reject.srs");
            var original = RuleSetSamples.KeywordRule();
            File.WriteAllBytes(path, original);
            if (stale) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            var timestamp = File.GetLastWriteTimeUtc(path);
            var refresh = new UnreadableRefresh();
            using var client = new HttpClient(refresh);
            var runtime = Environment.GetEnvironmentVariable("VPNROUTER_TEST_VERIFIER_RUNTIME");
            if (runtime != null) Assert.True(File.Exists(runtime), "Missing supplied verifier runtime");
            Task<bool> Validate(string file, CancellationToken ct) => runtime == null
                ? RuleSetSamples.CheckFixtureContentsAsync(file, ct)
                : RuleSetSamples.CheckWithRuntimeAsync(file, ct, runtime);
            string? Resolve(string url, string file) =>
                RuleSetCacheManager.EnsureLocal(url, file, Validate, httpClient: client);

            Assert.True(CustomConfigInjector.Validate(imported).IsValid);
            var candidate = CustomConfigInjector.Inject(imported, [], BuildSettings(true, false),
                ConfigGenerator.RejectMissingRuleSet, resolveRuleSet: Resolve);
            var config = JsonNode.Parse(candidate)!;
            var sets = config["route"]!["rule_set"]!.AsArray();
            Assert.Equal("inline", sets[0]!["type"]!.GetValue<string>());
            Assert.Equal("vpnrouter-adblock", sets[0]!["tag"]!.GetValue<string>());
            var managed = Assert.Single(sets, s => s?["path"]?.GetValue<string>() == path);
            Assert.Equal("local", managed!["type"]!.GetValue<string>());
            var tag = managed["tag"]!.GetValue<string>();
            Assert.NotEqual("vpnrouter-adblock", tag);
            var routeRules = config["route"]!["rules"]!.AsArray();
            var routeReject = Assert.Single(routeRules, r => r?["rule_set"] is JsonArray list && list[0]!.GetValue<string>() == tag);
            Assert.Equal("reject", routeReject!["action"]!.GetValue<string>());
            var importedRule = Assert.Single(routeRules, r => r?["rule_set"] is JsonValue v && v.GetValue<string>() == "vpnrouter-adblock");
            Assert.True(routeRules.IndexOf(routeReject) < routeRules.IndexOf(importedRule));
            var dnsReject = config["dns"]!["rules"]![0]!;
            Assert.Equal("reject", dnsReject["action"]!.GetValue<string>());
            Assert.Equal(tag, dnsReject["rule_set"]![0]!.GetValue<string>());
            Assert.Equal(stale ? 1 : 0, refresh.Requests);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        });
    }

    [Fact]
    public void ImportedLocalRuleSetKeepsItsSourceDirectoryAfterCopy()
    {
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            var sourceDir = Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "import"));
            var source = Path.Combine(sourceDir.FullName, "external.json");
            const string raw = """
                {"route":{"rule_set":[{"type":"local","tag":"personal","format":"source","path":"rules/local.json"},
                    {"type":"inline","tag":"inline","rules":[{"domain":"example.test"}]}]}}
                """;
            File.WriteAllText(source, raw);
            var copied = CustomConfigInjector.CopyToProgramData(source, "relative-rules");
            Assert.Equal(raw, File.ReadAllText(source));
            File.Delete(source);

            var saved = CustomConfigInjector.ReadImport(copied);
            Assert.Equal(saved, File.ReadAllText(copied));
            var sets = JsonNode.Parse(saved)!["route"]!["rule_set"]!;
            Assert.Equal(Path.Combine(sourceDir.FullName, "rules", "local.json"), (string?)sets[0]!["path"]);
            Assert.Equal("inline", (string?)sets[1]!["type"]);
        });
    }

    [Fact]
    public void ReimportChecksAdFilterBeforeRoutingAndAfterConditionalSniff()
    {
        const string source = """
            {"route":{"rules":[{"network":"udp","outbound":"proxy"},{"action":"sniff","network":"tcp"},
                               {"network":"tcp","outbound":"proxy"}]},
             "outbounds":[{"type":"socks","tag":"proxy","server":"10.0.0.2","server_port":1080}]}
            """;
        RuleSetSamples.WithBuiltInGeoFiles(() =>
        {
            string Inject(string raw) => CustomConfigInjector.Inject(raw, [], BuildSettings(true, false),
                ConfigGenerator.RejectMissingRuleSet, resolveRuleSet: (_, _) => AppPaths.GeoSiteRuPath);
            var config = JsonNode.Parse(Inject(Inject(source)))!;
            Assert.Single(config["route"]!["rule_set"]!.AsArray());
            var rules = config["route"]!["rules"]!.AsArray();
            Assert.Equal(new[] { "hijack-dns", "sniff", "hijack-dns", "reject", "udp", "sniff", "reject", "tcp" },
                rules.Select(r => (string?)r?["action"] ?? (string?)r?["network"]));
            Assert.Equal(53, (int?)rules[0]!["port"]);
            Assert.Equal("dns", (string?)rules[2]!["protocol"]);
            Assert.Equal("tcp", (string?)rules[5]!["network"]);
            Assert.Single(config["dns"]!["rules"]!.AsArray(), r => (string?)r?["action"] == "reject");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedAdFilterMarksOnlyIncompleteEnabledCandidates(bool enabled)
    {
        var settings = BuildSettings(enabled, false);
        int reads = 0;
        string? Missing(string url, string file) { reads++; return null; }
        var omissions = new List<string>();
        var cold = CustomConfigInjector.Inject("{}", [], settings, omissions.Add, resolveRuleSet: Missing);
        Assert.Equal(enabled ? 1 : 0, reads);
        Assert.Equal(enabled ? 1 : 0, omissions.Count);
        Assert.DoesNotContain("vpnrouter-adblock", cold);
        if (enabled)
            Assert.Throws<InvalidOperationException>(() => CustomConfigInjector.Inject("{}", [], settings,
                ConfigGenerator.RejectMissingRuleSet, resolveRuleSet: Missing));
        else
            Assert.Equal(cold, CustomConfigInjector.Inject("{}", [], settings,
                ConfigGenerator.RejectMissingRuleSet, resolveRuleSet: Missing));
        Assert.Equal(enabled ? 2 : 0, reads);
    }

    [Theory]
    [InlineData("custom_first")]
    [InlineData("toggles_first")]
    public void MissingAdFilterBlocksReplacementAndMarksColdFallback(string priority)
    {
        var settings = BuildSettings(blockAds: true, bypassRu: false);
        settings.App.CustomRulesPriority = priority;
        SingBoxConfig Generate(Action<string>? onMissing) =>
            ConfigGenerator.Generate(BuildProfile(), [], settings, null, null, (_, _) => null, onMissing);

        Assert.Throws<InvalidOperationException>(() => Generate(ConfigGenerator.RejectMissingRuleSet));
        var missing = new List<string>();
        var cold = Generate(missing.Add);
        Assert.Single(missing);
        Assert.DoesNotContain(cold.Route.RuleSet ?? [], entry => entry.Tag == "vpnrouter-adblock");
        Assert.DoesNotContain(cold.Dns.Rules, rule => rule.RuleSet?.Contains("vpnrouter-adblock") == true);
        Assert.DoesNotContain(cold.Route.Rules, rule => rule.RuleSet?.Contains("vpnrouter-adblock") == true);
    }

    [Fact]
    public void DisabledPoliciesCanReplaceWithoutReadingRuleSets()
    {
        var settings = BuildSettings(blockAds: false, bypassRu: false);
        var config = ConfigGenerator.Generate(BuildProfile(), [], settings, null, null,
            (_, _) => throw new InvalidOperationException("Unexpected ad-filter lookup"),
            ConfigGenerator.RejectMissingRuleSet,
            (_, _) => throw new InvalidOperationException("Unexpected geo lookup"));
        Assert.DoesNotContain(config.Route.RuleSet ?? [], entry => entry.Tag is
            "vpnrouter-adblock" or "vpnrouter-geoip-ru" or "vpnrouter-geosite-ru");

        var imported = CustomConfigInjector.Inject("{}", [], settings, ConfigGenerator.RejectMissingRuleSet,
            (_, _) => throw new InvalidOperationException("Unexpected geo lookup"));
        Assert.DoesNotContain("vpnrouter-geoip-ru", imported);
        Assert.DoesNotContain("vpnrouter-geosite-ru", imported);
    }

    [Theory]
    [InlineData("custom_first")]
    [InlineData("toggles_first")]
    public void Generate_PartialDirectAndCompleteBlock_EmitOnlyRegisteredReferences(string priority)
    {
        var settings = BuildSettings(false, false,
        [
            new() { Type = "geoip", Action = "direct", Value = "first, missing" },
            new() { Type = "geosite", Action = "block", Value = "first, second" },
        ]);
        settings.App.CustomRulesPriority = priority;
        var config = ConfigGenerator.Generate(BuildProfile(), [], settings, null, null,
            (_, file) => file == "user-geoip-missing.srs" ? null : Path.Combine("cache", file));

        var tags = config.Route.RuleSet!.Select(entry => entry.Tag);
        Assert.Equal(new[] { "user-geoip-first", "user-geosite-first", "user-geosite-second" }, tags);
        Assert.All(config.Route.RuleSet!, entry => Assert.Equal("local", entry.Type));
        var direct = Assert.Single(config.Route.Rules, r => r.RuleSet != null && r.Outbound == "direct");
        Assert.Equal(new[] { "user-geoip-first" }, direct.RuleSet);
        var reject = Assert.Single(config.Route.Rules, r => r.RuleSet != null && r.Action == "reject");
        var dns = Assert.Single(config.Dns.Rules, r => r.RuleSet != null);
        Assert.Equal(new[] { "user-geosite-first", "user-geosite-second" }, reject.RuleSet);
        Assert.Equal(reject.RuleSet, dns.RuleSet);
        Assert.Equal("reject", dns.Action);
    }

    [Theory]
    [InlineData("geosite", "block", true)]
    [InlineData("geosite", "block", false)]
    [InlineData("geoip", "block", true)]
    [InlineData("geoip", "block", false)]
    [InlineData("geosite", "proxy", true)]
    [InlineData("geosite", "proxy", false)]
    [InlineData("geoip", "proxy", true)]
    [InlineData("geoip", "proxy", false)]
    public void Generate_RequiresEveryProtectiveSet(string type, string action, bool firstAvailable)
    {
        var settings = BuildSettings(false, false,
            [new CustomRule { Type = type, Action = action, Value = "first, second" }]);
        string? Resolve(string url, string file) => firstAvailable && file == $"user-{type}-first.srs" ? file : null;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConfigGenerator.Generate(BuildProfile(), [], settings, null, null, Resolve));

        Assert.Contains(firstAvailable ? $"user-{type}-second" : $"user-{type}-first", error.Message);
    }
}
