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
            var config = ConfigGenerator.Generate(BuildProfile(), ["Discord.exe"], settings, null, null,
                (_, filename) => Path.Combine("cache", filename), checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
            var entries = config.Route.RuleSet ?? [];
            Assert.All(entries, entry => Assert.Equal("local", entry.Type));
            Assert.Equal(blockAds, entries.Any(entry => entry.Tag == "vpnrouter-adblock"));
            Assert.Equal(bypassRu, entries.Any(entry => entry.Tag == "vpnrouter-geoip-ru"));
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
                    requireGeo: true, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));
            Assert.Throws<InvalidOperationException>(() =>
                CustomConfigInjector.Inject("{}", [], settings, requireGeo: true,
                    checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync));
        });
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
