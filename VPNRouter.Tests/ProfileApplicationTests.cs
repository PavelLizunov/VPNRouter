using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 — pin the rules around the Android Profiles overlay (AND-PROFILES)
/// apply path. The overlay-side wiring lives in
/// <c>VPNRouter.Android.AndroidApp</c> (SharedPreferences-backed, requires
/// the Android runtime), but the rule that translates a chosen profile into
/// concrete storage writes is a pure function in
/// <see cref="ProfileApplication.Plan"/> and is worth pinning so a future
/// refactor can't silently drop split-tunnel switching, swallow the leak-
/// protection toggle, or have "No profile" clobber a user's manual app
/// picks.
///
/// <para>The bug class fenced against: a regression here would mean tapping
/// a profile silently leaves the user in full-tunnel mode even though their
/// app list got rewritten — they'd see "8 apps selected" but every site
/// would still go through VPN. Or, picking "No profile" would erase their
/// manually-curated banking-app exclusions. Both are quiet UX failures
/// that wouldn't surface in smoke testing.</para>
/// </summary>
public class ProfileApplicationTests
{
    [Fact]
    public void Plan_NullProfile_ClearsActiveAndSwitchesToFull()
    {
        var plan = ProfileApplication.Plan(null);

        Assert.Null(plan.ActiveProfileName);
        Assert.Equal("full", plan.RoutingMode);
    }

    [Fact]
    public void Plan_NullProfile_LeavesPerAppFieldsUntouched()
    {
        // "No profile" is NOT a destructive action — the user may have
        // manually curated an app list outside the profile system that
        // we shouldn't drop on profile clear.
        var plan = ProfileApplication.Plan(null);

        Assert.Null(plan.AndroidPackages);
        Assert.Null(plan.PerAppMode);
        Assert.Null(plan.PerAppLastMode);
        Assert.Null(plan.BlockOnVpnFail);
    }

    [Fact]
    public void Plan_AppliesProfile_WritesAllRoutingFields()
    {
        var profile = new Profile
        {
            Name = "Discord_Privacy",
            AndroidPackages = new List<string> { "com.discord" },
            BlockOnVpnFail = true,
        };

        var plan = ProfileApplication.Plan(profile);

        Assert.Equal("Discord_Privacy", plan.ActiveProfileName);
        Assert.Equal("split", plan.RoutingMode);
        Assert.Equal("include", plan.PerAppMode);
        Assert.Equal("include", plan.PerAppLastMode);
        Assert.NotNull(plan.AndroidPackages);
        Assert.Equal(new[] { "com.discord" }, plan.AndroidPackages);
        Assert.Equal(true, plan.BlockOnVpnFail);
    }

    [Fact]
    public void Plan_AppliesProfile_PropagatesBlockOnVpnFailFalse()
    {
        // BlockOnVpnFail=false must propagate explicitly, not silently
        // collapse to null — otherwise a user switching from "Discord
        // Privacy" (block=true) to "Browsers" (block=false) would keep
        // the previous block-on-fail flag on.
        var profile = new Profile
        {
            Name = "Browsers",
            AndroidPackages = new List<string> { "com.android.chrome" },
            BlockOnVpnFail = false,
        };

        var plan = ProfileApplication.Plan(profile);

        Assert.Equal(false, plan.BlockOnVpnFail);
    }

    [Fact]
    public void Plan_AppliesProfile_CopiesPackagesDefensively()
    {
        // Returned list must be independent of the catalog instance so
        // a downstream mutator (e.g. the picker overlay reusing the
        // list) can't pollute the BuiltInAndroidProfiles singleton.
        var profile = new Profile
        {
            Name = "Messengers",
            AndroidPackages = new List<string> { "org.telegram.messenger" },
        };

        var plan = ProfileApplication.Plan(profile);

        Assert.NotSame(profile.AndroidPackages, plan.AndroidPackages);
        plan.AndroidPackages!.Add("garbage.example");
        Assert.Single(profile.AndroidPackages);
    }

    [Fact]
    public void Plan_AppliesProfile_HandlesNullPackagesGracefully()
    {
        // A profile authored without android_packages (e.g. desktop-only
        // catalog entry that somehow leaked into the picker) should
        // produce a usable empty plan rather than throw.
        var profile = new Profile
        {
            Name = "DesktopOnly",
            AndroidPackages = null!,
        };

        var plan = ProfileApplication.Plan(profile);

        Assert.NotNull(plan.AndroidPackages);
        Assert.Empty(plan.AndroidPackages);
    }

    [Fact]
    public void BuiltInAndroidProfiles_HasEightCategories()
    {
        // Pin the catalog cardinality so a careless edit can't silently
        // drop a category. The set mirrors profiles/default-android.json
        // verbatim — both must move together.
        var catalog = BuiltInAndroidProfiles.Get();

        Assert.Equal(8, catalog.Profiles.Count);
        Assert.Contains(catalog.Profiles, p => p.Name == "Discord_Privacy");
        Assert.Contains(catalog.Profiles, p => p.Name == "Messengers");
        Assert.Contains(catalog.Profiles, p => p.Name == "Browsers");
        Assert.Contains(catalog.Profiles, p => p.Name == "AI_Tools");
        Assert.Contains(catalog.Profiles, p => p.Name == "Work_Suite");
        Assert.Contains(catalog.Profiles, p => p.Name == "Streaming");
        Assert.Contains(catalog.Profiles, p => p.Name == "Gaming");
        Assert.Contains(catalog.Profiles, p => p.Name == "Privacy_Shell");
    }

    [Fact]
    public void BuiltInAndroidProfiles_AllEntriesHavePackageIds()
    {
        // Every catalog entry must have at least one package — an empty
        // android_packages list would let the user "apply" a profile that
        // routes nothing, silently disabling split-tunnel without UI feedback.
        var catalog = BuiltInAndroidProfiles.Get();

        foreach (var profile in catalog.Profiles)
        {
            Assert.NotNull(profile.AndroidPackages);
            Assert.NotEmpty(profile.AndroidPackages);
        }
    }
}
