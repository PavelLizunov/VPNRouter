using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 5 — FreeConfigFreshness pure-logic tests
// ═══════════════════════════════════════════════════════════════════════════════
//
// All freshness math (tier classification, opacity, sort key, recheck-merge)
// lives in Core so it's testable without an Avalonia headless harness. The
// App's FreeConfigItemViewModel just delegates its getters.

public class FreeConfigFreshnessTierTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        DateTime? lastTestedAt,
        DateTime? lastVerifyFailedAt = null)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastTestedAt = lastTestedAt,
            LastVerifyFailedAt = lastVerifyFailedAt,
        };
    }

    [Theory]
    [InlineData(0)]              // freshly tested
    [InlineData(0.5)]            // half a day
    [InlineData(0.99)]           // just under 1 day
    public void Tier_Fresh_When_Under_24h(double daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Tier_Ageing_When_Between_1d_And_7d(int daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Ageing,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(29)]
    public void Tier_Stale_When_Over_7d(int daysAgo)
    {
        var entry = Make(Now.AddDays(-daysAgo));
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Stale,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Failed_When_LastVerifyFailedAt_Greater_Than_LastTested()
    {
        var entry = Make(Now.AddHours(-1), Now); // verified an hour ago, failed now
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Failed_Wins_Over_Fresh_Age()
    {
        // Even if LastTestedAt is in the future (fresh), a failure timestamp
        // ≥ tested makes it Failed. Defensive: locks the comparison rule.
        var entry = Make(Now, Now);
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Fresh_When_LastTestedAt_Null()
    {
        // Defensive: post-import / pre-Phase-1 entries with null timestamp
        // are surfaced as Fresh rather than dropped from the tier system.
        var entry = Make(null);
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(entry, Now));
    }

    [Fact]
    public void Tier_Null_Entry_Returns_Fresh_Default()
    {
        Assert.Equal(
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,
            VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.ClassifyTier(null!, Now));
    }

    [Theory]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Fresh,  1.0)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Ageing, 0.75)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Stale,  0.5)]
    [InlineData(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier.Failed, 0.5)]
    public void Opacity_Tracks_Tier(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshnessTier tier, double expected)
    {
        Assert.Equal(expected, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.OpacityFor(tier));
    }

    [Fact]
    public void IsStale_True_For_Over_24h()
    {
        var entry = Make(Now.AddHours(-25));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_False_For_Under_24h()
    {
        var entry = Make(Now.AddHours(-23));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_True_For_FailedLastCheck()
    {
        // Even a freshly verified entry is "stale" if it failed the most
        // recent recheck — the bulk-Recheck button picks it up too.
        var entry = Make(Now.AddHours(-1), Now);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void IsStale_True_For_NullLastTested()
    {
        var entry = Make(null);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.IsStale(entry, Now));
    }

    [Fact]
    public void SortKey_Fresh_Lower_Than_Ageing_Lower_Than_Stale_Lower_Than_Failed()
    {
        var fresh  = Make(Now.AddHours(-1));     fresh.LatencyMs  = 10;
        var ageing = Make(Now.AddDays(-3));      ageing.LatencyMs = 10;
        var stale  = Make(Now.AddDays(-10));     stale.LatencyMs  = 10;
        var failed = Make(Now.AddHours(-1), Now); failed.LatencyMs = 10;

        var k1 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(fresh,  Now);
        var k2 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(ageing, Now);
        var k3 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(stale,  Now);
        var k4 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(failed, Now);

        Assert.True(k1 < k2);
        Assert.True(k2 < k3);
        Assert.True(k3 < k4);
    }

    [Fact]
    public void SortKey_Within_Tier_Orders_By_Latency()
    {
        // Two fresh entries differ only in latency → lower latency sorts first.
        var fast = Make(Now.AddHours(-1)); fast.LatencyMs = 10;
        var slow = Make(Now.AddHours(-1)); slow.LatencyMs = 200;

        var kf = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(fast, Now);
        var ks = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.SortKey(slow, Now);

        Assert.True(kf < ks);
    }

    [Fact]
    public void AgeDays_Returns_Floored_Days()
    {
        var entry = Make(Now.AddDays(-3.7));
        Assert.Equal(3, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(entry, Now));
    }

    [Fact]
    public void AgeDays_Returns_0_For_Null_Or_Future()
    {
        Assert.Equal(0, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(Make(null), Now));
        // Future timestamp (clock skew defensive): still returns 0.
        Assert.Equal(0, VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.AgeDays(Make(Now.AddDays(1)), Now));
    }
}
