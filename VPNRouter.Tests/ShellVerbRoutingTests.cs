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

    [Fact]
    public void OpenLogs_StartInfo_UsesCorrectOpenerForOS()
    {
        var logsDir = VPNRouter.Core.AppPaths.LogsDir;
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(logsDir);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(logsDir);
        }

        Assert.False(psi.UseShellExecute);
        Assert.Single(psi.ArgumentList);
        Assert.Equal(logsDir, psi.ArgumentList[0]);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("explorer.exe", psi.FileName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("/usr/bin/open", psi.FileName);
        }
        else
        {
            Assert.Equal("xdg-open", psi.FileName);
        }
    }
}

#if PLATFORM_WINDOWS

public class ShellVerbRoutingTests
{
    private static MainWindowViewModel MakeVm() => new(new InMemorySettingsStore());

    private static AppSettings Settings(MainWindowViewModel vm) =>
        (AppSettings)typeof(MainWindowViewModel)
            .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private static AppItemViewModel Bridged(MainWindowViewModel vm, string proc)
    {
        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateBridgedAppItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (AppItemViewModel)factory.Invoke(vm, new object[] { proc, false, true })!;
    }

    private static void IncludeMode(MainWindowViewModel vm, AppSettings s)
    {
        s.App.RoutingAppsInclude = new List<string>();
        s.App.RoutingAppsExclude = new List<string>();
        s.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";
    }

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
        var item = Bridged(vm, "Game.exe");
        group.Apps.Add(item);
        Assert.False(item.IsChecked);
        Assert.DoesNotContain("Game.exe", s.App.RoutingAppsInclude);

        vm.RouteAppFromShell("Game.exe");

        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void IncludeMode_Unroute_RemovesFromAllGroupsAndList()
    {
        var vm = MakeVm();
        var s = Settings(vm);
        IncludeMode(vm, s);

        var g1 = new AppGroupViewModel("Custom Apps", "", isChecked: true);
        var g2 = new AppGroupViewModel("Games", "", isChecked: true) { IsCustomCategory = true };
        vm.AppGroups.Add(g1);
        vm.AppGroups.Add(g2);
        var i1 = Bridged(vm, "Game.exe");
        var i2 = Bridged(vm, "Game.exe");
        g1.Apps.Add(i1);
        g2.Apps.Add(i2);
        i1.IsChecked = true;
        Assert.Contains("Game.exe", s.App.RoutingAppsInclude);

        vm.UnrouteAppFromShell("Game.exe");

        Assert.DoesNotContain("Game.exe", s.App.RoutingAppsInclude);
        Assert.DoesNotContain(g1.Apps, a => a.ProcessName == "Game.exe");
        Assert.DoesNotContain(g2.Apps, a => a.ProcessName == "Game.exe");
    }
}
#endif
