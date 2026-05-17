using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.28.6 Phase 1 — FreeConfigEntry schema additions
// ═══════════════════════════════════════════════════════════════════════════════
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
        // Last-good numbers must survive too — Phase 3 displays them on
        // entries that failed re-verify.
        Assert.Equal(42, revived.LatencyMs);
        Assert.Equal(25, revived.MeasuredBandwidthMbps);
    }

    [Fact]
    public void LastVerifyFailedAt_Indicates_FailedLastCheck_When_Greater_Than_LastTestedAt()
    {
        // Phase 3 display logic check: if LastVerifyFailedAt > LastTestedAt,
        // the row gets the "failed last check" badge while preserving the
        // last-good numbers. Phase 1 just pins the comparison semantics.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            LastTestedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            LastVerifyFailedAt = new DateTime(2026, 5, 2, 8, 30, 0, DateTimeKind.Utc),
        };

        Assert.True(entry.LastVerifyFailedAt > entry.LastTestedAt);
    }
}
