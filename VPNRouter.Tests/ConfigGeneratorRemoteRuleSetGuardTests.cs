using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.31.9-r5 regression pin: <see cref="ConfigGenerator"/> must NEVER
/// emit <c>type:remote</c> rule-set entries.
///
/// <para>The brat-2026-05-05 P0 closed in -r3 was caused by an
/// AdBlock <c>type:remote</c> rule-set that sing-box treats as a
/// MANDATORY synchronous fetch on startup — TLS timeout = process
/// FATAL = HealthMonitor crash loop. -r3 routed the AdBlock URL
/// through <see cref="RuleSetCacheManager"/> + emitted <c>type:local</c>.
/// -r5 fixed the same pattern in
/// <see cref="ConfigGenerator.ApplyCustomRules"/> for user-defined
/// geosite / geoip rules.</para>
///
/// <para>This test scans the generated config across the
/// representative toggle matrix and asserts no rule-set ever has
/// <c>Type == "remote"</c>. A future feature that adds another
/// <c>type:remote</c> entry will fail here loudly.</para>
/// </summary>
public sealed class ConfigGeneratorRemoteRuleSetGuardTests : IDisposable
{
    private readonly string _origDataDir;
    private readonly string _testDir;

    public ConfigGeneratorRemoteRuleSetGuardTests()
    {
        // Isolate cache dir so the rule-set fetch in ApplyAdBlock does
        // not corrupt the user's real %ProgramData% during tests.
        _testDir = Path.Combine(Path.GetTempPath(), "vpnr-cfggen-rs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _origDataDir = Environment.GetEnvironmentVariable("ProgramData") ?? "";
        // We can't easily redirect AppPaths without DI; rely on the
        // fact that even if RuleSetCacheManager populates the real
        // %CacheDir%, we only assert the SHAPE of the generated config.
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
        // BypassRu silently disables itself if geo files aren't
        // available on the host (intentional gate). That's fine — we're
        // checking that whatever DOES end up in the config is type:local.
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
        // Even with user-defined geosite rules — which pre-r5 emitted
        // type:remote pointing at SagerNet's GitHub raw — the result
        // must be type:local (or omitted on cache+fetch failure).
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
