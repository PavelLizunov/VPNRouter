using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// FreeConfigAggregator.PreservePreviousValidation — v2.28.3-r5 regression
//
// Triggering bug (2026-04-27): user re-ran Refresh with new criteria and lost
// their previously-Verified configs. Root cause: aggregator built byId from
// freshly-fetched pool only, so cache entries not in the new pool were
// silently dropped. The server-side pool.json regenerates every 6h and rotates
// entries, so verified results from yesterday could vanish after one Refresh.
//
// PreservePreviousValidation merges "interesting" cache entries back into the
// fresh-pool dictionary. These tests pin the contract:
//   - Verified entries always survive (regardless of age).
//   - Ok entries survive only if tested within the last 24h.
//   - Other statuses get dropped — they're not worth preserving.
//   - Entries already in byId aren't touched (live pool wins).
//   - Empty-id entries (corrupt cache) are skipped without throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public class FreeConfigAggregatorPreserveTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry MakeEntry(
        string id,
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status =
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
        DateTime? lastTestedAt = null)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = id,
            Host = $"host-{id}.example.com",
            Port = 443,
            Uuid = $"uuid-{id}",
            Status = status,
            LatencyMs = 100,
            LastTestedAt = lastTestedAt,
        };
    }

    private static readonly DateTime _now =
        new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Verified_PreservedRegardlessOfAge()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("v1",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
                lastTestedAt: _now.AddDays(-365)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
        Assert.Equal("v1", configs[0].Id);
        Assert.True(byId.ContainsKey("v1"));
    }

    [Fact]
    public void RecentOk_Preserved()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok1",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: _now.AddHours(-1)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
    }

    [Fact]
    public void StaleOk_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok-stale",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: _now.AddHours(-25)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }

    [Fact]
    public void OtherStatuses_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var fresh = _now.AddMinutes(-5);
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("tls",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,    lastTestedAt: fresh),
            MakeEntry("timeout", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout,      lastTestedAt: fresh),
            MakeEntry("unr",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unreachable,  lastTestedAt: fresh),
            MakeEntry("slow",    VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Slow,         lastTestedAt: fresh),
            MakeEntry("imp",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible,  lastTestedAt: fresh),
            MakeEntry("unk",     VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown,      lastTestedAt: null),
            MakeEntry("perr",    VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.ParseError,   lastTestedAt: fresh),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }

    [Fact]
    public void AlreadyInPool_NotTouched()
    {
        var freshEntry = MakeEntry("dup",
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Unknown);
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            ["dup"] = freshEntry,
        };
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry> { freshEntry };
        var cacheEntry = MakeEntry("dup",
            VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            lastTestedAt: _now);
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry> { cacheEntry };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Single(configs);
        Assert.Same(freshEntry, configs[0]);
        Assert.Same(freshEntry, byId["dup"]);
    }

    [Fact]
    public void MixedCache_OnlyEligibleSurvives()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("v1",        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,    lastTestedAt: _now.AddDays(-3)),
            MakeEntry("ok-recent", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,           lastTestedAt: _now.AddHours(-2)),
            MakeEntry("ok-stale",  VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,           lastTestedAt: _now.AddDays(-2)),
            MakeEntry("tls",       VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,    lastTestedAt: _now.AddMinutes(-5)),
            MakeEntry("imp",       VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Implausible,  lastTestedAt: _now.AddMinutes(-5)),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(2, n);
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.Id == "v1");
        Assert.Contains(configs, c => c.Id == "ok-recent");
    }

    [Fact]
    public void EmptyId_Skipped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("",   VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, lastTestedAt: _now),
            MakeEntry("ok", VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, lastTestedAt: _now),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(1, n);
        Assert.Single(configs);
        Assert.Equal("ok", configs[0].Id);
        Assert.False(byId.ContainsKey(string.Empty));
    }

    [Fact]
    public void EmptyCache_NoOp()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
        Assert.Empty(byId);
    }

    [Fact]
    public void OkWithNullTimestamp_Dropped()
    {
        var byId = new Dictionary<string, VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var configs = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>();
        var cache = new List<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>
        {
            MakeEntry("ok-no-ts",
                VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
                lastTestedAt: null),
        };

        var n = VPNRouter.Core.Services.FreeConfigs.FreeConfigAggregator
            .PreservePreviousValidation(byId, configs, cache, _now);

        Assert.Equal(0, n);
        Assert.Empty(configs);
    }

    // ─── DATA-4: MergeWithCache duplicate-ID tolerance ─────────────────

    [Fact]
    public void MergeWithCache_DuplicateIds_FirstWins_VerifiedPreserved()
    {
        var tempDir = Directory.CreateTempSubdirectory("vpnrouter-test-");
        try
        {
            using var logger = new LoggerConfiguration().CreateLogger();
            var cache = new FreeConfigCache(
                logger, Path.Combine(tempDir.FullName, "free_configs.json"));
            var aggregator = new FreeConfigAggregator(logger, cache);

            cache.Save(new FreeConfigCache.CacheFile
            {
                Configs =
                {
                    MakeEntry("verified-gone",
                        FreeConfigStatus.Verified,
                        lastTestedAt: _now.AddDays(-30)),
                    MakeEntry("cached-dup",
                        FreeConfigStatus.Ok,
                        lastTestedAt: _now.AddHours(-1)),
                    MakeEntry("cached-dup"),
                },
            });

            var first = MakeEntry("dup");
            first.Host = "first.example.com";
            var second = MakeEntry("dup");
            second.Host = "second.example.com";

            var result = aggregator.MergeWithCache(
                new List<FreeConfigEntry> { first, second });

            var dups = result.Where(c => c.Id == "dup").ToList();
            Assert.Single(dups);
            Assert.Equal("first.example.com", dups[0].Host);
            Assert.Contains(result, c => c.Id == "verified-gone"
                && c.Status == FreeConfigStatus.Verified);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }
}
