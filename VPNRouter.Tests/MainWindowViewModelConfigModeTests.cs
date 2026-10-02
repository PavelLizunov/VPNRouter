using System.Collections.Generic;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class MainWindowViewModelConfigModeTests
{
    private static InMemorySettingsStore StoreWithSubscriptionAndForgottenLink()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = new List<SubscriptionEntry>
        {
            new()
            {
                Name = "simple", Url = "https://sub.example/c/1", Enabled = true,
                Servers = new List<VlessServerEntry> { new() { Name = "Sweden VLESS ~ninitux", Server = "155.4.244.204", Port = 443 } },
            },
        };
        settings.App.ActiveSubscriptionServer = "Sweden VLESS ~ninitux";
        settings.Vless.Servers = new List<VlessServerEntry> { new() { Name = "", Server = "130.94.0.171", Port = 443 } };
        var store = new InMemorySettingsStore();
        store.Save(settings, AppPaths.ConfigYamlPath);
        return store;
    }

    [Fact]
    public void RoutingChangeOnTheHomeScreen_KeepsTheSubscription_EvenWhenTheLastAdvancedTabWasServers()
    {
        var store = StoreWithSubscriptionAndForgottenLink();
        using var vm = new MainWindowViewModel(store);
        vm.IsSimpleMode = true;
        vm.IsSubscribeMode = false;
        vm.IsVlessMode = true;

        vm.IsSplitTunnel = !vm.IsSplitTunnel;

        Assert.Equal("subscribe", store.Load(AppPaths.ConfigYamlPath).App.ConfigMode);
    }

    [Fact]
    public void HomeCard_WithASubscription_NamesTheSubscriptionKind_NotTheForgottenLink()
    {
        var store = StoreWithSubscriptionAndForgottenLink();
        using var vm = new MainWindowViewModel(store);

        Assert.Contains(VPNRouter.App.Localization.Strings.SmpKindSubscription, vm.SmpConfigKind);
        Assert.DoesNotContain("130.94.0.171", vm.SmpConfigName);
    }
}
