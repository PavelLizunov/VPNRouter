// Phase 3E (2026-05-18) — CacheMergeStage unit tests.
//
// The stage must produce the same behaviour as the inline cache-merge
// logic that lived in FreeConfigAggregator.RefreshAsync pre-3E:
//
//   - Fresh entries with cache hits inherit FirstSeenAt + CountryCode +
//     ResolvedIp + Status + LatencyMs + LastTestedAt + bandwidth fields +
//     LastDeepVerifyAt + LastVerifyFailedAt.
//   - Cache-only Verified entries survive into the output.
//   - Cache-only Ok entries tested in the last 24h survive.
//   - Cache-only Ok-stale, TlsFailed, Timeout, Unreachable entries drop.
//
// The cross-cutting "PreservePreviousValidation" contract still lives in
// FreeConfigAggregatorPreserveTests.cs (those tests call the static
// directly). This file pins the surrounding wiring inside CacheMergeStage.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Tests;

public class FreeConfigCacheMergeStageTests : IDisposable
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _tmpDir;
    private readonly string _cachePath;
    private readonly FreeConfigCache _cache;

    public FreeConfigCacheMergeStageTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-stages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _cachePath = Path.Combine(_tmpDir, "free_configs.json");
        _cache = new FreeConfigCache(Logger, _cachePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private StageContext Ctx(IReadOnlyList<FreeConfigEntry> input) => new(
        Input: input,
        Settings: new AppSettings(),
        Cache: _cache,
        Sources: Array.Empty<FreeConfigSource>(),
        Logger: Logger);

    private static FreeConfigEntry MakeEntry(
        string id,
        FreeConfigStatus status = FreeConfigStatus.Unknown,
        int latencyMs = 0,
        DateTime? lastTestedAt = null,
        string? cc = null) =>
        new()
        {
            Id = id,
            Host = $"h-{id}",
            Port = 443,
            Uuid = $"u-{id}",
            Status = status,
            LatencyMs = latencyMs,
            LastTestedAt = lastTestedAt,
            CountryCode = cc,
        };

    [Fact]
    public async Task FreshOnly_NoCache_ReturnsInputUnchanged()
    {
        var stage = new CacheMergeStage();
        var input = new List<FreeConfigEntry>
        {
            MakeEntry("fresh1"),
            MakeEntry("fresh2"),
        };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Output.Count);
        Assert.All(result.Output, e => Assert.Equal(FreeConfigStatus.Unknown, e.Status));
    }

    [Fact]
    public async Task FreshAndCache_OverlapId_InheritsCacheFields()
    {
        var prev = MakeEntry(
            "shared",
            status: FreeConfigStatus.Verified,
            latencyMs: 42,
            lastTestedAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            cc: "NL");
        _cache.Save(new FreeConfigCache.CacheFile { Configs = new() { prev } });

        var stage = new CacheMergeStage();
        var input = new List<FreeConfigEntry> { MakeEntry("shared") }; // status=Unknown

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        // The fresh entry's status/latency/cc was overwritten with the
        // cached values per the inherit-cache pass.
        Assert.Equal(FreeConfigStatus.Verified, result.Output[0].Status);
        Assert.Equal(42, result.Output[0].LatencyMs);
        Assert.Equal("NL", result.Output[0].CountryCode);
    }

    [Fact]
    public async Task CacheOnlyVerified_Survives()
    {
        var cached = MakeEntry(
            "v-only",
            status: FreeConfigStatus.Verified,
            lastTestedAt: DateTime.UtcNow.AddDays(-30));
        _cache.Save(new FreeConfigCache.CacheFile { Configs = new() { cached } });

        var stage = new CacheMergeStage();
        var input = new List<FreeConfigEntry> { MakeEntry("fresh-other") };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Output.Count);
        Assert.Contains(result.Output, e => e.Id == "v-only" && e.Status == FreeConfigStatus.Verified);
    }

    [Fact]
    public async Task CacheOnlyOkStale_Dropped()
    {
        var cached = MakeEntry(
            "stale",
            status: FreeConfigStatus.Ok,
            lastTestedAt: DateTime.UtcNow.AddHours(-48));
        _cache.Save(new FreeConfigCache.CacheFile { Configs = new() { cached } });

        var stage = new CacheMergeStage();
        var input = new List<FreeConfigEntry> { MakeEntry("fresh-other") };

        var result = await stage.RunAsync(Ctx(input), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        Assert.Equal("fresh-other", result.Output[0].Id);
    }

    [Fact]
    public async Task EmptyInput_AndCachedVerified_PreservesCache()
    {
        // Edge case: pool fetch failed completely, but cache has Verified
        // entries. Cache-merge must still surface them — the user's earned
        // verification effort should not vanish on a pool blip.
        var cached = MakeEntry("v", status: FreeConfigStatus.Verified);
        _cache.Save(new FreeConfigCache.CacheFile { Configs = new() { cached } });

        var stage = new CacheMergeStage();

        var result = await stage.RunAsync(Ctx(Array.Empty<FreeConfigEntry>()), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Output);
        Assert.Equal(FreeConfigStatus.Verified, result.Output[0].Status);
    }
}
