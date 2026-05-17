using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>
/// v2.31.6-r10 (Phase F): tests pinning the extracted
/// <c>MergeUserCustomization</c> helper. Pre-r10 this logic was
/// duplicated ~50 LOC verbatim between VpnEngine.StartAsync and
/// VpnEngine.ApplyAsync, with silent-leak risk if the two drifted.
/// These tests exercise every branch of the consolidated helper:
/// CustomGroupApps merge into existing profiles, dupe skip,
/// CustomCategories injection, name-collision skip, .exe extension
/// normalisation, empty/null inputs, whitespace-only inputs.
/// </summary>
public class MergeUserCustomizationTests
{
    private static ProfileCollection BuildCollectionWith(params string[] profileNames)
    {
        var pc = new ProfileCollection();
        foreach (var n in profileNames)
        {
            pc.Profiles.Add(new Profile { Name = n, Processes = new List<ProcessRule>() });
        }
        return pc;
    }

    private static AppSettings BuildSettingsWithGroup(string groupName, params string[] apps)
    {
        var s = new AppSettings();
        s.CustomGroupApps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [groupName] = new List<string>(apps),
        };
        return s;
    }

    private static AppSettings BuildSettingsWithCategory(string catName, params string[] apps)
    {
        var s = new AppSettings();
        s.CustomCategories = new List<CustomCategory>
        {
            new CustomCategory { Name = catName, Apps = new List<string>(apps) },
        };
        return s;
    }

    [Fact]
    public void Merge_AddsAppsToMatchingProfile_AppendingExeExtension()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = BuildSettingsWithGroup("Discord_Privacy", "MyExtraApp", "AlreadyHas.exe");

        VpnEngine.MergeUserCustomization(pc, s);

        var p = pc.Profiles.Single();
        Assert.Equal(2, p.Processes.Count);
        // .exe appended to bare name; pre-existing .exe stays unchanged.
        Assert.Contains(p.Processes, x => x.Name == "MyExtraApp.exe");
        Assert.Contains(p.Processes, x => x.Name == "AlreadyHas.exe");
    }

    [Fact]
    public void Merge_SkipsDuplicateProcessNamesCaseInsensitively()
    {
        var pc = new ProfileCollection();
        pc.Profiles.Add(new Profile
        {
            Name = "Browsers",
            Processes = new List<ProcessRule> { new() { Name = "Chrome.exe" } }
        });
        var s = BuildSettingsWithGroup("Browsers", "chrome.exe", "CHROME.exe", "firefox");

        VpnEngine.MergeUserCustomization(pc, s);

        var p = pc.Profiles.Single();
        // Pre-existing Chrome.exe stays; case-insensitive duplicates
        // skipped; firefox.exe added.
        Assert.Equal(2, p.Processes.Count);
        Assert.Contains(p.Processes, x => x.Name == "Chrome.exe");
        Assert.Contains(p.Processes, x => x.Name == "firefox.exe");
    }

    [Fact]
    public void Merge_SkipsUnknownGroup()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = BuildSettingsWithGroup("DoesNotExist", "App1");

        VpnEngine.MergeUserCustomization(pc, s);

        // No profile matches the group name → no mutation.
        Assert.Empty(pc.Profiles.Single().Processes);
    }

    [Fact]
    public void Merge_SkipsWhitespaceOnlyAppNames()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = BuildSettingsWithGroup("Discord_Privacy", " ", "", "\t\n", "RealApp");

        VpnEngine.MergeUserCustomization(pc, s);

        Assert.Single(pc.Profiles.Single().Processes);
        Assert.Equal("RealApp.exe", pc.Profiles.Single().Processes.Single().Name);
    }

    [Fact]
    public void Merge_InjectsNewCategoryAsProfile()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = BuildSettingsWithCategory("MyCustomCategory", "App1", "App2.exe");

        VpnEngine.MergeUserCustomization(pc, s);

        Assert.Equal(2, pc.Profiles.Count);
        var newProfile = pc.Profiles.Single(p => p.Name == "MyCustomCategory");
        Assert.Equal("vpn_only", newProfile.DnsMode);
        Assert.False(newProfile.BlockOnVpnFail);
        Assert.Equal(2, newProfile.Processes.Count);
    }

    [Fact]
    public void Merge_SkipsCategoryWhoseNameCollidesWithExistingProfile()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = BuildSettingsWithCategory("discord_privacy", "App1");

        VpnEngine.MergeUserCustomization(pc, s);

        // No new profile injected; existing Discord_Privacy untouched.
        Assert.Single(pc.Profiles);
        Assert.Empty(pc.Profiles.Single().Processes);
    }

    [Fact]
    public void Merge_NullCollections_NoOp()
    {
        var pc = BuildCollectionWith("Discord_Privacy");
        var s = new AppSettings(); // no CustomGroupApps, no CustomCategories

        VpnEngine.MergeUserCustomization(pc, s);

        Assert.Single(pc.Profiles);
        Assert.Empty(pc.Profiles.Single().Processes);
    }
}
