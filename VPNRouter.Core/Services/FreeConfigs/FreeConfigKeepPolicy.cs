using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// v2.28.5: centralised "which Free Config entries are worth keeping
/// across sessions" policy.
///
/// <para>The full pool fetch produces ~25k <see cref="FreeConfigEntry"/>
/// objects (~12 MB managed heap on a typical run). After a default
/// search only ~10 reach <see cref="FreeConfigStatus.Verified"/>
/// (deep-verified with a real HTTP round-trip) and a few hundred
/// <see cref="FreeConfigStatus.Ok"/> (TCP+TLS-passed). The rest sit
/// in <see cref="FreeConfigStatus.Timeout"/> /
/// <see cref="FreeConfigStatus.Unreachable"/> /
/// <see cref="FreeConfigStatus.TlsFailed"/> /
/// <see cref="FreeConfigStatus.Implausible"/> /
/// <see cref="FreeConfigStatus.ParseError"/> — the displayed list
/// filters them out anyway, but they keep occupying memory until the
/// next search overwrites the in-memory list.</para>
///
/// <para>This policy is the single source of truth for "live cache
/// keep set" used by the Free Configs page VM after a search ends
/// (<c>TrimAndReclaim</c>). It deliberately excludes the
/// <c>EnsureCacheLoaded</c> at-launch policy, which is stricter
/// (Verified only) so a stale on-disk cache from a previous app
/// version doesn't surface non-deep-verified entries on first
/// launch — see <c>FreeConfigsPageViewModel.EnsureCacheLoaded</c>.</para>
/// </summary>
public static class FreeConfigKeepPolicy
{
    /// <summary>
    /// True if the entry is worth keeping in <c>_allConfigs</c> /
    /// the on-disk cache between searches.
    ///
    /// <para>v2.28.5-r2: tightened to Verified only (was Verified + Ok in
    /// r1). User feedback: "только полностью рабочие конфиги, ничего
    /// другого". Ok = TCP+TLS handshake passed but never went through a
    /// real HTTPS round-trip via sing-box; from the user's perspective
    /// these are not "working" yet. The new batched search loop already
    /// runs Deep Verify on every Ok candidate inline, so the surviving
    /// list naturally contains only Verified entries.</para>
    ///
    /// <para>Verified: gold — proved real HTTP traffic via deep verify.</para>
    /// </summary>
    public static bool ShouldKeepInLiveCache(FreeConfigEntry entry)
    {
        if (entry == null) return false;
        return entry.Status == FreeConfigStatus.Verified;
    }

    /// <summary>v2.28.6 Phase 1: how many days a Verified entry survives in
    /// the persistent saved list (the future Сохранённые tab) before
    /// EnsureCacheLoaded silently drops it on next launch. Beyond this the
    /// upstream pool is still re-discoverable on the next search.</summary>
    public const int SavedListRetentionDays = 30;

    /// <summary>
    /// v2.28.6 Phase 1: predicate for the persistent saved list at
    /// cache-load time. Stricter than <see cref="ShouldKeepInLiveCache"/>:
    /// in addition to the Verified-status requirement, applies a
    /// <see cref="SavedListRetentionDays"/>-day age cap on
    /// <see cref="FreeConfigEntry.LastTestedAt"/>.
    ///
    /// <para>Entries with a null <c>LastTestedAt</c> are kept (defensive:
    /// they may be brand-new entries whose timestamp wasn't set yet, or
    /// post-import entries from an older cache version where the field
    /// hadn't been recorded).</para>
    /// </summary>
    public static bool ShouldRetainInSavedList(FreeConfigEntry entry, DateTime nowUtc)
    {
        if (entry == null) return false;
        if (entry.Status != FreeConfigStatus.Verified) return false;
        if (!entry.LastTestedAt.HasValue) return true;
        return (nowUtc - entry.LastTestedAt.Value).TotalDays <= SavedListRetentionDays;
    }
}
