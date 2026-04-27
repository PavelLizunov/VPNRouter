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
    /// <para>Verified: gold — proved real HTTP traffic via deep verify.</para>
    /// <para>Ok: TCP + TLS handshake passed. Useful as a candidate seed
    /// for the next search's deep verify (priority 1 in the
    /// <c>DeepVerifyTopAsync</c> Priority function).</para>
    /// </summary>
    public static bool ShouldKeepInLiveCache(FreeConfigEntry entry)
    {
        if (entry == null) return false;
        return entry.Status == FreeConfigStatus.Verified
            || entry.Status == FreeConfigStatus.Ok;
    }
}
