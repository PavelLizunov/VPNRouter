namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// v2.28.6 Phase 5: pure functions classifying a saved <see cref="FreeConfigEntry"/>
/// by recency since its last successful verify, plus the failed-re-verify
/// state-merge helper used by the Recheck commands.
///
/// <para>The App layer's <c>FreeConfigItemViewModel</c> delegates its
/// <c>FreshnessLabel</c> / <c>OpacityValue</c> / <c>FreshnessSortKey</c> /
/// <c>HasFailedLastCheck</c> / <c>IsStale</c> getters to this class so all
/// the freshness math is testable from <c>VPNRouter.Tests</c> without an
/// Avalonia headless harness.</para>
/// </summary>
public static class FreeConfigFreshness
{
    /// <summary>Hours since last verify, beyond which the entry is "stale"
    /// for the bulk-Recheck button's stale-counter.</summary>
    public const int StaleAfterHours = 24;

    /// <summary>Days since last verify, beyond which the entry shifts to
    /// the dimmest visual tier (50% opacity).</summary>
    public const int VeryStaleAfterDays = 7;

    /// <summary>True when the most recent re-verify failed on a previously-
    /// Verified entry (so historical numbers are preserved with a failure
    /// timestamp on top). Drives the "failed last check" badge.</summary>
    public static bool HasFailedLastCheck(FreeConfigEntry entry)
    {
        if (entry == null) return false;
        if (!entry.LastVerifyFailedAt.HasValue) return false;
        // No prior successful verify timestamp → treat the failure as
        // current: HasFailedLastCheck.
        if (!entry.LastTestedAt.HasValue) return true;
        return entry.LastVerifyFailedAt.Value >= entry.LastTestedAt.Value;
    }

    /// <summary>True when the entry is older than <see cref="StaleAfterHours"/>
    /// since last verify, OR has a failed-last-check marker. Drives the
    /// "Recheck stale (N)" bulk button's filter.</summary>
    public static bool IsStale(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null) return false;
        if (HasFailedLastCheck(entry)) return true;
        if (!entry.LastTestedAt.HasValue) return true;
        return (nowUtc - entry.LastTestedAt.Value).TotalHours > StaleAfterHours;
    }

    /// <summary>Freshness tier for the saved-tab Status column.
    /// Maps to the localized label + opacity in the App layer.</summary>
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

    /// <summary>Whole-days age since last verify, for the "Nd ago" label
    /// in the Ageing tier. Returns 0 for null/future timestamps.</summary>
    public static int AgeDays(FreeConfigEntry entry, System.DateTime nowUtc)
    {
        if (entry == null || !entry.LastTestedAt.HasValue) return 0;
        var totalDays = (nowUtc - entry.LastTestedAt.Value).TotalDays;
        if (totalDays < 0) return 0;
        return (int)System.Math.Floor(totalDays);
    }

    /// <summary>Visual dim level for the saved-tab row Opacity binding:
    /// 1.0 fresh / 0.75 ageing / 0.5 stale or failed.</summary>
    public static double OpacityFor(FreeConfigFreshnessTier tier) => tier switch
    {
        FreeConfigFreshnessTier.Fresh => 1.0,
        FreeConfigFreshnessTier.Ageing => 0.75,
        _ => 0.5,
    };

    /// <summary>Sort key for the saved-tab list: tier-first (Fresh
    /// lowest → Failed highest), then by latency. Failed entries sink
    /// to the bottom regardless of age.</summary>
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

    /// <summary>v2.28.6 Phase 5: snapshot of last-good values that the
    /// Recheck commands capture before invoking
    /// <c>FreeConfigDeepVerifier.VerifyOneAsync</c>. On a failed re-verify,
    /// <see cref="MergeRecheckResult"/> restores these so the saved-list row
    /// keeps showing "this used to work at 15 ms / 50 Mbps" while the
    /// "failed" badge layers on top.</summary>
    public readonly record struct RecheckSnapshot(
        int LatencyMs,
        int? MeasuredBandwidthMbps,
        System.DateTime? LastTestedAt,
        // v2.39.0 (audit P0): capture the prior deep-verify success stamp so the
        // merge can tell a real fresh success from a residual Verified status.
        System.DateTime? LastDeepVerifyAt)
    {
        /// <summary>Capture a snapshot from the entry's current state.</summary>
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

    /// <summary>v2.28.6 Phase 5: apply the recheck-result merge policy.
    ///
    /// <para>Caller flow:</para>
    /// <list type="number">
    /// <item>Capture <c>prior = RecheckSnapshot.Capture(entry)</c> before
    ///   invoking the deep verifier.</item>
    /// <item>Run <c>_deepVerifier.VerifyOneAsync(entry)</c> — this mutates
    ///   <see cref="FreeConfigEntry.Status"/>,
    ///   <see cref="FreeConfigEntry.LatencyMs"/>,
    ///   <see cref="FreeConfigEntry.MeasuredBandwidthMbps"/>,
    ///   <see cref="FreeConfigEntry.LastTestedAt"/>.</item>
    /// <item>Call <c>MergeRecheckResult(entry, prior, DateTime.UtcNow)</c>
    ///   to apply the policy:
    ///   <list type="bullet">
    ///   <item>If <c>Status == Verified</c> after verify: success — clear
    ///     <see cref="FreeConfigEntry.LastVerifyFailedAt"/>; keep the fresh
    ///     numbers the verifier wrote.</item>
    ///   <item>Else: failed re-verify — restore <c>Status = Verified</c>
    ///     and the prior Latency/Bandwidth/LastTested so the saved-list
    ///     row keeps its historical numbers; set
    ///     <see cref="FreeConfigEntry.LastVerifyFailedAt"/> to <paramref name="nowUtc"/>.</item>
    ///   </list>
    /// </item>
    /// </list>
    ///
    /// <para>Pure function (modulo the <see cref="FreeConfigEntry"/>
    /// mutation). Easy to unit-test by constructing a synthetic entry,
    /// flipping its post-verify Status, and asserting the merged state.</para>
    /// </summary>
    public static void MergeRecheckResult(
        FreeConfigEntry entry,
        in RecheckSnapshot prior,
        System.DateTime nowUtc)
    {
        if (entry == null) return;

        // v2.39.0 (public-configs audit P0): a deep verify SUCCEEDED iff it
        // freshly stamped LastDeepVerifyAt — the verifier sets that field ONLY
        // on a passed HTTP-through-proxy probe. Keying success on
        // Status==Verified was wrong: the verifier does NOT downgrade a
        // previously-Verified Saved entry when its bind/HTTP/timeout fails, so a
        // dead config kept Status=Verified and was misread as a successful
        // recheck — the failure marker was even cleared. Use the stamp instead.
        bool verifySucceeded =
            entry.LastDeepVerifyAt.HasValue &&
            (!prior.LastDeepVerifyAt.HasValue ||
             entry.LastDeepVerifyAt.Value > prior.LastDeepVerifyAt.Value);

        if (verifySucceeded)
        {
            entry.Status = FreeConfigStatus.Verified;
            entry.LastVerifyFailedAt = null;
            // Verifier already updated LatencyMs/Bw/LastTestedAt to the
            // fresh successful values; leave them.
        }
        else
        {
            // Failed (or infrastructure-unavailable) re-verify: keep the row in
            // the Saved list (Status=Verified so the retention filter doesn't
            // drop it), restore the last-good numbers, and layer the
            // failed-last-check marker on top.
            entry.Status = FreeConfigStatus.Verified;
            entry.LatencyMs = prior.LatencyMs;
            entry.MeasuredBandwidthMbps = prior.MeasuredBandwidthMbps;
            entry.LastTestedAt = prior.LastTestedAt;
            entry.LastVerifyFailedAt = nowUtc;
        }
    }

    /// <summary>v2.28.6 Phase 5 cancel safety: restore the entry's prior
    /// state without marking it as a failure. Called from the Recheck
    /// commands' OperationCanceledException handlers — a cancel isn't a
    /// failure, and leaving the entry in the verifier's half-mutated state
    /// (e.g. Status = TlsFailed) would cause the retention filter to drop
    /// it on next cache load.</summary>
    public static void RestorePriorState(FreeConfigEntry entry, in RecheckSnapshot prior)
    {
        if (entry == null) return;
        entry.Status = FreeConfigStatus.Verified;
        entry.LatencyMs = prior.LatencyMs;
        entry.MeasuredBandwidthMbps = prior.MeasuredBandwidthMbps;
        entry.LastTestedAt = prior.LastTestedAt;
        // LastVerifyFailedAt intentionally unchanged — don't mark cancel
        // as a failure event.
    }
}

/// <summary>v2.28.6 Phase 5: enum used by the App layer to map a
/// classified saved entry to the right localized label + opacity. Living
/// in Core so test code can pin the classification rules.</summary>
public enum FreeConfigFreshnessTier
{
    /// <summary>Verified within the last 24 hours, or never tested.</summary>
    Fresh,

    /// <summary>Verified 1-7 days ago.</summary>
    Ageing,

    /// <summary>Verified more than 7 days ago.</summary>
    Stale,

    /// <summary>Last re-verify failed; last-good numbers preserved.</summary>
    Failed,
}
