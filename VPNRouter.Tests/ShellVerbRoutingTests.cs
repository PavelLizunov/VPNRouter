using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Services;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class RevealInFileManagerTests
{
    [Fact]
    public void BuildRevealStartInfo_SafeArgumentList_DoesNotUseUnescapedStringInArguments()
    {
        var malPath = Path.Combine(Path.GetTempPath(), "test_path_with spaces_\" & calc.exe & \"file.txt");
        var psi = FileManagerHelper.BuildRevealStartInfo(malPath);

        Assert.Empty(psi.Arguments);
        Assert.False(psi.UseShellExecute);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("explorer.exe", psi.FileName);
            Assert.Single(psi.ArgumentList);
            Assert.Equal($"/select,{malPath}", psi.ArgumentList[0]);
        }
        else
        {
            Assert.Equal(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open", psi.FileName);
            Assert.False(psi.UseShellExecute);
            Assert.Single(psi.ArgumentList);
            Assert.Equal(Path.GetDirectoryName(malPath), psi.ArgumentList[0]);
        }
    }
}

#if PLATFORM_WINDOWS

/// <summary>
/// v2.38.0-r6 — regression pins for the Explorer "route / unroute through VPN"
/// shell verbs (<c>RouteAppFromShell</c> / <c>UnrouteAppFromShell</c>). The
/// Heavy adversarial audit of r1→r5 found these defects in the UNtested
/// ViewModel bridge (RoutingAppListEditorTests only covered the pure Core
/// helper — every confirmed bug lived here):
/// <list type="number">
///   <item><b>Exclude-mode inversion</b> — the verbs must use include-list
///   semantics regardless of the current in-app routing mode.</item>
///   <item><b>False-success toast</b> — AddCustomApp early-returns on an
///   existing-but-unchecked item, so nothing got routed while the toast claimed
///   success. r6 fix: force the landed item checked + base the toast on the
///   actual routed state.</item>
///   <item><b>Unroute break</b> — removed only the FIRST group instance; a
///   leftover in another group re-persisted and could re-route. r6 fix: remove
///   EVERY instance.</item>
/// </list>
/// Windows-only — the verbs are <c>#if PLATFORM_WINDOWS</c> in the App, so this
/// whole file is gated the same way (excluded from the Linux/Mac build).
/// </summary>
public class ShellVerbRoutingTests
{
    private static MainWindowViewModel MakeVm() => new(new InMemorySettingsStore());

    private static AppSettings Settings(MainWindowViewModel vm) =>
        (AppSettings)typeof(MainWindowViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    // Mirror AppsModeTests: build a properly bridged AppItem (ReadMode/WriteMode
    // wired to the VM's mode-aware helpers) via the private factory.
    private static AppItemViewModel Bridged(MainWindowViewModel vm, string proc)
    {
        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateBridgedAppItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // signature: (string processName, bool legacyChecked, bool isCustom = false)
        return (AppItemViewModel)factory.Invoke(vm, new object[] { proc, false, true })!;
    }

    private static void IncludeMode(MainWindowViewModel vm, AppSettings s)
    {
        s.App.RoutingAppsInclude = new List<string>();
        s.App.RoutingAppsExclude = new List<string>();
        s.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";
    }

    // ── r7: shell verbs are include-only regardless of routing mode ──

    [AvaloniaFact]
    public void ExcludeMode_Route_AddsToIncludeOnly()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        s.App.RoutingAppsInclude = new List<string>();
        s.App.RoutingAppsExclude = new List<string>();
        s.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        vm.RouteAppFromShell("Game.exe");

        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);
        Assert.Empty(s.App.RoutingAppsExclude);
    }

    [AvaloniaFact]
    public void ExcludeMode_Unroute_RemovesFromIncludeOnly()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        s.App.RoutingAppsInclude = new List<string> { "Steam.exe" };
        s.App.RoutingAppsExclude = new List<string> { "Steam.exe" };
        s.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        vm.UnrouteAppFromShell("Steam.exe");

        Assert.DoesNotContain("Steam.exe", s.App.RoutingAppsInclude);
        Assert.Contains("Steam.exe", s.App.RoutingAppsExclude);
    }

    // ── Include-mode happy paths ──

    [AvaloniaFact]
    public void IncludeMode_Route_AddsToInclude()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        IncludeMode(vm, s);

        vm.RouteAppFromShell("Game.exe");

        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);
        Assert.Empty(s.App.RoutingAppsExclude);
    }

    // ── Finding #2: existing-but-unchecked item → false success ──

    [AvaloniaFact]
    public void IncludeMode_Route_ExistingUncheckedItem_ActuallyRoutes()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        IncludeMode(vm, s);

        var group = vm.AppGroups.FirstOrDefault(g => g.Name == "Custom Apps");
        if (group == null)
        {
            group = new AppGroupViewModel("Custom Apps", "", isChecked: true);
            vm.AppGroups.Add(group);
        }
        // Present in the group but UNCHECKED (not in RoutingAppsInclude).
        var item = Bridged(vm, "Game.exe");
        group.Apps.Add(item);
        Assert.False(item.IsChecked);
        Assert.DoesNotContain("Game.exe", s.App.RoutingAppsInclude);

        vm.RouteAppFromShell("Game.exe");

        // Pre-r6: AddCustomApp early-returned → nothing routed, toast lied.
        // r6: the landed item is forced checked → actually routed.
        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);
    }

    // ── Finding #3: unroute must clear EVERY group instance ──

    [AvaloniaFact]
    public void IncludeMode_Unroute_RemovesFromAllGroupsAndList()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        IncludeMode(vm, s);

        // Same process name in TWO groups (Custom Apps + a named category).
        var g1 = new AppGroupViewModel("Custom Apps", "", isChecked: true);
        var g2 = new AppGroupViewModel("Games", "", isChecked: true) { IsCustomCategory = true };
        vm.AppGroups.Add(g1);
        vm.AppGroups.Add(g2);
        var i1 = Bridged(vm, "Game.exe");
        var i2 = Bridged(vm, "Game.exe");
        g1.Apps.Add(i1);
        g2.Apps.Add(i2);
        i1.IsChecked = true; // → RoutingAppsInclude now has Game.exe
        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);

        vm.UnrouteAppFromShell("Game.exe");

        // Pre-r6: break removed only g1's instance; g2's leftover re-persisted
        // and could re-route. r6: every instance gone + list scrubbed.
        Assert.DoesNotContain("Game.exe", s.App.RoutingAppsInclude);
        Assert.DoesNotContain(g1.Apps, a => a.ProcessName == "Game.exe");
        Assert.DoesNotContain(g2.Apps, a => a.ProcessName == "Game.exe");
    }
}
#endif
