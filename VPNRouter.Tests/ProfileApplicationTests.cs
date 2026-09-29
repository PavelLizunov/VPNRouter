using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
        var catalog = BuiltInAndroidProfiles.Get();

        foreach (var profile in catalog.Profiles)
        {
            Assert.NotNull(profile.AndroidPackages);
            Assert.NotEmpty(profile.AndroidPackages);
        }
    }
}
