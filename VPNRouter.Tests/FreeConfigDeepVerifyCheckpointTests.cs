using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class FreeConfigDeepVerifyCheckpointTests
{
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

    private static bool ShouldSkipDeepVerify(VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry cfg)
    {
        return cfg.Status == VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified
            && cfg.LastDeepVerifyAt.HasValue
            && (DateTime.UtcNow - cfg.LastDeepVerifyAt.Value) < TimeSpan.FromHours(6)
            && cfg.LatencyMs > 0;
    }
}
