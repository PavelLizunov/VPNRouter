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
        _origDataDir = AppPaths.DataDir;
        _testDir = Path.Combine(Path.GetTempPath(), "vpnr-cfggen-rs-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideDataDir(_testDir);
        try
        {
            var cache = Path.Combine(AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir);
            Directory.CreateDirectory(cache);
            foreach (var name in new[] { "adblock_reject.srs", "user-geosite-ru.srs", "user-geosite-cn.srs", "user-geoip-ru.srs" })
                File.WriteAllBytes(Path.Combine(cache, name), new byte[] { 1 });
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, new byte[10 * 1024]);
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, new byte[100]);
            // Nonempty fresh files exercise the cache-hit/config-shape path.
            // They are not valid SRS payloads and are never passed to sing-box.
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_origDataDir);
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void Fixture_UsesPrivateSeededRuleSets()
    {
        // Fail before generation if the fixture still shares testhost AppPaths.
        Assert.Equal(_testDir, AppPaths.DataDir);
        foreach (var name in new[] { "adblock_reject.srs", "user-geosite-ru.srs", "user-geosite-cn.srs", "user-geoip-ru.srs" })
        {
            var path = Path.Combine(AppPaths.CacheDir, RuleSetCacheManager.CacheSubdir, name);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
            Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RuleSetCacheManager.MaxAgeForUseAsIs);
        }
        Assert.True(GeoDataDownloader.AreGeoFilesAvailable());
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
        Fixture_UsesPrivateSeededRuleSets(); // Fail before a cache miss can reach HTTP.
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
        var expectedTags = new List<string>();
        if (blockAds) expectedTags.Add("vpnrouter-adblock");
        if (bypassRu) expectedTags.AddRange(new[] { "vpnrouter-geoip-ru", "vpnrouter-geosite-ru" });
        AssertLocalEntries(ruleSet, expectedTags);
    }

    [Fact]
    public void Generate_WithCustomGeositeRule_NoRemoteRuleSets()
    {
        Fixture_UsesPrivateSeededRuleSets();
        // Seeded custom sets must be emitted, not silently omitted.
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
        AssertLocalEntries(ruleSet, new[] { "user-geosite-ru", "user-geosite-cn", "user-geoip-ru" });
    }

    private void AssertLocalEntries(List<RuleSetEntry> entries, IEnumerable<string> expectedTags)
    {
        Assert.Equal(expectedTags.OrderBy(tag => tag), entries.Select(entry => entry.Tag).OrderBy(tag => tag));
        foreach (var entry in entries)
        {
            Assert.Equal("local", entry.Type);
            Assert.Equal("binary", entry.Format);
            Assert.NotNull(entry.Path);
            var relative = Path.GetRelativePath(_testDir, entry.Path!);
            Assert.False(Path.IsPathRooted(relative));
            Assert.NotEqual("..", relative);
            Assert.False(relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var expectedBytes = entry.Tag switch
            {
                "vpnrouter-geoip-ru" => new byte[10 * 1024],
                "vpnrouter-geosite-ru" => new byte[100],
                _ => new byte[] { 1 }
            };
            Assert.Equal(expectedBytes, File.ReadAllBytes(entry.Path!));
        }
    }
}
