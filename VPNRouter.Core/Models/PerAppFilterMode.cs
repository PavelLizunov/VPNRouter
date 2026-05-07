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
}
