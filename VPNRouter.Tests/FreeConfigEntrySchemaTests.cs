using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class FreeConfigEntrySchemaTests
{
    [Fact]
    public void LastVerifyFailedAt_Defaults_Null()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry();
        Assert.Null(entry.LastVerifyFailedAt);
    }

    [Fact]
    public void LastVerifyFailedAt_RoundTrips_Through_Json()
    {
        var original = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "abc",
            Host = "h.example.com",
            Port = 443,
            Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LastTestedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LatencyMs = 42,
            MeasuredBandwidthMbps = 25,
            LastVerifyFailedAt = new DateTime(2026, 5, 2, 8, 30, 0, DateTimeKind.Utc),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var revived = System.Text.Json.JsonSerializer
            .Deserialize<VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry>(json);

        Assert.NotNull(revived);
        Assert.Equal(original.LastVerifyFailedAt, revived!.LastVerifyFailedAt);
        Assert.Equal(42, revived.LatencyMs);
        Assert.Equal(25, revived.MeasuredBandwidthMbps);
    }

    [Fact]
    public void LastVerifyFailedAt_Indicates_FailedLastCheck_When_Greater_Than_LastTestedAt()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            LastTestedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LastVerifyFailedAt = new DateTime(2026, 5, 2, 8, 30, 0, DateTimeKind.Utc),
        };

        Assert.True(entry.LastVerifyFailedAt > entry.LastTestedAt);
    }
}
