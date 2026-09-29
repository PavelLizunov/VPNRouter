using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;
public class FreeConfigRecheckMergeTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Success_ClearsFailureMarker_KeepsFreshValues()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-2),
            LastDeepVerifyAt = Now.AddDays(-2),
            LastVerifyFailedAt = Now.AddDays(-1),
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        entry.LatencyMs = 30;
        entry.MeasuredBandwidthMbps = 60;
        entry.LastTestedAt = Now;
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified;
        entry.LastDeepVerifyAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Null(entry.LastVerifyFailedAt);
        Assert.Equal(30, entry.LatencyMs);
        Assert.Equal(60, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now, entry.LastTestedAt);
    }

    [Fact]
    public void Failure_RestoresPriorValues_SetsFailureMarker_KeepsVerifiedStatus()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastDeepVerifyAt = Now.AddDays(-1),
            LastVerifyFailedAt = null,
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        entry.LastTestedAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        Assert.Equal(Now, entry.LastVerifyFailedAt);
    }

    [Fact]
    public void Failure_Then_Success_ClearsMarker()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastDeepVerifyAt = Now.AddDays(-1),
        };

        var snap1 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        entry.LastTestedAt = Now;
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, snap1, Now);
        Assert.Equal(Now, entry.LastVerifyFailedAt);
        Assert.Equal(50, entry.LatencyMs);

        var later = Now.AddMinutes(10);
        var snap2 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified;
        entry.LatencyMs = 35;
        entry.MeasuredBandwidthMbps = 80;
        entry.LastTestedAt = later;
        entry.LastDeepVerifyAt = later;
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, snap2, later);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Null(entry.LastVerifyFailedAt);
        Assert.Equal(35, entry.LatencyMs);
        Assert.Equal(80, entry.MeasuredBandwidthMbps);
    }

    [Fact]
    public void RestorePriorState_RestoresVerifiedStatus()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastVerifyFailedAt = null,
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed;
        entry.LatencyMs = 9999;
        entry.LastTestedAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        Assert.Null(entry.LastVerifyFailedAt);
    }

    [Fact]
    public void RestorePriorState_DoesNot_Clobber_Existing_FailureMarker()
    {
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            LastTestedAt = Now.AddDays(-2),
            LastVerifyFailedAt = Now.AddDays(-1),
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(Now.AddDays(-1), entry.LastVerifyFailedAt);
    }
}
