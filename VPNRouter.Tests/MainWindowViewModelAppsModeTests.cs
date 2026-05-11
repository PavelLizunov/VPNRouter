using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// AM-3 (2026-05-12) — pin the two-independent-selection-states contract
/// for the Apps Include/Exclude mode bridge in
/// <see cref="MainWindowViewModel"/>. The user's brother (stas)
/// requested explicitly: switching mode must NOT copy or wipe the
/// inactive list — each mode owns its own list of process names.
///
/// <para>The tests rely on the bridge wiring set up in LoadApps, but
/// most assertions go through the internal helpers
/// (<c>IsAppCheckedInCurrentMode</c> / <c>SetAppCheckedInCurrentMode</c>)
/// and the canonical settings fields
/// (<see cref="AppConfig.RoutingAppsInclude"/> /
/// <see cref="AppConfig.RoutingAppsExclude"/>) so the test suite is
/// stable against UI-template changes.</para>
/// </summary>
public class MainWindowViewModelAppsModeTests
{
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

    [AvaloniaFact]
    public void IsAppCheckedInCurrentMode_ReadsFromRoutingAppsInclude_WhenIncludeMode()
    {
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { "chrome.exe", "Firefox.exe" };
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe" };
        vm.RoutingAppsMode = "include";

        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
        // Case-insensitive compare — sing-box is case-sensitive but the
        // bridge protects users from accidental casing mismatches.
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "CHROME.EXE"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "firefox.EXE"));
        // Steam is in Exclude list — NOT seen as checked in include mode.
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));
        // Unknown app — unchecked.
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "nowhere.exe"));
    }

    [AvaloniaFact]
    public void IsAppCheckedInCurrentMode_ReadsFromRoutingAppsExclude_WhenExcludeMode()
    {
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "chrome.exe", "Firefox.exe" };
        settings.App.RoutingAppsExclude = new List<string> { "Steam.exe", "bank.exe" };
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "bank.exe"));
        // Apps in include list are NOT visible while exclude mode active.
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
    }

    [AvaloniaFact]
    public void SetAppCheckedInCurrentMode_WritesToActiveMode_OnlyTouchesActiveList()
    {
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        // Add "Discord.exe" via the bridge in include mode → must land
        // in RoutingAppsInclude only.
        InvokeSetAppCheckedInCurrentMode(vm, "Discord.exe", true);
        Assert.Contains("Discord.exe", settings.App.RoutingAppsInclude);
        Assert.Empty(settings.App.RoutingAppsExclude);

        // Switch to exclude mode + add "Steam.exe" — must land in
        // RoutingAppsExclude only.
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        InvokeSetAppCheckedInCurrentMode(vm, "Steam.exe", true);
        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
        Assert.DoesNotContain("Steam.exe", settings.App.RoutingAppsInclude);
        // Include list state preserved.
        Assert.Contains("Discord.exe", settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void SwitchMode_TwoIndependentSelectionStates()
    {
        // This is the core acceptance: user toggles checkboxes in
        // Include mode (chrome + firefox), flips to Exclude mode →
        // checkboxes for chrome + firefox are NOT seen as checked, user
        // toggles Steam → Steam is in Exclude list, flips back to Include
        // mode → chrome + firefox are still checked there, Steam is NOT.
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        InvokeSetAppCheckedInCurrentMode(vm, "chrome.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "Firefox.exe", true);
        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);
        Assert.Empty(settings.App.RoutingAppsExclude);

        // Flip mode. Triggers OnRoutingAppsModeChanged → RefreshAppCheckboxes
        // (just OnPropertyChanged spam, no state mutation).
        vm.RoutingAppsMode = "exclude";

        // Same data in include list — exclude list still empty.
        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);
        Assert.Empty(settings.App.RoutingAppsExclude);
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));

        // User checks Steam in exclude mode.
        InvokeSetAppCheckedInCurrentMode(vm, "Steam.exe", true);
        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
        Assert.Equal(2, settings.App.RoutingAppsInclude.Count);

        // Back to include — chrome + firefox visible again.
        vm.RoutingAppsMode = "include";
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "chrome.exe"));
        Assert.True(InvokeIsAppCheckedInCurrentMode(vm, "Firefox.exe"));
        Assert.False(InvokeIsAppCheckedInCurrentMode(vm, "Steam.exe"));

        // And the exclude list is preserved.
        Assert.Contains("Steam.exe", settings.App.RoutingAppsExclude);
    }

    [AvaloniaFact]
    public void BridgedAppItem_IsCheckedReflectsList_InCurrentMode()
    {
        // The AppItem bridge ReadMode delegates to
        // IsAppCheckedInCurrentMode. Pin that the ViewModel-level bridge
        // wiring surfaces correctly to the AppItem.IsChecked getter.
        var vm = new MainWindowViewModel();
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

        // Switch the active mode list to exclude — item.IsChecked must
        // re-evaluate to false because Test.exe isn't in the exclude
        // list.
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public void BridgedAppItem_SetterWritesIntoActiveList()
    {
        var vm = new MainWindowViewModel();
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

        // Switch to exclude and add via bridge.
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";
        item.IsChecked = true;
        Assert.Contains("test.exe", settings.App.RoutingAppsExclude);
        Assert.Empty(settings.App.RoutingAppsInclude);
    }

    [AvaloniaFact]
    public void GroupCascade_FlipsAllItems_InCurrentMode()
    {
        // AppGroup.OnIsCheckedChanged sets IsChecked = value on every
        // child. With AM-3 bridging this writes into the active list.
        // Pin that the cascade affects only the active list.
        var vm = new MainWindowViewModel();
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

        // Toggle group → cascade fires → both items get IsChecked=true.
        group.IsChecked = true;
        Assert.Contains("a.exe", settings.App.RoutingAppsInclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsInclude);

        // Switch mode. List state preserved (independent lists).
        settings.App.RoutingAppsMode = "exclude";
        vm.RoutingAppsMode = "exclude";

        // Untoggle the group in exclude mode: cascade sets each item's
        // IsChecked = false. Since both items are currently "unchecked"
        // in exclude mode (RoutingAppsExclude is empty), the cascade is
        // a no-op for the exclude list.
        group.IsChecked = false;
        Assert.Empty(settings.App.RoutingAppsExclude);
        // Include list untouched by the exclude-mode cascade.
        Assert.Contains("a.exe", settings.App.RoutingAppsInclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsInclude);

        // Now in exclude mode, toggle group ON → both items added to
        // RoutingAppsExclude.
        group.IsChecked = true;
        Assert.Contains("a.exe", settings.App.RoutingAppsExclude);
        Assert.Contains("b.exe", settings.App.RoutingAppsExclude);
    }

    [AvaloniaFact]
    public void OnRoutingAppsModeChanged_FiresIsCheckedNotifications()
    {
        // Mode flip must trigger PropertyChanged on every AppItem's
        // IsChecked so XAML CheckBox bindings re-read from the now-active
        // list. The RefreshAppCheckboxes path is the wiring under test.
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string> { "x.exe" };
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        // Find any AppGroup that LoadApps populated; create one+attach
        // manually if none exists. The factory pattern in LoadApps
        // wires bridges through the VM's helpers, but here we just want
        // to observe notifications.
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
            // Inject a bridged item manually (the ctor doesn't wire
            // bridges by default; do it via CreateBridgedAppItem
            // through reflection).
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

        // Flip mode → RefreshAppCheckboxes runs → notification fires.
        vm.RoutingAppsMode = "exclude";
        Assert.NotEmpty(notifications);
    }

    [AvaloniaFact]
    public void SetAppCheckedInCurrentMode_IsIdempotent_NoDuplicates()
    {
        // Calling Set(true) on the same name twice must not produce
        // duplicate entries — the list.Add only fires if the entry
        // isn't already present.
        var vm = new MainWindowViewModel();
        var settings = GetSettings(vm);
        settings.App.RoutingAppsInclude = new List<string>();
        settings.App.RoutingAppsExclude = new List<string>();
        settings.App.RoutingAppsMode = "include";
        vm.RoutingAppsMode = "include";

        InvokeSetAppCheckedInCurrentMode(vm, "twin.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "twin.exe", true);
        InvokeSetAppCheckedInCurrentMode(vm, "TWIN.exe", true);  // diff casing
        Assert.Single(settings.App.RoutingAppsInclude);

        // Removing also idempotent (no-op when not present).
        InvokeSetAppCheckedInCurrentMode(vm, "missing.exe", false);
        Assert.Single(settings.App.RoutingAppsInclude);
    }
}

/// <summary>
/// AM-3 unit-level pins for <see cref="AppItemViewModel"/> bridge
/// callbacks (Read / Write / RaiseIsCheckedChanged). No Avalonia
/// dispatcher needed; pure data plumbing.
/// </summary>
public class AppItemViewModelBridgeTests
{
    [Fact]
    public void IsChecked_FallsBackToLocalField_WhenBridgeNotWired()
    {
        var item = new AppItemViewModel("a.exe", isChecked: true);
        // No ReadMode / WriteMode → falls back to ctor seed.
        Assert.True(item.IsChecked);

        item.IsChecked = false;
        Assert.False(item.IsChecked);
    }

    [Fact]
    public void IsChecked_RoutesToBridge_WhenWired()
    {
        var backing = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
        var item = new AppItemViewModel("a.exe", isChecked: true)
        {
            ReadMode = name => backing.TryGetValue(name, out var v) && v,
            WriteMode = (name, val) => backing[name] = val,
        };

        // ctor seed is irrelevant — bridge ReadMode wins.
        Assert.False(item.IsChecked);

        // Set true via bridge.
        item.IsChecked = true;
        Assert.True(backing.TryGetValue("a.exe", out var stored) && stored);
        Assert.True(item.IsChecked);

        // Set false.
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
        // Idempotent setter: writing the same value again must not fire
        // PropertyChanged (ObservableObject contract). Pin this so the
        // bridge doesn't double-fire on cascade or RefreshAppCheckboxes
        // when nothing changed.
        var item = new AppItemViewModel("a.exe", isChecked: true);
        int fired = 0;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppItemViewModel.IsChecked))
                fired++;
        };

        item.IsChecked = true; // unchanged
        Assert.Equal(0, fired);

        item.IsChecked = false; // change
        Assert.Equal(1, fired);
    }
}
