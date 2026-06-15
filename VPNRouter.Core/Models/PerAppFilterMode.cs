using System;

namespace VPNRouter.Core.Models;

/// <summary>
/// v3.0 v2.32.0 (2026-05-07) — pure-parse helpers for the Android per-app
/// filter mode tri-state ("off" / "include" / "exclude"). Lives in Core
/// (not VPNRouter.Android) so it compiles on the test host's net8.0 target
/// without an Android SDK dep — VPNRouter.Tests can pin the parsing rules
/// while the surrounding storage call (SharedPreferences via
/// <c>AndroidStorage</c>) stays Android-side.
///
/// <para>Storage values are persisted via SharedPreferences as-is. The
/// helper centralises three rules:</para>
/// <list type="bullet">
///   <item><see cref="Normalize"/>: any unrecognised string → "off". Used
///   on the storage-read path so a corrupted preference file never leaves
///   the routing layer in an undefined state.</item>
///   <item><see cref="ResolveLastMode"/>: read-time fallback for the
///   "remember last non-off mode" key. Defaults to "include" because that
///   matches first-time-user intent (most common per-app filter use case
///   is "only Netflix via VPN" rather than "everything except banking").</item>
///   <item><see cref="IsSplit"/>: true for include OR exclude (both
///   imply the form's split-tunnel radio is selected); false for off.</item>
/// </list>
/// </summary>
public static class PerAppFilterMode
{
    public const string Off = "off";
    public const string Include = "include";
    public const string Exclude = "exclude";

    /// <summary>
    /// Coerce any value (incl. null / whitespace / unknown) into one of the
    /// three canonical mode tokens. Comparison is case-insensitive so
    /// hand-edited config files don't trip on "Include" vs "include".
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Off;
        if (string.Equals(value, Include, StringComparison.OrdinalIgnoreCase)) return Include;
        if (string.Equals(value, Exclude, StringComparison.OrdinalIgnoreCase)) return Exclude;
        if (string.Equals(value, Off, StringComparison.OrdinalIgnoreCase)) return Off;
        return Off;
    }

    /// <summary>
    /// Resolve the persisted "last active non-off mode" preference. Used
    /// when the user toggles split-tunnel back on and we need to decide
    /// whether their last intent was include or exclude. "exclude" sticks;
    /// everything else (null, "off", "include", garbage) → "include".
    /// </summary>
    public static string ResolveLastMode(string? value)
    {
        if (string.Equals(value, Exclude, StringComparison.OrdinalIgnoreCase)) return Exclude;
        return Include;
    }

    /// <summary>
    /// True when the form's "Selected apps" radio should be selected for
    /// the given mode. Both include and exclude are split-tunnel variants;
    /// only off means full-tunnel.
    /// </summary>
    public static bool IsSplit(string? value)
    {
        var n = Normalize(value);
        return n == Include || n == Exclude;
    }

    // ── F6 (2026-06-15, plans/android-deep-qa-perf-2026-06-15.md) ──────────
    //
    // Single-source-of-truth projection between the desktop-style "split" /
    // "full" routing verb and the Android per-app filter tri-state.
    //
    // On Android the VpnService per-app filter (this tri-state) is the ONLY
    // thing that drives split-vs-full: the generated sing-box config is
    // always full-tunnel (AndroidConfigBuilder hard-sets RoutingMode="full"),
    // so the desktop "routing_mode" knob has no data-plane effect. Before
    // this fix Android stored routing_mode as a SECOND independent key, which
    // drifted out of sync with PerAppMode (Simple page read PerAppMode while
    // Advanced→Routing read routing_mode → contradictory radios, device-
    // confirmed on A101BM). These two helpers let AndroidStorage make
    // routing_mode a pure projection of PerAppMode, mirroring desktop where a
    // SINGLE IsSplitTunnel bool backs both the Simple and Settings radios so
    // they can never disagree.

    /// <summary>
    /// Project the per-app filter tri-state onto the desktop-style routing
    /// verb: "off" → "full" (whole-device tunnel), include/exclude → "split".
    /// Inverse of desktop's <c>IsSplitTunnel = !RoutingMode.Equals("full")</c>.
    /// </summary>
    public static string RoutingModeFor(string? perAppMode) =>
        IsSplit(perAppMode) ? "split" : "full";

    /// <summary>
    /// Resolve the per-app filter value a routing-mode change should write,
    /// or <c>null</c> when the change is a no-op (already in the requested
    /// split/full state) so the caller can skip the SharedPreferences write.
    /// <list type="bullet">
    ///   <item>"full" → "off" (collapse to whole-device tunnel), unless
    ///   already "off".</item>
    ///   <item>"split" → restore the last include/exclude intent
    ///   (<see cref="ResolveLastMode"/>) ONLY when currently "off"; an
    ///   already-split picker keeps its direction so split→split never
    ///   silently flips include↔exclude.</item>
    /// </list>
    /// Any non-"full" verb is treated as split (mirrors desktop's
    /// <c>!RoutingMode.Equals("full")</c> rule).
    /// </summary>
    public static string? PerAppModeForRoutingChange(
        string? routingMode, string? currentPerAppMode, string? lastMode)
    {
        var current = Normalize(currentPerAppMode);
        var wantFull = string.Equals(routingMode, "full", StringComparison.OrdinalIgnoreCase);
        if (wantFull)
            return current == Off ? null : Off;
        // want split:
        if (current != Off) return null;        // already split — keep include/exclude
        return ResolveLastMode(lastMode);        // restore last active direction
    }
}
