using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 1 — Saved-list retention policy + LastVerifyFailedAt schema
// ═══════════════════════════════════════════════════════════════════════════════
//
// The Сохранённые tab persists Verified entries across sessions, capped at
// FreeConfigKeepPolicy.SavedListRetentionDays (=30). At cache-load time
// (FreeConfigsPageViewModel.EnsureCacheLoaded) entries beyond the cap are
// silently dropped. Phase 1 introduces this policy and the
// LastVerifyFailedAt schema field that Phase 3 will use for the
// "failed last check" badge.
public class FreeConfigSavedRetentionTests
{
    private static VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry Make(
        VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus status,
        DateTime? lastTestedAt)
    {
        return new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = status,
            LastTestedAt = lastTestedAt,
        };
    }

    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Verified_FreshlyTested_Retained()
    {
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddHours(-1));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_29Days_Retained()
    {
        // Just under the 30-day cap.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-29));
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_31Days_Dropped()
    {
        // Just past the 30-day cap.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            Now.AddDays(-31));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Verified_NullLastTested_Retained()
    {
        // Defensive: post-import or pre-Phase-1 entries with null timestamp
        // are kept rather than nuked. The next search will set the timestamp.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, null);
        Assert.True(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void NonVerified_Dropped_RegardlessOfAge()
    {
        // Even a fresh Ok entry shouldn't reach the saved list — the saved
        // list is for things that proved real connectivity, not just TCP+TLS.
        var entry = Make(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Ok,
            Now.AddHours(-1));
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(entry, Now));
    }

    [Fact]
    public void Null_Dropped()
    {
        Assert.False(VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .ShouldRetainInSavedList(null!, Now));
    }

    [Fact]
    public void RetentionDays_Const_Is30()
    {
        // Pin the public constant — if we ever need to bump it, surface
        // the change in code review (and update the saved-list tooltip).
        Assert.Equal(30, VPNRouter.Core.Services.FreeConfigs.FreeConfigKeepPolicy
            .SavedListRetentionDays);
    }
}
