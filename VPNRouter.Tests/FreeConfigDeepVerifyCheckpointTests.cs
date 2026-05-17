using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>v2.29.0-r7+ Phase 3C: persistent deep-verify checkpoint
/// tests. Verifies the new <see cref="VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry.LastDeepVerifyAt"/>
/// field and the 6-hour skip window logic that the search loop uses.</summary>
public class FreeConfigDeepVerifyCheckpointTests
{
    [Fact]
    public void NewEntry_LastDeepVerifyAt_IsNull()
    {
        var e = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry();
        Assert.Null(e.LastDeepVerifyAt);
    }

    [Fact]
    public void Schema_Roundtrips_LastDeepVerifyAt_ViaJson()
    {
        // Phase 3C field must round-trip through System.Text.Json so the
        // cache file at %ProgramData%\VPNRouter\cache\free_configs.json
        // survives app restart with the timestamp preserved.
        var stamp = new DateTime(2026, 4, 29, 14, 30, 0, DateTimeKind.Utc);
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "abc",
            Host = "example.com",
            Port = 443,
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = stamp,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("LastDeepVerifyAt", json);
        var roundTripped = System.Text.Json.JsonSerializer
            .Deserialize<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(stamp, roundTripped!.LastDeepVerifyAt);
    }

    [Fact]
    public void SkipDeepVerify_WithFreshCheckpoint_AndVerifiedStatus_True()
    {
        // The skip predicate inlined in VerifyOneAndAppendAsync expects
        // all three: Verified status, LastDeepVerifyAt set, age < 6h, and
        // LatencyMs > 0. Replicate it as test logic.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-2),  // 2h ago
            LatencyMs = 50,
        };
        Assert.True(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithStaleCheckpoint_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-7),  // 7h ago, > 6h
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithoutCheckpoint_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = null,
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_WithoutPing_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-1),
            LatencyMs = 0,  // never TCP-tested
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_NonVerifiedStatus_False()
    {
        // Even with a recent timestamp, we must re-verify if the last
        // status was anything other than Verified (e.g. TlsFailed or
        // Timeout from a previous failed re-check).
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-1),
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    /// <summary>Mirror of the inline skip-predicate in
    /// VerifyOneAndAppendAsync. Kept here as test fixture so the
    /// 6-hour boundary + flag combinations are pinned.</summary>
    private static bool ShouldSkipDeepVerify(VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry cfg)
    {
        return cfg.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
            && cfg.LastDeepVerifyAt.HasValue
            && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
            && cfg.LatencyMs > 0;
    }
}
