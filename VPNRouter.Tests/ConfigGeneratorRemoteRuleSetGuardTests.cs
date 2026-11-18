using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
