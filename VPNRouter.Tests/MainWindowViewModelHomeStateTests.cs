using System.Collections.Generic;
using System.Reflection;
using Avalonia.Headless.XUnit;
using VPNRouter.App.Localization;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

// The home screen (Simple page, concept A) binds to these read-only projections; they must follow the state they read.
public class MainWindowViewModelHomeStateTests
{
    private static void AddServer(MainWindowViewModel vm, string name)
    {
        var server = new ServerViewModel(new VlessServerEntry { Name = name, Server = "203.0.113.10", Port = 443 });
        vm.Servers.Add(server);
        vm.SelectedServer = server;
    }

    [AvaloniaFact]
    public void FreshInstall_AsksForConfig_AndConnectWaitsForInput()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());

        Assert.False(vm.SmpHasConfig);
        Assert.True(vm.SmpNeedsConfig);
        Assert.True(vm.SmpConfigEditorVisible);
        Assert.False(vm.SmpCanConnect);
        Assert.True(vm.SmpHeroIsIdle);
        Assert.Equal(Strings.SmpHeroAddConfigTitle, vm.SmpHomeTitle);
        Assert.Equal(Strings.SmpCtaConnect, vm.SimpleCtaText);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SmpInput = "vless://example";

        Assert.True(vm.SmpCanConnect);
        Assert.Contains(nameof(MainWindowViewModel.SmpCanConnect), changed);
    }

    [AvaloniaFact]
    public void SavedServer_HidesEditor_AndNamesTheConfig()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        AddServer(vm, "Frankfurt 1");

        Assert.True(vm.SmpHasConfig);
        Assert.False(vm.SmpNeedsConfig);
        Assert.False(vm.SmpConfigEditorVisible);
        Assert.True(vm.SmpCanConnect);
        Assert.Equal("Frankfurt 1", vm.SmpConfigName);

        vm.OpenConfigPickerCommand.Execute(null);
        Assert.True(vm.SmpConfigEditorVisible);
    }

    [AvaloniaFact]
    public void ConnectionStates_MapToOneHeroState()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        AddServer(vm, "Frankfurt 1");

        vm.IsConnecting = true;
        Assert.True(vm.SmpHeroIsBusy);
        Assert.False(vm.SmpHeroIsIdle);

        vm.IsConnecting = false;
        vm.IsConnected = true;
        Assert.True(vm.SmpHeroIsOn);
        Assert.Equal(Strings.SmpStatusProtected, vm.SmpHomeTitle);
        Assert.Equal(Strings.SmpCtaDisconnect, vm.SimpleCtaText);

        vm.IsConnected = false;
        vm.SmpErrorText = "boom";
        Assert.True(vm.SmpHeroIsError);
        Assert.False(vm.SmpHeroIsIdle);
        Assert.Equal(Strings.SmpHeroErrorTitle, vm.SmpHomeTitle);
        Assert.Equal("boom", vm.SmpHomeSubline);
    }

    [AvaloniaFact]
    public void FailoverAlert_IsAWarning_WithoutTheTextSymbol()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        AddServer(vm, "Frankfurt 1");
        vm.IsConnected = true;

        typeof(MainWindowViewModel).GetField("_lastConnectionAlert", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, "⚠ The server does not respond.");

        Assert.True(vm.SmpHeroIsWarn);
        Assert.False(vm.SmpHeroIsOn);
        Assert.Equal(Strings.SmpHeroWarnTitle, vm.SmpHomeTitle);
        Assert.Equal("The server does not respond.", vm.SmpHomeSubline);
    }
}
