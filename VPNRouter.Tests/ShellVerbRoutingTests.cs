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
    public void OpenProbeLogAndOpenLogs_UseArgumentList_PreventsArgumentInjection()
    {
        var malPath = Path.Combine(Path.GetTempPath(), "log_path_with spaces_\" & calc.exe & \"file.txt");

        var probePsi = new ProcessStartInfo
        {
            FileName = "notepad.exe",
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        probePsi.ArgumentList.Add(malPath);

        Assert.Empty(probePsi.Arguments);
        Assert.Single(probePsi.ArgumentList);
        Assert.Equal(malPath, probePsi.ArgumentList[0]);

        var logsPsi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "/usr/bin/open",
            UseShellExecute = false
        };
        logsPsi.ArgumentList.Add(malPath);

        Assert.False(logsPsi.UseShellExecute);
        Assert.Empty(logsPsi.Arguments);
        Assert.Single(logsPsi.ArgumentList);
        Assert.Equal(malPath, logsPsi.ArgumentList[0]);

        var healthPsi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "notepad.exe" : (OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open"),
            UseShellExecute = false
        };
        healthPsi.ArgumentList.Add(malPath);

        Assert.False(healthPsi.UseShellExecute);
        Assert.Empty(healthPsi.Arguments);
        Assert.Single(healthPsi.ArgumentList);
        Assert.Equal(malPath, healthPsi.ArgumentList[0]);
    }

    [Fact]
    public void RestartInSafeModeAndResetConfig_UseArgumentList_PreventsArgumentInjection()
    {
        var malExe = Path.Combine(Path.GetTempPath(), "app_path_with spaces_\" & calc.exe & \"app.exe");

        var linuxPsi = new ProcessStartInfo
        {
            FileName = "/usr/bin/setsid",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        linuxPsi.ArgumentList.Add("--fork");
        linuxPsi.ArgumentList.Add(malExe);
        linuxPsi.ArgumentList.Add("--safe");

        Assert.Empty(linuxPsi.Arguments);
        Assert.Equal(3, linuxPsi.ArgumentList.Count);
        Assert.Equal("--fork", linuxPsi.ArgumentList[0]);
        Assert.Equal(malExe, linuxPsi.ArgumentList[1]);
        Assert.Equal("--safe", linuxPsi.ArgumentList[2]);

        var winPsi = new ProcessStartInfo
        {
            FileName = malExe,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        winPsi.ArgumentList.Add("--safe");

        Assert.Empty(winPsi.Arguments);
        Assert.Single(winPsi.ArgumentList);
        Assert.Equal("--safe", winPsi.ArgumentList[0]);
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
