#nullable enable
// ============================================================================
// ZapretProbeCacheTests.cs — v2.37.0-r6 (2026-05-25)
// ============================================================================
//
// Tests for ZapretProbeCache (warm-start cache for the magic-button probe).
// Covers TryLoad / RecordSuccess / RecordFailure / Clear plus the
// IsRecentAndReliable predicate.
//
// Cache lives at %ProgramData%\VPNRouter\cache\zapret_probe.json — these
// tests redirect AppPaths.DataDir to a per-test temp directory via
// AppPaths.OverrideDataDir so they don't trash the live cache and so xUnit
// parallel runs don't collide.
// ============================================================================

using System;
using System.IO;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ZapretProbeCacheTests : IDisposable
{
    private readonly string _tempDataDir;
    private readonly string _originalDataDir;

    public ZapretProbeCacheTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(Path.GetTempPath(),
            $"vpnrouter-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    public void Dispose()
    {
        // Restore the original so subsequent test classes don't see our
        // hijacked DataDir.
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── TryLoad ─────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_NoCacheFile_ReturnsNull()
    {
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsNullGracefully()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(
            Path.Combine(AppPaths.CacheDir, "zapret_probe.json"),
            "{not valid json...");
        // Must not throw.
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [Fact]
    public void TryLoad_EmptyStrategy_ReturnsNull()
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(
            Path.Combine(AppPaths.CacheDir, "zapret_probe.json"),
            "{\"Strategy\":\"\",\"SuccessRunCount\":5,\"LastSweepAt\":\"2026-05-25T00:00:00Z\"}");
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    // ── RecordSuccess ───────────────────────────────────────────────────────

    [Fact]
    public void RecordSuccess_FreshEntry_PersistsWithCountOne()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal("general (ALT3)", loaded!.Strategy);
        Assert.Equal(1, loaded.SuccessRunCount);
        Assert.Equal(0, loaded.LastFailureCount);
    }

    [Fact]
    public void RecordSuccess_SameStrategyTwice_BumpsCount()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal(2, loaded!.SuccessRunCount);
        Assert.Equal(0, loaded.LastFailureCount);
    }

    [Fact]
    public void RecordSuccess_DifferentStrategy_ResetsCount()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordSuccess("general (FAKE TLS AUTO)"); // different
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal("general (FAKE TLS AUTO)", loaded!.Strategy);
        Assert.Equal(1, loaded.SuccessRunCount);
    }

    [Fact]
    public void RecordSuccess_AfterFailure_ResetsFailureCount()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordFailure("general (ALT3)");
        ZapretProbeCache.RecordFailure("general (ALT3)");
        var beforeRecover = ZapretProbeCache.TryLoad();
        Assert.Equal(2, beforeRecover!.LastFailureCount);

        ZapretProbeCache.RecordSuccess("general (ALT3)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal(0, loaded!.LastFailureCount);
        Assert.Equal(2, loaded.SuccessRunCount); // 1 + 1 (success counted)
    }

    [Fact]
    public void RecordSuccess_EmptyStrategy_NoOp()
    {
        ZapretProbeCache.RecordSuccess("");
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    // ── RecordFailure ───────────────────────────────────────────────────────

    [Fact]
    public void RecordFailure_NoCache_NoOp()
    {
        // No prior cache, no exception, no file written.
        ZapretProbeCache.RecordFailure("general (ALT3)");
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [Fact]
    public void RecordFailure_DifferentStrategy_NoOp()
    {
        // Cache has X, failure of Y is irrelevant (would only matter if Y
        // had been cached as winner). Don't bump X's failure count.
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordFailure("general (FAKE TLS AUTO)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal("general (ALT3)", loaded!.Strategy);
        Assert.Equal(0, loaded.LastFailureCount);
    }

    [Fact]
    public void RecordFailure_SameStrategy_BumpsFailureCount()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        ZapretProbeCache.RecordFailure("general (ALT3)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal(1, loaded!.LastFailureCount);
    }

    // ── IsRecentAndReliable ─────────────────────────────────────────────────

    [Fact]
    public void IsRecentAndReliable_FreshSuccess_True()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            SuccessRunCount = 1,
            LastFailureCount = 0,
            LastSweepAt = DateTime.UtcNow.AddMinutes(-5),
        };
        Assert.True(entry.IsRecentAndReliable());
    }

    [Fact]
    public void IsRecentAndReliable_OlderThanSevenDays_False()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            SuccessRunCount = 5,
            LastFailureCount = 0,
            LastSweepAt = DateTime.UtcNow.AddDays(-8),
        };
        Assert.False(entry.IsRecentAndReliable());
    }

    [Fact]
    public void IsRecentAndReliable_ZeroSuccess_False()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            SuccessRunCount = 0,
            LastFailureCount = 0,
            LastSweepAt = DateTime.UtcNow,
        };
        Assert.False(entry.IsRecentAndReliable());
    }

    [Fact]
    public void IsRecentAndReliable_ThreeConsecutiveFails_False()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            SuccessRunCount = 5,
            LastFailureCount = 3,
            LastSweepAt = DateTime.UtcNow,
        };
        Assert.False(entry.IsRecentAndReliable());
    }

    [Fact]
    public void IsRecentAndReliable_TwoConsecutiveFails_True()
    {
        // Two failures are still tolerable — one more demotes the entry.
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            SuccessRunCount = 5,
            LastFailureCount = 2,
            LastSweepAt = DateTime.UtcNow,
        };
        Assert.True(entry.IsRecentAndReliable());
    }

    [Fact]
    public void IsRecentAndReliable_EmptyStrategy_False()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "",
            SuccessRunCount = 5,
            LastFailureCount = 0,
            LastSweepAt = DateTime.UtcNow,
        };
        Assert.False(entry.IsRecentAndReliable());
    }

    // ── Clear ───────────────────────────────────────────────────────────────

    [Fact]
    public void Clear_RemovesCacheFile()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        Assert.NotNull(ZapretProbeCache.TryLoad());
        ZapretProbeCache.Clear();
        Assert.Null(ZapretProbeCache.TryLoad());
    }

    [Fact]
    public void Clear_NoCache_NoThrow()
    {
        // Idempotent — must not throw on missing file.
        ZapretProbeCache.Clear();
        ZapretProbeCache.Clear();
    }

    // ── r24 — schema v2 score fields ────────────────────────────────────────

    [Fact]
    public void RecordSuccess_WithScore_PersistsTargetsFields()
    {
        ZapretProbeCache.RecordSuccess("general (ALT3)", targetsPassed: 4, targetsTotal: 5);
        var loaded = ZapretProbeCache.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.TargetsPassed);
        Assert.Equal(5, loaded.TargetsTotal);
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.True(loaded.HasTargetScore());
    }

    [Fact]
    public void RecordSuccess_NoScoreOverload_DoesNotSetTargets()
    {
        // Backward-compat overload must persist with targets=0 so the Hero
        // card knows not to render the "X из Y" line for legacy paths.
        ZapretProbeCache.RecordSuccess("general (ALT3)");
        var loaded = ZapretProbeCache.TryLoad();
        Assert.Equal(0, loaded!.TargetsPassed);
        Assert.Equal(0, loaded.TargetsTotal);
        Assert.False(loaded.HasTargetScore());
    }

    [Fact]
    public void IsStale_OlderThanSevenDays_True()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            LastSweepAt = DateTime.UtcNow.AddDays(-8),
        };
        Assert.True(entry.IsStale());
    }

    [Fact]
    public void IsStale_FreshEntry_False()
    {
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "general (ALT3)",
            LastSweepAt = DateTime.UtcNow.AddMinutes(-30),
        };
        Assert.False(entry.IsStale());
    }

    [Fact]
    public void IsStale_EmptyStrategy_False()
    {
        // Empty entries don't trigger stale state — they're shown as
        // "не проверена" with the ◌ icon, not "⚠ устарела".
        var entry = new ZapretProbeCacheEntry
        {
            Strategy = "",
            LastSweepAt = DateTime.UtcNow.AddDays(-30),
        };
        Assert.False(entry.IsStale());
    }

    [Fact]
    public void HasTargetScore_TotalZero_False()
    {
        var entry = new ZapretProbeCacheEntry { TargetsPassed = 0, TargetsTotal = 0 };
        Assert.False(entry.HasTargetScore());
    }

    [Fact]
    public void HasTargetScore_AllTargetsFailed_StillTrue()
    {
        // 0/5 is a valid score — UI must render "0 из 5 целей" to show the
        // strategy was probed but didn't work. Only TotalTargets=0 means
        // "no score data" (e.g. legacy v1 cache).
        var entry = new ZapretProbeCacheEntry { TargetsPassed = 0, TargetsTotal = 5 };
        Assert.True(entry.HasTargetScore());
    }

    [Fact]
    public void TryLoad_LegacyV1Json_DeserializesWithZeroTargets()
    {
        // Verify backward-compat: a v1 cache file (no Targets* fields) must
        // load cleanly with both targets defaulting to 0. The HasTargetScore
        // helper then correctly suppresses the "X из Y" line.
        Directory.CreateDirectory(AppPaths.CacheDir);
        var legacyJson = @"{
            ""Strategy"":""general (ALT3)"",
            ""LastSuccessAt"":""2026-05-20T12:00:00Z"",
            ""LastSweepAt"":""2026-05-20T12:00:00Z"",
            ""SuccessRunCount"":3,
            ""LastFailureCount"":0,
            ""SchemaVersion"":1
        }";
        File.WriteAllText(Path.Combine(AppPaths.CacheDir, "zapret_probe.json"), legacyJson);
        var loaded = ZapretProbeCache.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal("general (ALT3)", loaded!.Strategy);
        Assert.Equal(0, loaded.TargetsPassed);
        Assert.Equal(0, loaded.TargetsTotal);
        Assert.False(loaded.HasTargetScore());
    }
}
