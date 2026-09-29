using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class TunMtuPersistenceTests
{
    [Fact]
    public void ManualMtuEdit_InRange_IsPersisted()
    {
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);
        var before = store.SaveCount;

        vm.TunMtu = 1400;

        Assert.True(store.SaveCount > before);
        Assert.Equal(1400, store.Load().Tun.Mtu);
    }

    [Fact]
    public void ManualMtuEdit_AboveMaximum_IsPersistedClamped()
    {
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);

        vm.TunMtu = 1600;

        Assert.Equal(TunSettings.MaximumMtu, store.Load().Tun.Mtu);
    }

    [Fact]
    public void ManualMtuEdit_BelowMinimum_IsNotPersisted()
    {
        var store = new InMemorySettingsStore();
        using var vm = new MainWindowViewModel(store);
        var before = store.SaveCount;

        vm.TunMtu = 12;

        Assert.Equal(before, store.SaveCount);
    }
}
