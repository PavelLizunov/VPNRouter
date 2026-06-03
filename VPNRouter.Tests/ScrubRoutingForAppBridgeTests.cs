using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.40.0-r8 (#5 bug-scout regression fix) — pin
/// <c>MainWindowViewModel.ScrubRoutingForApp</c> driven THROUGH the real
/// <see cref="AppItemViewModel.IsChecked"/> read-through bridge, which is the
/// gap that let the bug ship: every prior survivor-guard test exercised the
/// pure <c>RoutingAppListEditor.IsStillRoutedByAnother</c> helper with
/// hand-built name lists, never the VM path where <c>item.IsChecked</c> reads
/// from the single shared routing-list entry.
///
/// <para>The bug: removing a duplicate-name app from one group un-routed it
/// from EVERY group, because <c>AppItemViewModel.IsChecked</c> is a
/// read-through to the single shared <see cref="AppConfig.RoutingAppsInclude"/>
/// / <c>RoutingAppsExclude</c> entry (one entry per process name, shared by
/// every AppItem with that name across groups). The pre-r8 scrub set
/// <c>item.IsChecked = false</c> — which empties that shared entry — BEFORE
/// computing survivors, so the survivor snapshot saw the OTHER checked
/// duplicate as unchecked too and dropped the name from all lists
/// (split-tunnel leak-from-intent). The fix snapshots the checked OTHER
/// duplicates first and early-returns when another checked AppItem still
/// routes the name.</para>
///
/// <para>These tests build REAL bridged state: each <see cref="AppItemViewModel"/>
/// is produced by the production private factory <c>CreateBridgedAppItem</c>
/// (reflected), so <c>IsChecked</c> is wired to the VM's actual mode-aware
/// <c>IsAppCheckedInCurrentMode</c> / <c>SetAppCheckedInCurrentMode</c> over the
/// real <c>_settings</c> list — no hand-mocked Read/Write callbacks. Mirrors the
/// harness setup in <see cref="MainWindowViewModelAppsModeTests"/>.</para>
/// </summary>
public class ScrubRoutingForAppBridgeTests
{
    private const string Dup = "Game.exe";

    private static AppSettings GetSettings(MainWindowViewModel vm)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (AppSettings)field!.GetValue(vm)!;
    }

    /// <summary>
    /// Build an AppItem through the production <c>CreateBridgedAppItem</c>
    /// factory so its <see cref="AppItemViewModel.IsChecked"/> reads/writes the
    /// active mode list via the same bridge LoadApps wires at runtime — the
    /// whole point of this suite is to exercise that bridge, not a mock.
    /// </summary>
    private static AppItemViewModel CreateBridgedItem(
        MainWindowViewModel vm, string name, bool isCustom)
    {
        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateBridgedAppItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        // signature: CreateBridgedAppItem(string processName, bool legacyChecked, bool isCustom)
        return (AppItemViewModel)factory!.Invoke(vm, new object[] { name, false, isCustom })!;
    }

    /// <summary>
    /// Put the VM in include mode with a single shared routing entry for
    /// <see cref="Dup"/> and a clean <c>AppGroups</c> so the survivor scan is
    /// hermetic (only the items this test adds exist). Safe because
    /// <c>SaveSettings</c> never rebuilds RoutingAppsInclude/Exclude from
    /// AppGroups — it only serialises the existing <c>_settings</c> lists.
    /// </summary>
    private static AppSettings ArrangeIncludeModeWithSharedEntry(MainWindowViewModel vm)
    {
        var settings = GetSettings(vm);
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { Dup };
        settings.App.RoutingAppsExclude = new List<string>();
        vm.RoutingAppsMode = "include";
        vm.AppGroups.Clear();
        return settings;
    }

    /// <summary>
    /// Primary repro via the RemoveCustomApp path: two groups (a bundled
    /// profile group + a custom category) each hold a bridged AppItem for the
    /// same process name, both reading <c>IsChecked == true</c> through the one
    /// shared list entry. Removing the custom-group instance MUST leave the
    /// name routed (the bundled survivor still wants it) and the survivor
    /// AppItem MUST still read checked.
    /// </summary>
    [AvaloniaFact]
    public void RemoveCustomApp_WhenAnotherGroupStillRoutesSameName_KeepsItRoutedForSurvivor()
    {
        var vm = new MainWindowViewModel();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var bundled = new AppGroupViewModel("Discord_Privacy", "", isChecked: true);
        var bundledItem = CreateBridgedItem(vm, Dup, isCustom: false);
        bundled.Apps.Add(bundledItem);

        var custom = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        var customItem = CreateBridgedItem(vm, Dup, isCustom: true);
        custom.Apps.Add(customItem);

        vm.AppGroups.Add(bundled);
        vm.AppGroups.Add(custom);

        // Sanity: both duplicates read checked through the shared bridge entry.
        Assert.True(bundledItem.IsChecked);
        Assert.True(customItem.IsChecked);

        // Act: remove only the custom-group instance (drives ScrubRoutingForApp
        // once, where the pre-r8 uncheck-before-snapshot collapsed the shared
        // entry and un-routed the app from every group).
        vm.RemoveCustomAppCommand.Execute(customItem);

        // The bundled survivor still routes the name...
        Assert.Contains(Dup, settings.App.RoutingAppsInclude);
        // ...and its AppItem still reads checked through the intact entry.
        Assert.True(bundledItem.IsChecked, "surviving duplicate must still read IsChecked == true");
        // The removed row is gone from its group; the survivor's group untouched.
        Assert.DoesNotContain(customItem, custom.Apps);
        Assert.Contains(bundledItem, bundled.Apps);
    }

    /// <summary>
    /// Same survivor-guard contract via the other ScrubRoutingForApp caller:
    /// RemoveCategory scrubs every app in the category before dropping it.
    /// Deleting the custom category that shares the name with a bundled group
    /// MUST keep the name routed for the bundled survivor.
    /// </summary>
    [AvaloniaFact]
    public void RemoveCategory_WhenAnotherGroupStillRoutesSameName_KeepsItRoutedForSurvivor()
    {
        var vm = new MainWindowViewModel();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var bundled = new AppGroupViewModel("Discord_Privacy", "", isChecked: true);
        var bundledItem = CreateBridgedItem(vm, Dup, isCustom: false);
        bundled.Apps.Add(bundledItem);

        var category = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        category.Apps.Add(CreateBridgedItem(vm, Dup, isCustom: true));

        vm.AppGroups.Add(bundled);
        vm.AppGroups.Add(category);

        Assert.True(bundledItem.IsChecked);

        // Act: remove the whole custom category (ScrubRoutingForApp per item).
        vm.RemoveCategoryCommand.Execute(category);

        Assert.Contains(Dup, settings.App.RoutingAppsInclude);
        Assert.True(bundledItem.IsChecked, "surviving duplicate must still read IsChecked == true");
        Assert.DoesNotContain(category, vm.AppGroups);
    }

    /// <summary>
    /// Mirror negative case: the fix must not over-correct into never-removing.
    /// When NO other checked AppItem routes the name, removing the only instance
    /// DOES drop it from RoutingAppsInclude (proves the early-return is gated on
    /// a real survivor, not unconditional).
    /// </summary>
    [AvaloniaFact]
    public void RemoveCustomApp_WhenNoOtherGroupRoutesName_DropsItFromRouting()
    {
        var vm = new MainWindowViewModel();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var only = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        var item = CreateBridgedItem(vm, Dup, isCustom: true);
        only.Apps.Add(item);
        vm.AppGroups.Add(only);

        Assert.True(item.IsChecked);

        // Act: remove the only instance — nothing else routes it.
        vm.RemoveCustomAppCommand.Execute(item);

        Assert.DoesNotContain(Dup, settings.App.RoutingAppsInclude);
        Assert.DoesNotContain(item, only.Apps);
    }
}
