using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class ScrubRoutingForAppBridgeTests
{
    private const string Dup = "Game.exe";

    private static MainWindowViewModel MakeVm() => new(new InMemorySettingsStore());

    private static AppSettings GetSettings(MainWindowViewModel vm)
    {
        var field = typeof(MainWindowViewModel).GetField(
            "_settings",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (AppSettings)field!.GetValue(vm)!;
    }

    private static AppItemViewModel CreateBridgedItem(
        MainWindowViewModel vm, string name, bool isCustom)
    {
        var factory = typeof(MainWindowViewModel).GetMethod(
            "CreateBridgedAppItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        return (AppItemViewModel)factory!.Invoke(vm, new object[] { name, false, isCustom })!;
    }

    private static AppSettings ArrangeIncludeModeWithSharedEntry(MainWindowViewModel vm)
    {
        var settings = GetSettings(vm);
        settings.App.RoutingAppsMode = "include";
        settings.App.RoutingAppsInclude = new List<string> { Dup };
        settings.App.RoutingAppsExclude = new List<string>();
        vm.RoutingAppsMode = "include";
        vm.AppGroups.Clear();
        vm.BypassAppGroups.Clear();
        return settings;
    }

    [AvaloniaFact]
    public void RemoveCustomApp_WhenAnotherGroupStillRoutesSameName_KeepsItRoutedForSurvivor()
    {
        var vm = MakeVm();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var bundled = new AppGroupViewModel("Discord_Privacy", "", isChecked: true);
        var bundledItem = CreateBridgedItem(vm, Dup, isCustom: false);
        bundled.Apps.Add(bundledItem);

        var custom = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        var customItem = CreateBridgedItem(vm, Dup, isCustom: true);
        custom.Apps.Add(customItem);

        vm.AppGroups.Add(bundled);
        vm.AppGroups.Add(custom);

        Assert.True(bundledItem.IsChecked);
        Assert.True(customItem.IsChecked);

        vm.RemoveCustomAppCommand.Execute(customItem);

        Assert.Contains(Dup, settings.App.RoutingAppsInclude);
        Assert.True(bundledItem.IsChecked, "surviving duplicate must still read IsChecked == true");
        Assert.DoesNotContain(customItem, custom.Apps);
        Assert.Contains(bundledItem, bundled.Apps);
    }

    [AvaloniaFact]
    public void RemoveCategory_WhenAnotherGroupStillRoutesSameName_KeepsItRoutedForSurvivor()
    {
        var vm = MakeVm();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var bundled = new AppGroupViewModel("Discord_Privacy", "", isChecked: true);
        var bundledItem = CreateBridgedItem(vm, Dup, isCustom: false);
        bundled.Apps.Add(bundledItem);

        var category = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        category.Apps.Add(CreateBridgedItem(vm, Dup, isCustom: true));

        vm.AppGroups.Add(bundled);
        vm.AppGroups.Add(category);

        Assert.True(bundledItem.IsChecked);

        vm.RemoveCategoryCommand.Execute(category);

        Assert.Contains(Dup, settings.App.RoutingAppsInclude);
        Assert.True(bundledItem.IsChecked, "surviving duplicate must still read IsChecked == true");
        Assert.DoesNotContain(category, vm.AppGroups);
    }

    [AvaloniaFact]
    public void RemoveCustomApp_WhenNoOtherGroupRoutesName_DropsItFromRouting()
    {
        var vm = MakeVm();
        var settings = ArrangeIncludeModeWithSharedEntry(vm);

        var only = new AppGroupViewModel("My Games", "", isChecked: true) { IsCustomCategory = true };
        var item = CreateBridgedItem(vm, Dup, isCustom: true);
        only.Apps.Add(item);
        vm.AppGroups.Add(only);

        Assert.True(item.IsChecked);

        vm.RemoveCustomAppCommand.Execute(item);

        Assert.DoesNotContain(Dup, settings.App.RoutingAppsInclude);
        Assert.DoesNotContain(item, only.Apps);
    }
}
