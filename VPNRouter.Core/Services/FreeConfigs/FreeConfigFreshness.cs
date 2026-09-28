namespace VPNRouter.Core.Services.FreeConfigs;

public static class FreeConfigFreshness
{
    public const int StaleAfterHours = 24;

    public const int VeryStaleAfterDays = 7;

    public static bool HasFailedLastCheck(FreeConfigEntry entry)
    {
        if (entry == null) return false;
        if (!entry.LastVerifyFailedAt.HasValue) return false;
        if (!entry.LastTestedAt.HasValue) return true;
        return entry.LastVerifyFailedAt.Value >= entry.LastTestedAt.Value;
    }

    public static bool IsStale(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null) return false;
        if (HasFailedLastCheck(entry)) return true;
        if (!entry.LastTestedAt.HasValue) return true;
        return (nowUtc - entry.LastTestedAt.Value).TotalHours > StaleAfterHours;
    }

    public static FreeConfigFreshnessTier ClassifyTier(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null) return FreeConfigFreshnessTier.Fresh;
        if (HasFailedLastCheck(entry)) return FreeConfigFreshnessTier.Failed;
        if (!entry.LastTestedAt.HasValue) return FreeConfigFreshnessTier.Fresh;
        var ageDays = (nowUtc - entry.LastTestedAt.Value).TotalDays;
        if (ageDays < 1) return FreeConfigFreshnessTier.Fresh;
        if (ageDays > VeryStaleAfterDays) return FreeConfigFreshnessTier.Stale;
        return FreeConfigFreshnessTier.Ageing;
    }

    public static int AgeDays(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null || !entry.LastTestedAt.HasValue) return 0;
        var totalDays = (nowUtc - entry.LastTestedAt.Value).TotalDays;
        if (totalDays < 0) return 0;
        return (int)System.Math.Floor(totalDays);
    }

    public static double OpacityFor(FreeConfigFreshnessTier tier) => tier switch
    {
        FreeConfigFreshnessTier.Fresh => 1.0,
        FreeConfigFreshnessTier.Ageing => 0.75,
        _ => 0.5,
    };

    public static int SortKey(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null) return 0;
        var tier = ClassifyTier(entry, nowUtc);
        int tierBase = tier switch
        {
            FreeConfigFreshnessTier.Fresh => 0,
            FreeConfigFreshnessTier.Ageing => 100_000,
            FreeConfigFreshnessTier.Stale => 200_000,
            FreeConfigFreshnessTier.Failed => 1_000_000,
            _ => 0,
        };
        var latency = entry.LatencyMs > 0 ? entry.LatencyMs : 0;
        return tierBase + latency;
    }

    public readonly record struct RecheckSnapshot(
        int LatencyMs,
        int? MeasuredBandwidthMbps,
        System.DateTime? LastTestedAt,
        System.DateTime? LastDeepVerifyAt)
    {
        public static RecheckSnapshot Capture(FreeConfigEntry entry)
        {
            if (entry == null) return new RecheckSnapshot(0, null, null, null);
            return new RecheckSnapshot(
                entry.LatencyMs,
                entry.MeasuredBandwidthMbps,
                entry.LastTestedAt,
                entry.LastDeepVerifyAt);
        }
    }

    public static void MergeRecheckResult(
        FreeConfigEntry entry,
        in RecheckSnapshot prior,
        System.DateTime nowUtc)
    {
        if (entry == null) return;

        bool verifySucceeded =
            entry.LastDeepVerifyAt.HasValue &&
            (!prior.LastDeepVerifyAt.HasValue ||
             entry.LastDeepVerifyAt.Value > prior.LastDeepVerifyAt.Value);

        if (verifySucceeded)
        {
            entry.Status = FreeConfigStatus.Verified;
            entry.LastVerifyFailedAt = null;
        }
        else
        {
            entry.Status = FreeConfigStatus.Verified;
            entry.LatencyMs = prior.LatencyMs;
            entry.MeasuredBandwidthMbps = prior.MeasuredBandwidthMbps;
            entry.LastTestedAt = prior.LastTestedAt;
            entry.LastVerifyFailedAt = nowUtc;
        }
    }

    public static void RestorePriorState(FreeConfigEntry entry, in RecheckSnapshot prior)
    {
        if (entry == null) return;
        entry.Status = FreeConfigStatus.Verified;
        entry.LatencyMs = prior.LatencyMs;
        entry.MeasuredBandwidthMbps = prior.MeasuredBandwidthMbps;
        entry.LastTestedAt = prior.LastTestedAt;
    }
}

public enum FreeConfigFreshnessTier
{
    Fresh,

    Ageing,

    Stale,

    Failed,
}
