using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class MainWindowViewModelChangePanelTests
{
    private static ServerViewModel Server(string name) =>
        new(new VlessServerEntry { Name = name, Server = "203.0.113.10", Port = 443 });

    [Fact]
    public void FreshInstall_ShowsTheFirstRunEditorAboveTheButton_AndNoChangePanel()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());

        Assert.True(vm.SmpFirstRunEditorVisible);
        Assert.False(vm.SmpPickerVisible);
    }

    [Fact]
    public void WithAConfig_ChangeOpensThePanelInsideTheCard_NotTheEditorAboveTheButton()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.Servers.Add(Server("Frankfurt 1"));

        Assert.False(vm.SmpFirstRunEditorVisible);
        Assert.False(vm.SmpPickerVisible);

        vm.OpenConfigPickerCommand.Execute(null);

        Assert.True(vm.SmpPickerVisible);
        Assert.False(vm.SmpFirstRunEditorVisible);
        Assert.True(vm.SmpConfigEditorVisible);

        vm.OpenConfigPickerCommand.Execute(null);
        Assert.False(vm.SmpPickerVisible);
    }

    [Fact]
    public void Panel_OffersTheSubscriptionServers_WhenThereAreAny()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.Servers.Add(Server("Frankfurt 1"));
        Assert.False(vm.SmpHasSubscriptionServers);

        vm.SubscriptionServers.Add(Server("Sweden VLESS ~ninitux"));

        Assert.True(vm.SmpHasSubscriptionServers);
    }
}
