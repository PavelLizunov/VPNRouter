using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorRemoteRuleSetGuardTests : IDisposable
{
    private readonly string _origDataDir;
    private readonly string _testDir;

    public ConfigGeneratorRemoteRuleSetGuardTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "vpnr-cfggen-rs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _origDataDir = Environment.GetEnvironmentVariable("ProgramData") ?? "";
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

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
        var profile = BuildProfile();
        var processes = new[] { "Discord.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);
        var ruleSet = config.Route.RuleSet ?? new List<RuleSetEntry>();

        var remoteEntries = ruleSet
            .Where(rs => string.Equals(rs.Type, "remote", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(remoteEntries.Count == 0,
            $"Found {remoteEntries.Count} type:remote rule-set entries — these crash sing-box on TLS timeout. " +
            $"Tags: {string.Join(", ", remoteEntries.Select(r => r.Tag))}. " +
            "Route through RuleSetCacheManager + emit type:local instead.");
    }

    [Fact]
    public void Generate_WithCustomGeositeRule_NoRemoteRuleSets()
    {
        var customRules = new List<CustomRule>
        {
            new() { Enabled = true, Type = "geosite", Action = "direct", Value = "ru,cn" },
            new() { Enabled = true, Type = "geoip",   Action = "direct", Value = "ru" },
        };
        var settings = BuildSettings(blockAds: false, bypassRu: false, customRules: customRules);
        var profile = BuildProfile();
        var processes = new[] { "Discord.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);
        var ruleSet = config.Route.RuleSet ?? new List<RuleSetEntry>();

        var remoteEntries = ruleSet
            .Where(rs => string.Equals(rs.Type, "remote", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(remoteEntries.Count == 0,
            $"Found {remoteEntries.Count} type:remote rule-set entries from custom geosite/geoip rules. " +
            $"Tags: {string.Join(", ", remoteEntries.Select(r => r.Tag))}.");
    }
}
