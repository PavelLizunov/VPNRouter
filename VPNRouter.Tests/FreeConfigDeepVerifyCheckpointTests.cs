using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
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
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-2),
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
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-7),
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
            LatencyMs = 0,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    [Fact]
    public void SkipDeepVerify_NonVerifiedStatus_False()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed,
            LastDeepVerifyAt = DateTime.UtcNow.AddHours(-1),
            LatencyMs = 50,
        };
        Assert.False(ShouldSkipDeepVerify(entry));
    }

    private static bool ShouldSkipDeepVerify(VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry cfg)
    {
        return cfg.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
            && cfg.LastDeepVerifyAt.HasValue
            && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
            && cfg.LatencyMs > 0;
    }
}
