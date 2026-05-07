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
}
