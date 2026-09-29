using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class MainWindowViewModelAppsModeTests
{
    private static MainWindowViewModel MakeVm() => new(new InMemorySettingsStore());

    private static AppSettings GetSettings(MainWindowViewModel vm)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (AppSettings)field!.GetValue(vm)!;
    }

    private static bool InvokeIsAppCheckedInCurrentMode(
        MainWindowViewModel vm, string processName)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "IsAppCheckedInCurrentMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(vm, new object?[] { processName })!;
    }

    private static void InvokeSetAppCheckedInCurrentMode(
        MainWindowViewModel vm, string processName, bool isChecked)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "SetAppCheckedInCurrentMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object?[] { processName, isChecked });
    }

    private static async Task InvokeTrueSplitStateAsync(
        MainWindowViewModel vm, TrueSplitState state, string reason = "")
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "OnTrueSplitStateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, new object?[] { state, reason });
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    private static void InvokeMarkTrueSplitServiceManaged(MainWindowViewModel vm)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "MarkTrueSplitServiceManagedIfNeeded",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(vm, null);
    }

    [AvaloniaFact]
    public void IsAppCheckedInCurrentMode_ReadsFromRoutingAppsInclude_WhenIncludeMode()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { "chrome.exe", "Firefox.exe" };
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };
        vm.RoutingAppsMode = "include";

        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "CHROME.EXE"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "firefox.EXE"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "nowhere.exe"));
    }

    [AvaloniaFact]
    public async Task TrueSplitActiveState_HidesStatusBanner()
    {
        var vm = MakeVm();
        vm.IsConnected = true;
        vm.IsSplitTunnel = true;
        vm.RoutingAppsMode = "exclude";

        await InvokeTrueSplitStateAsync(vm, TrueSplitState.Starting);
        Assert.True(vm.IsTrueSplitStatusVisible);
        Assert.False(vm.IsTrueSplitActive);

        await InvokeTrueSplitStateAsync(vm, TrueSplitState.Active);
        Assert.True(vm.IsTrueSplitActive);
        Assert.False(vm.IsTrueSplitStatusVisible);
    }

    [AvaloniaFact]
    public void TrueSplitServiceManaged_ShowsBannerWithoutRetry()
    {
        var vm = MakeVm();
        vm.IsConnected = true;
        vm.IsSplitTunnel = true;
        vm.RoutingAppsMode = "exclude";

        InvokeMarkTrueSplitServiceManaged(vm);

        Assert.True(vm.IsTrueSplitStatusVisible);
        Assert.True(vm.IsTrueSplitProblem);
        Assert.False(vm.IsTrueSplitRetryVisible);
        Assert.Contains("Windows", vm.TrueSplitStatusText);
    }

    [AvaloniaFact]
    public void IsAppCheckedInCurrentMode_ReadsFromRoutingAppsExclude_WhenExcludeMode()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "chrome.exe", "Firefox.exe" };
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe", "bank.exe" };
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "bank.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
    }

    [AvaloniaFact]
    public void SetAppCheckedInCurrentMode_WritesToActiveMode_OnlyTouchesActiveList()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        InvokeSetAppCheckedInCurrentMode(vm, "Discord.exe", true);
        Assert.Contains("Discord.exe", settings.App.RoutingAppsInclude);
        Assert.Empty(settings.App.RoutingAppsExclude);

        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        InvokeSetAppCheckedInCurrentMode(vm, "Steam.exe", true);
        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
        Assert.DoesNotContain("Steam.exe", settings.App.RoutingAppsInclude);
        Assert.Contains("Discord.exe", settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void SwitchMode_TwoIndependentSelectionStates()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        InvokeSetAppCheckedInCurrentMode(vm, "chrome.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "Firefox.exe", true);
        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);
        Assert.Empty(settings.App.RoutingAppsExclude);

        vm.RoutingAppsMode = "exclude";

        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);
        Assert.Empty(settings.App.RoutingAppsExclude);
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));

        InvokeSetAppCheckedInCurrentMode(vm, "Steam.exe", true);
        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);

        vm.RoutingAppsMode = "include";
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));

        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
    }

    [AvaloniaFact]
    public void BridgedAppItem_IsCheckedReflectsList_InCurrentMode()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "test.exe" };
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        var item = new AppItemViewModel("test.exe")
        {
            ReadMode = name => InvokeIsAppCheckedInCurrentMode(vm, name),
            WriteMode = (name, val) => InvokeSetAppCheckedInCurrentMode(vm, name, val),
        };
        Assert.True(item.IsChecked);

        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public void BridgedAppItem_SetterWritesIntoActiveList()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        var item = new AppItemViewModel("test.exe")
        {
            ReadMode = name => InvokeIsAppCheckedInCurrentMode(vm, name),
            WriteMode = (name, val) => InvokeSetAppCheckedInCurrentMode(vm, name, val),
        };

        item.IsChecked = true;
        Assert.Contains("test.exe", settings.App.RoutingAppsInclude);
        Assert.Empty(settings.App.RoutingAppsExclude);

        item.IsChecked = false;
        Assert.Empty(settings.App.RoutingAppsInclude);

        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        item.IsChecked = true;
        Assert.Contains("test.exe", settings.App.RoutingAppsExclude);
        Assert.Empty(settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void GroupCascade_FlipsAllItems_InCurrentMode()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        var group = new AppGroupViewModel("test-group", "desc", isChecked: false);
        var item1 = new AppItemViewModel("a.exe")
        {
            ReadMode = name => InvokeIsAppCheckedInCurrentMode(vm, name),
            WriteMode = (name, val) => InvokeSetAppCheckedInCurrentMode(vm, name, val),
        };
        var item2 = new AppItemViewModel("b.exe")
        {
            ReadMode = name => InvokeIsAppCheckedInCurrentMode(vm, name),
            WriteMode = (name, val) => InvokeSetAppCheckedInCurrentMode(vm, name, val),
        };
        group.Apps.Add(item1);
        group.Apps.Add(item2);

        group.IsChecked = true;
        Assert.Contains("a.exe", settings.App.RoutingAppsInclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsInclude);

        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        group.IsChecked = false;
        Assert.Empty(settings.App.RoutingAppsExclude);
        Assert.Contains("a.exe", settings.App.RoutingAppsInclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsInclude);

        group.IsChecked = true;
        Assert.Contains("a.exe", settings.App.RoutingAppsExclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsExclude);
    }

    [AvaloniaFact]
    public void LoadedApps_HaveSeparateIncludeAndExcludeItems()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();

        var includeGroup = vm.AppGroups.First(g => g.Name == "Gaming");
        var excludeGroup = vm.BypassAppGroups.First(g => g.Name == "Game_Launchers");
        var includeItem = includeGroup.Apps.First();
        var excludeItem = excludeGroup.Apps.First(a => a.ProcessName == "steam.exe");

        includeItem.IsChecked = true;

        Assert.Contains(includeItem.ProcessName, settings.App.RoutingAppsInclude);
        Assert.DoesNotContain(includeItem.ProcessName, settings.App.RoutingAppsExclude);
        Assert.True(includeItem.IsChecked);
        Assert.False(excludeItem.IsChecked);

        excludeItem.IsChecked = true;

        Assert.Contains(includeItem.ProcessName, settings.App.RoutingAppsInclude);
        Assert.Contains(includeItem.ProcessName, settings.App.RoutingAppsExclude);
        Assert.True(includeItem.IsChecked);
        Assert.True(excludeItem.IsChecked);
    }

    [AvaloniaFact]
    public void RoutingModeFlip_DoesNotChangeSeparateListCheckboxes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();

        var includeGroup = vm.AppGroups.First(g => g.Name == "Gaming");
        var excludeGroup = vm.BypassAppGroups.First(g => g.Name == "Game_Launchers");
        var includeItem = includeGroup.Apps.First();
        var excludeItem = excludeGroup.Apps.First(a => a.ProcessName == "steam.exe");

        includeItem.IsChecked = true;
        excludeItem.IsChecked = true;
        vm.RoutingAppsMode = "exclude";

        Assert.True(includeItem.IsChecked);
        Assert.True(excludeItem.IsChecked);

        vm.RoutingAppsMode = "include";

        Assert.True(includeItem.IsChecked);
        Assert.True(excludeItem.IsChecked);
    }

    [AvaloniaFact]
    public void AddCustomApp_InExcludeEditor_DoesNotTouchIncludeList()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        var expected = OperatingSystem.IsWindows() ? "OnlyBypass.exe" : "OnlyBypass";
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        vm.AppsListEditorMode = "exclude";
        vm.SelectedBypassAppGroup = vm.BypassAppGroups.First(g => g.Name == "Custom Apps");

        vm.AddCustomAppCommand.Execute("OnlyBypass.exe");

        Assert.Contains(expected, settings.App.RoutingAppsExclude);
        Assert.DoesNotContain(expected, settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void RoutingModeChange_ShowsMatchingListEditor()
    {
        var vm = MakeVm();

        vm.RoutingAppsMode = "exclude";
        Assert.Equal("exclude", vm.AppsListEditorMode);
        Assert.Same(vm.BypassAppGroups, vm.ActiveAppGroups);

        vm.RoutingAppsMode = "include";
        Assert.Equal("include", vm.AppsListEditorMode);
        Assert.Same(vm.AppGroups, vm.ActiveAppGroups);
    }

    [AvaloniaFact]
    public void ApplyAppChanges_IsDisabledDuringConnectionTransition()
    {
        var vm = MakeVm();
        vm.IsConnected = true;
        vm.IsConnecting = true;

        Assert.False(vm.CanApplyAppChanges);
    }

    [AvaloniaFact]
    public void ConnectionToggle_IsDisabledWhileAppChangesAreApplying()
    {
        var vm = MakeVm();
        vm.IsApplying = true;

        Assert.False(vm.CanToggleConnection);
    }

    [AvaloniaFact]
    public void ApplyCompletion_DoesNotClearNewerPendingRoutingEdit()
    {
        var vm = MakeVm();
        vm.IsConnected = true;
        var type = typeof(MainWindowViewModel);
        var mark = type.GetMethod(
            "MarkRoutingSettingsChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var clear = type.GetMethod(
            "ClearPendingIfRevisionUnchanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var revision = type.GetField(
            "_routingSettingsRevision",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        mark.Invoke(vm, null);
        var applyStartedAt = (int)revision.GetValue(vm)!;
        mark.Invoke(vm, null);
        clear.Invoke(vm, new object[] { applyStartedAt });

        Assert.True(vm.HasPendingAppChanges);
    }

    [AvaloniaFact]
    public void AddCustomApp_WithBuiltInCategorySelected_LandsInPersistedCustomGroup()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vm = MakeVm();
        vm.RoutingAppsMode = "exclude";
        vm.SelectedBypassAppGroup = vm.BypassAppGroups.First(g => g.Name != "Custom Apps");
        var expected = OperatingSystem.IsWindows() ? "PinnedGame.exe" : "PinnedGame";

        vm.AddCustomAppCommand.Execute("C:\\Games\\PinnedGame.exe");

        Assert.Contains(
            vm.BypassAppGroups.First(g => g.Name == "Custom Apps").Apps,
            a => a.ProcessName == expected);
        Assert.DoesNotContain(
            vm.SelectedBypassAppGroup.Apps,
            a => a.ProcessName == expected);
    }

    [AvaloniaFact]
    public void AddCustomApp_WithUserCategorySelected_StaysInThatCategory()
    {
        var vm = MakeVm();
        vm.NewCategoryName = "My games";
        vm.AddCategoryCommand.Execute(null);
        var category = vm.SelectedActiveAppGroup!;
        var expected = OperatingSystem.IsWindows() ? "MyGame.exe" : "MyGame";

        vm.AddCustomAppCommand.Execute("C:\\Games\\MyGame.exe");

        Assert.True(category.IsCustomCategory);
        Assert.Contains(category.Apps, a => a.ProcessName == expected);
    }

    [AvaloniaFact]
    public void BypassCatalogue_UsesWindowsBypassProfiles_NotIncludeProfiles()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var vm = MakeVm();

        Assert.Contains(vm.AppGroups, g => g.Name == "Gaming");
        Assert.DoesNotContain(vm.BypassAppGroups, g => g.Name == "Gaming");
        Assert.Contains(vm.BypassAppGroups, g => g.Name == "Game_Launchers");
        Assert.Contains(
            vm.BypassAppGroups.First(g => g.Name == "Game_Launchers").Apps,
            a => a.ProcessName == "steam.exe");
    }

    [Fact]
    public void ResolveBundledProfilePath_MissingBypassDoesNotFallbackToDefault()
    {
        var missing = MainWindowViewModel.ResolveBundledProfilePath(
            "definitely-missing-bypass.json",
            fallbackToDefault: false);

        Assert.Null(missing);
    }

    [AvaloniaFact]
    public void ImportedSteamCandidate_WritesToExcludeOnly()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();

        var method = typeof(MainWindowViewModel).GetMethod(
            "AddCustomAppCandidate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var added = (bool)method!.Invoke(vm, new object?[] { "Dota2.exe" })!;
        Assert.True(added);

        var expected = OperatingSystem.IsWindows() ? "Dota2.exe" : "Dota2";
        var item = vm.BypassAppGroups
            .First(g => g.Name == "Custom Apps")
            .Apps
            .First(a => a.ProcessName == expected);

        Assert.Contains(expected, settings.App.RoutingAppsExclude);
        Assert.DoesNotContain(expected, settings.App.RoutingAppsInclude);
        Assert.True(item.IsChecked);
    }

    [AvaloniaFact]
    public void RemoveCustomApp_RemovesMirroredCustomRows()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        var expected = OperatingSystem.IsWindows() ? "OnlyBypass.exe" : "OnlyBypass";
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        vm.AppsListEditorMode = "exclude";
        var bypassCustom = vm.BypassAppGroups.First(g => g.Name == "Custom Apps");
        vm.SelectedBypassAppGroup = bypassCustom;

        vm.AddCustomAppCommand.Execute("OnlyBypass.exe");
        var item = bypassCustom.Apps.First(a => a.ProcessName == expected);

        vm.RemoveCustomAppCommand.Execute(item);

        Assert.DoesNotContain(expected, settings.App.RoutingAppsExclude);
        Assert.DoesNotContain(vm.AppGroups.SelectMany(g => g.Apps), a => a.ProcessName == expected);
        Assert.DoesNotContain(vm.BypassAppGroups.SelectMany(g => g.Apps), a => a.ProcessName == expected);
    }

    [AvaloniaFact]
    public void RemoveCustomApp_KeepsExcludeRuleWhenBuiltInBypassRowSurvives()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vm = MakeVm();
        var settings = GetSettings(vm);
        vm.RoutingAppsMode = "exclude";

        var builtIn = vm.BypassAppGroups
            .Where(g => g.Name != "Custom Apps")
            .SelectMany(g => g.Apps)
            .First();
        vm.AddCustomAppCommand.Execute(builtIn.ProcessName);
        var custom = vm.BypassAppGroups
            .First(g => g.Name == "Custom Apps")
            .Apps
            .First(a => a.ProcessName.Equals(
                builtIn.ProcessName, System.StringComparison.OrdinalIgnoreCase));

        vm.RemoveCustomAppCommand.Execute(custom);

        Assert.Contains(settings.App.RoutingAppsExclude, p => p.Equals(
            builtIn.ProcessName, System.StringComparison.OrdinalIgnoreCase));
        Assert.True(builtIn.IsChecked);
    }

    [AvaloniaFact]
    public void RemoveCustomApp_ScrubsIncludeAndExcludeListsIndependently()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vm = MakeVm();
        var settings = GetSettings(vm);
        vm.RoutingAppsMode = "exclude";

        var builtIn = vm.BypassAppGroups
            .Where(g => g.Name != "Custom Apps")
            .SelectMany(g => g.Apps)
            .First();
        vm.AddCustomAppCommand.Execute(builtIn.ProcessName);
        settings.App.RoutingAppsInclude = new List<string> { builtIn.ProcessName };
        vm.AppGroups.Clear();
        var custom = vm.BypassAppGroups
            .First(g => g.Name == "Custom Apps")
            .Apps
            .First(a => a.ProcessName.Equals(
                builtIn.ProcessName, System.StringComparison.OrdinalIgnoreCase));

        vm.RemoveCustomAppCommand.Execute(custom);

        Assert.Empty(settings.App.RoutingAppsInclude);
        Assert.Contains(settings.App.RoutingAppsExclude, p => p.Equals(
            builtIn.ProcessName, System.StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void OnRoutingAppsModeChanged_FiresIsCheckedNotifications()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "x.exe" };
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        AppItemViewModel? targetItem = null;
        AppGroupViewModel? targetGroup = null;
        foreach (var g in vm.AppGroups)
        {
            foreach (var a in g.Apps)
            {
                if (string.Equals(a.ProcessName, "x.exe", System.StringComparison.OrdinalIgnoreCase))
                {
                    targetItem = a;
                    targetGroup = g;
                    break;
                }
            }
            if (targetItem != null) break;
        }
        if (targetItem == null)
        {
            var factory = typeof(MainWindowViewModel).GetMethod(
                "CreateBridgedAppItem",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(factory);
            targetItem = (AppItemViewModel)factory!.Invoke(
                vm, new object[] { "x.exe", false, false })!;
            targetGroup = new AppGroupViewModel("synthetic", "", true);
            targetGroup.Apps.Add(targetItem);
            vm.AppGroups.Add(targetGroup);
        }

        var notifications = new List<string>();
        targetItem!.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
                notifications.Add(targetItem.IsChecked ? "checked" : "unchecked");
        };

        vm.RoutingAppsMode = "exclude";
        Assert.NotEmpty(notifications);
    }

    [AvaloniaFact]
    public void SetAppCheckedInCurrentMode_IsIdempotent_NoDuplicates()
    {
        var vm = MakeVm();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        InvokeSetAppCheckedInCurrentMode(vm, "twin.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "twin.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "TWIN.exe", true);
        Assert.Single(settings.App.RoutingAppsInclude);

        InvokeSetAppCheckedInCurrentMode(vm, "missing.exe", false);
        Assert.Single(settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void ModeFlip_RefreshesCheckboxes_WhenSaveFails()
    {
        var vm = new MainWindowViewModel(new ThrowingSaveSettingsStore());
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "x.exe" };
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateBridgedAppItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        var item = (AppItemViewModel)factory!.Invoke(
            vm, new object[] { "x.exe", false, false })!;
        var group = new AppGroupViewModel("synthetic", "", true);
        group.Apps.Add(item);
        vm.AppGroups.Add(group);

        var notifications = new List<bool>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
                notifications.Add(item.IsChecked);
        };

        vm.RoutingAppsMode = "exclude";

        Assert.Equal("exclude", settings.App.RoutingAppsMode);
        Assert.Contains(false, notifications);
        Assert.False(item.IsChecked);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private sealed class ThrowingSaveSettingsStore : ISettingsStore
    {
        private readonly InMemorySettingsStore _inner = new();

        public AppSettings Load(string? path = null) => _inner.Load(path);
        public void Save(AppSettings settings, string? path = null) =>
            throw new UnauthorizedAccessException("test save failure");
        public string? ResetToDefaults(string? path = null) => _inner.ResetToDefaults(path);
        public string? ConsumeRecoveryNotice() => _inner.ConsumeRecoveryNotice();
        public (int Count, string AtUtc) ConsumePlaceholderPruneNotice(AppSettings settings) =>
            _inner.ConsumePlaceholderPruneNotice(settings);
        public void StartWatching(string? path = null, Action<AppSettings>? onReload = null) =>
            _inner.StartWatching(path, onReload);
        public void StopWatching() => _inner.StopWatching();
    }
}

public class AppItemViewModelBridgeTests
{
    [Fact]
    public void IsChecked_RoutesToBridge_WhenWired()
    {
        var backing = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
        var item = new AppItemViewModel("a.exe", isChecked: true)
        {
            ReadMode = name => backing.TryGetValue(name, out var v) && v,
            WriteMode = (name, val) => backing[name] = val,
        };

        Assert.False(item.IsChecked);

        item.IsChecked = true;
        Assert.True(backing.TryGetValue("a.exe", out var stored) && stored);
        Assert.True(item.IsChecked);

        item.IsChecked = false;
        Assert.False(backing.TryGetValue("a.exe", out var afterUnset) && afterUnset);
    }

    [Fact]
    public void RaiseIsCheckedChanged_FiresPropertyChanged()
    {
        var item = new AppItemViewModel("a.exe");
        var fired = false;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
                fired = true;
        };

        item.RaiseIsCheckedChanged();
        Assert.True(fired);
    }

    [Fact]
    public void IsChecked_SetterNoOps_WhenValueUnchanged()
    {
        var item = new AppItemViewModel("a.exe", isChecked: true);
        int fired = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
                fired++;
        };

        item.IsChecked = true;
        Assert.Equal(0, fired);

        item.IsChecked = false;
        Assert.Equal(1, fired);
    }
}
