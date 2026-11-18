using Serilog;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;

public class FreeConfigCacheMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_LegacyVerdictKeepsProfileAndRequiresFreshVerification(bool asyncRead)
    {
        var dir = Directory.CreateTempSubdirectory("vpnrouter-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "free_configs.json");
            var raw = $"vless://{Guid.Empty:D}@profile.example.com:443?security=tls";
            var tested = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(path, $$"""
                {"schema_version":1,"Configs":[{"Id":"saved","RawUri":"{{raw}}","Status":8,
                 "LastTestedAt":"{{tested:O}}","LastDeepVerifyAt":"{{tested:O}}","LatencyMs":42,"MeasuredBandwidthMbps":20}]}
                """);
            using var logger = new LoggerConfiguration().CreateLogger();
            var cache = new FreeConfigCache(logger, path);
            var file = asyncRead ? await cache.LoadAsync(TestContext.Current.CancellationToken) : cache.Load();
            var entry = Assert.Single(file.Configs);
            Assert.Equal(raw, entry.RawUri);
            Assert.Equal("profile.example.com", entry.ToVlessServerEntry().Server);
            Assert.True(entry.RequiresSavedRecheck);
            Assert.Equal(tested, entry.LastTestedAt);
            Assert.True(FreeConfigFreshness.IsStale(entry, tested));
            Assert.False(FreeConfigKeepPolicy.ShouldKeepInLiveCache(entry));
            Assert.True(FreeConfigKeepPolicy.ShouldRetainInSavedList(entry, tested.AddDays(30)));
            Assert.False(FreeConfigKeepPolicy.ShouldRetainInSavedList(entry, tested.AddDays(30).AddTicks(1)));
            Assert.Equal(FreeConfigStatus.Unknown, entry.Status);
            Assert.Null(entry.LastDeepVerifyAt);
            Assert.Null(entry.MeasuredBandwidthMbps);
            Assert.Equal(0, entry.LatencyMs);
            cache.Save(file);
            var reopened = cache.Load();
            Assert.Equal(FreeConfigCache.CurrentSchemaVersion, reopened.SchemaVersion);
            var saved = Assert.Single(reopened.Configs);
            Assert.True(saved.RequiresSavedRecheck);
            Assert.Equal(FreeConfigStatus.Unknown, saved.Status);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_WithCorruptedSubThresholdLatencies_ResetsToZero()
    {
        var dir = Directory.CreateTempSubdirectory("vpnrouter-test-");
        try
        {
            using var logger = new LoggerConfiguration().CreateLogger();
            var cache = new FreeConfigCache(logger, Path.Combine(dir.FullName, "free_configs.json"));
            var latencies = new[] { 1, 4, 0, 5, 42 };
            cache.Save(new FreeConfigCache.CacheFile
            {
                Configs = latencies.Select(ms => new FreeConfigEntry { LatencyMs = ms }).ToList()
            });

            Assert.Equal(new[] { 0, 0, 0, 5, 42 }, cache.Load().Configs.Select(e => e.LatencyMs));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
