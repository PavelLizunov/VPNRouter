using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Bug-r9-I regression pin (2026-05-11). Pre-r9-I a per-app checkbox in
/// the desktop Applications tab was a transient view state — toggling
/// Firefox off inside the Browsers group survived only until app
/// restart. Now persisted via <see cref="AppSettings.ExcludedApps"/>
/// and applied by <see cref="VpnEngine.RemoveExcludedApps"/> after all
/// process-merge steps complete in both StartAsync and ApplyAsync.
///
/// <para>User quote (verbatim): "я прост каждый раз когда захожу
/// отправляю фаерфокс в исключения потому что там ру сайты, а когда
/// перезапускаю винду галочка на нем опять стоит". Persistence test
/// lives in the App layer; this class pins the engine-layer filter
/// rules so a future refactor can't silently route an excluded app.</para>
/// </summary>
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
        // ExcludedApps from desktop is stored in AppItemViewModel.ProcessName
        // form: on Windows raw (with .exe), on macOS/Linux stripped. The
        // engine pipeline appends .exe in MergeUserCustomization so the
        // process list always has .exe. Filter must match across the
        // variance.
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
        // sing-box process_name match is case-sensitive at routing time
        // but our exclusion compare uses OrdinalIgnoreCase so a user
        // who hand-edited config.yaml with "FIREFOX" still gets the
        // expected behaviour.
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
        // No exception — the helper must tolerate the null profile case
        // because StartAsync/ApplyAsync only guard against null with a
        // local check before calling. Belt-and-braces.
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
        // MergeUserCustomization already dedupes by case-insensitive
        // name, but a hand-edited profile catalogue might still surface
        // duplicates. Filter removes every matching entry, not just the
        // first.
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
