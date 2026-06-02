using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// Recheck merge: success path keeps fresh values, clears failure marker;
// failure path restores prior good values, sets failure marker.
public class FreeConfigRecheckMergeTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Success_ClearsFailureMarker_KeepsFreshValues()
    {
        // Setup: entry was failing (LastVerifyFailedAt set). Snapshot
        // captures prior values. Verifier reruns with success — Status =
        // Verified, fresh latency/bw/lastTested.
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

        // Simulate verifier mutation on success — it stamps a FRESH LastDeepVerifyAt.
        entry.LatencyMs = 30;
        entry.MeasuredBandwidthMbps = 60;
        entry.LastTestedAt = Now;
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified;
        entry.LastDeepVerifyAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Null(entry.LastVerifyFailedAt);
        Assert.Equal(30, entry.LatencyMs);          // fresh value kept
        Assert.Equal(60, entry.MeasuredBandwidthMbps); // fresh value kept
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

        // Simulate the REAL bug class: the verifier's bind/timeout/exception
        // paths do NOT downgrade a previously-Verified entry and do NOT stamp a
        // fresh LastDeepVerifyAt. Status stays Verified; only LastTestedAt
        // updates. Pre-fix this was misread as success (marker cleared); the
        // merge must now treat the absent fresh stamp as a failed recheck.
        entry.LastTestedAt = Now;        // verifier always updates this

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, prior, Now);

        // Status restored so retention filter doesn't drop it.
        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        // Last-good values restored.
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        // Failure marker set to recheck-time.
        Assert.Equal(Now, entry.LastVerifyFailedAt);
    }

    [Fact]
    public void Failure_Then_Success_ClearsMarker()
    {
        // Round trip: Verified → fail (marker set, last-good preserved) →
        // succeed (marker cleared, fresh values written).
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            MeasuredBandwidthMbps = 25,
            LastTestedAt = Now.AddDays(-1),
            LastDeepVerifyAt = Now.AddDays(-1),
        };

        // First recheck: fails (Status stays Verified, no fresh deep-verify stamp).
        var snap1 = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);
        entry.LastTestedAt = Now;
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(entry, snap1, Now);
        Assert.Equal(Now, entry.LastVerifyFailedAt);
        Assert.Equal(50, entry.LatencyMs);

        // Second recheck: succeeds — verifier stamps a fresh LastDeepVerifyAt.
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
    public void Null_Entry_NoOp()
    {
        var prior = new VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot();
        // Should not throw.
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.MergeRecheckResult(null!, prior, Now);
    }

    // ── v2.28.6-r2 cancel safety ──

    [Fact]
    public void RestorePriorState_RestoresVerifiedStatus()
    {
        // Cancel-mid-recheck scenario: verifier already mutated Status to
        // TlsFailed before the cancellation token tripped. Without
        // RestorePriorState the entry would be evicted by the retention
        // filter at next cache load (Status != Verified).
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

        // Simulate cancel after verifier got partway through and started
        // mutating fields.
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.TlsFailed;
        entry.LatencyMs = 9999;
        entry.LastTestedAt = Now;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(50, entry.LatencyMs);
        Assert.Equal(25, entry.MeasuredBandwidthMbps);
        Assert.Equal(Now.AddDays(-1), entry.LastTestedAt);
        // Cancel != failure — LastVerifyFailedAt stays null.
        Assert.Null(entry.LastVerifyFailedAt);
    }

    [Fact]
    public void RestorePriorState_DoesNot_Clobber_Existing_FailureMarker()
    {
        // If the entry was already in failed-last-check state (from a prior
        // failed recheck) and the user starts a new recheck which gets
        // cancelled, the prior failure marker should survive — we don't
        // know if this is now working again, so leave the existing marker.
        var entry = new VPNRouter.Core.Services.FreeConfigs.FreeConfigEntry
        {
            Id = "x", Host = "h", Port = 443, Uuid = "u",
            Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified,
            LatencyMs = 50,
            LastTestedAt = Now.AddDays(-2),
            LastVerifyFailedAt = Now.AddDays(-1),
        };
        var prior = VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot.Capture(entry);

        // Cancel mid-verify — verifier mutated some fields.
        entry.Status = VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Timeout;

        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(entry, prior);

        Assert.Equal(VPNRouter.Core.Services.FreeConfigs.FreeConfigStatus.Verified, entry.Status);
        Assert.Equal(Now.AddDays(-1), entry.LastVerifyFailedAt); // unchanged
    }

    [Fact]
    public void RestorePriorState_Null_Entry_NoOp()
    {
        var prior = new VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RecheckSnapshot();
        VPNRouter.Core.Services.FreeConfigs.FreeConfigFreshness.RestorePriorState(null!, prior);
    }
}
