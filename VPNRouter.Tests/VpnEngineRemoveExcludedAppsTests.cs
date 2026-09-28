using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VpnEngineRemoveExcludedAppsTests
{
    [Fact]
    public void RemoveExcludedApps_DropsMatchingProcessByExactName()
    {
        var profile = MakeProfile("firefox.exe", "chrome.exe", "msedge.exe");

        VpnEngine.RemoveExcludedApps(profile, new[] { "firefox.exe" });

        Assert.Equal(2, profile.Processes.Count);
        Assert.DoesNotContain(profile.Processes,
            p => p.Name.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveExcludedApps_NormalisesExeSuffixVariance()
    {
        var profile = MakeProfile("firefox.exe");
        VpnEngine.RemoveExcludedApps(profile, new[] { "firefox" });
        Assert.Empty(profile.Processes);

        profile = MakeProfile("firefox");
        VpnEngine.RemoveExcludedApps(profile, new[] { "firefox.exe" });
        Assert.Empty(profile.Processes);
    }

    [Fact]
    public void RemoveExcludedApps_IsCaseInsensitive()
    {
        var profile = MakeProfile("Firefox.exe");
        VpnEngine.RemoveExcludedApps(profile, new[] { "FIREFOX" });
        Assert.Empty(profile.Processes);
    }

    [Fact]
    public void RemoveExcludedApps_NullExcludedList_IsNoOp()
    {
        var profile = MakeProfile("firefox.exe");
        VpnEngine.RemoveExcludedApps(profile, null);
        Assert.Single(profile.Processes);
    }

    [Fact]
    public void RemoveExcludedApps_EmptyExcludedList_IsNoOp()
    {
        var profile = MakeProfile("firefox.exe");
        VpnEngine.RemoveExcludedApps(profile, new List<string>());
        Assert.Single(profile.Processes);
    }

    [Fact]
    public void RemoveExcludedApps_NullProfile_IsNoOp()
    {
        VpnEngine.RemoveExcludedApps(null, new[] { "firefox" });
    }

    [Fact]
    public void RemoveExcludedApps_SkipsWhitespaceExcludeEntries()
    {
        var profile = MakeProfile("firefox.exe", "chrome.exe");
        VpnEngine.RemoveExcludedApps(profile,
            new[] { "  ", null!, string.Empty, "firefox" });
        Assert.Single(profile.Processes);
        Assert.Equal("chrome.exe", profile.Processes[0].Name);
    }

    [Fact]
    public void RemoveExcludedApps_DropsAllMatchesAcrossDuplicates()
    {
        var profile = new Profile
        {
            Name = "Browsers",
            Processes = new List<ProcessRule>
            {
                new() { Name = "firefox.exe" },
                new() { Name = "Firefox.exe" },
                new() { Name = "FIREFOX.EXE" },
                new() { Name = "chrome.exe" },
            }
        };
        VpnEngine.RemoveExcludedApps(profile, new[] { "firefox" });
        Assert.Single(profile.Processes);
        Assert.Equal("chrome.exe", profile.Processes[0].Name);
    }

    private static Profile MakeProfile(params string[] processNames)
    {
        return new Profile
        {
            Name = "TestProfile",
            Processes = processNames
                .Select(n => new ProcessRule { Name = n })
                .ToList()
        };
    }
}
