using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

/// <summary>
/// v3.0 v2.32.0 (2026-05-07) — pin the rules around the Android per-app
/// filter mode tri-state. The actual storage round-trip lives in
/// <c>VPNRouter.Android.AndroidStorage</c> (SharedPreferences-backed,
/// requires the Android runtime), but the parsing rules — what counts
/// as "include" vs "exclude" vs "off" + last-mode default + IsSplit
/// computed — are pure functions in
/// <see cref="PerAppFilterMode"/> and worth pinning so a future refactor
/// can't silently flip the default from "include" → "off" or treat
/// "Exclude" as "off" via a missing case-insensitive compare.
///
/// <para>The bug class fenced against: per-app filter is the difference
/// between "user's banking app stays on home network" (exclude) and
/// "user's banking app routes through VPN" (include). A silent default
/// flip would route real banking traffic via VPN unintentionally — the
/// kind of regression no smoke-test catches.</para>
/// </summary>
public class PerAppFilterModeTests
{
    [Theory]
    [InlineData(null, "off")]
    [InlineData("", "off")]
    [InlineData("   ", "off")]
    [InlineData("off", "off")]
    [InlineData("OFF", "off")]
    [InlineData("include", "include")]
    [InlineData("Include", "include")]
    [InlineData("INCLUDE", "include")]
    [InlineData("exclude", "exclude")]
    [InlineData("Exclude", "exclude")]
    [InlineData("EXCLUDE", "exclude")]
    // Unknown / corrupted values fall through to "off" — safer than
    // surfacing an undefined mode to the routing layer.
    [InlineData("garbage", "off")]
    [InlineData("split", "off")]
    [InlineData("on", "off")]
    public void Normalize_CanonicalisesAllInputs(string? input, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.Normalize(input));
    }

    [Theory]
    // First-time / never-set → include is the friendly default
    // (matches first-time-user intent for per-app filters).
    [InlineData(null, "include")]
    [InlineData("", "include")]
    [InlineData("   ", "include")]
    // "off" is not a valid LAST mode (it's the toggle-off state, not
    // an active mode the user picked) — falls back to include.
    [InlineData("off", "include")]
    [InlineData("include", "include")]
    [InlineData("INCLUDE", "include")]
    // Only "exclude" sticks.
    [InlineData("exclude", "exclude")]
    [InlineData("Exclude", "exclude")]
    [InlineData("EXCLUDE", "exclude")]
    // Garbage → include (safer default; user can always re-toggle).
    [InlineData("garbage", "include")]
    public void ResolveLastMode_OnlyExcludeSticks(string? input, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.ResolveLastMode(input));
    }

    [Theory]
    // include OR exclude → split tunnel. off / null / unknown → not split.
    // Pinning this rule keeps the form's split radio in sync with the
    // mode tri-state without a "magic" comparison drifting out of place.
    [InlineData("include", true)]
    [InlineData("exclude", true)]
    [InlineData("INCLUDE", true)]
    [InlineData("Exclude", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void IsSplit_CoversIncludeAndExcludeOnly(string? input, bool expected)
    {
        Assert.Equal(expected, PerAppFilterMode.IsSplit(input));
    }

    [Fact]
    public void Constants_MatchPersistedTokens()
    {
        // Pin the literal storage tokens. SharedPreferences keys persist
        // across upgrades; if these tokens drift, every existing user's
        // saved mode reads as "off" (corrupted-fallback behaviour) and
        // their selection silently disables. Catching this at test time
        // beats catching it at first-launch-after-update.
        Assert.Equal("off", PerAppFilterMode.Off);
        Assert.Equal("include", PerAppFilterMode.Include);
        Assert.Equal("exclude", PerAppFilterMode.Exclude);
    }

    // ── F6 (2026-06-15, plans/android-deep-qa-perf-2026-06-15.md) ──────────
    //
    // RoutingMode is a pure projection of PerAppMode on Android: the two
    // SharedPreferences keys used to drift (Simple page seeded its radio from
    // PerAppMode, Advanced→Routing from a separate routing_mode key →
    // contradictory radios, device-confirmed on A101BM). AndroidStorage's
    // GetRoutingMode / SetRoutingMode now delegate to these helpers so the
    // two surfaces can never disagree, mirroring desktop's single
    // IsSplitTunnel bool. The storage round-trip itself lives in
    // VPNRouter.Android.AndroidStorage (Android-runtime-only); the projection
    // rules below are the testable seam.

    [Theory]
    // off → full tunnel; include/exclude → split. Inverse of desktop's
    // IsSplitTunnel = !RoutingMode.Equals("full").
    [InlineData("off", "full")]
    [InlineData("OFF", "full")]
    [InlineData(null, "full")]
    [InlineData("", "full")]
    [InlineData("garbage", "full")]   // normalises to off → full
    [InlineData("include", "split")]
    [InlineData("Include", "split")]
    [InlineData("exclude", "split")]
    [InlineData("EXCLUDE", "split")]
    public void RoutingModeFor_ProjectsPerAppModeToSplitFull(string? perAppMode, string expected)
    {
        Assert.Equal(expected, PerAppFilterMode.RoutingModeFor(perAppMode));
    }

    [Theory]
    // "full" collapses to "off" unless already off (then no-op = null).
    [InlineData("full", "include", "include", "off")]
    [InlineData("full", "exclude", "exclude", "off")]
    [InlineData("full", "off", "include", null)]      // already full → no write
    // "split" from off restores the last active include/exclude intent.
    [InlineData("split", "off", "include", "include")]
    [InlineData("split", "off", "exclude", "exclude")]
    [InlineData("split", "off", null, "include")]     // no last mode → include default
    [InlineData("split", "off", "garbage", "include")]
    // "split" while already split is a no-op (keeps the current direction —
    // must NOT silently flip include↔exclude).
    [InlineData("split", "include", "exclude", null)]
    [InlineData("split", "exclude", "include", null)]
    // Any non-"full" verb is treated as split (mirrors desktop's
    // !RoutingMode.Equals("full")).
    [InlineData("SPLIT", "off", "include", "include")]
    public void PerAppModeForRoutingChange_TranslatesVerbToPerAppMode(
        string routingMode, string currentPerAppMode, string? lastMode, string? expected)
    {
        Assert.Equal(expected,
            PerAppFilterMode.PerAppModeForRoutingChange(routingMode, currentPerAppMode, lastMode));
    }

    [Theory]
    // Drift invariant: applying a routing verb then projecting back yields
    // the verb that was applied — the round-trip the two AndroidStorage keys
    // failed before F6. (Simulates SetRoutingMode → GetRoutingMode.)
    [InlineData("off", "full", "full")]
    [InlineData("include", "full", "full")]
    [InlineData("exclude", "full", "full")]
    [InlineData("off", "split", "split")]
    [InlineData("include", "split", "split")]
    [InlineData("exclude", "split", "split")]
    public void RoutingChange_ThenProject_RoundTrips(
        string startPerAppMode, string applyRoutingMode, string expectedRoutingMode)
    {
        // SetRoutingMode semantics: null means "no change", so the effective
        // PerAppMode is either the helper's result or the unchanged start.
        var lastMode = PerAppFilterMode.ResolveLastMode(startPerAppMode);
        var next = PerAppFilterMode.PerAppModeForRoutingChange(
            applyRoutingMode, startPerAppMode, lastMode);
        var effectivePerAppMode = next ?? startPerAppMode;

        Assert.Equal(expectedRoutingMode, PerAppFilterMode.RoutingModeFor(effectivePerAppMode));
    }
}
