using System.Collections.Generic;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SubscriptionViewModelBadgeTests
{
    private static SubscriptionViewModel Vm(int cachedServers)
    {
        var entry = new SubscriptionEntry { Name = "Sub", Url = "https://example/sub" };
        for (int i = 0; i < cachedServers; i++)
            entry.Servers.Add(new VlessServerEntry());
        return new SubscriptionViewModel(entry);
    }

    [Fact]
    public void HappyPath_NoBadge()
    {
        var vm = Vm(4);
        vm.LastRefreshFailed = false;
        Assert.Equal(string.Empty, vm.StatusBadge);
    }

    [Fact]
    public void FetchFailed_WithCache_ShowsCachedBadge()
    {
        var vm = Vm(4);
        vm.LastRefreshFailed = true;
        Assert.Equal(4, vm.CachedServerCount);
        Assert.Equal(VPNRouter.Core.Localization.Strings.SubRefreshFailedCached, vm.StatusBadge);
        Assert.NotEqual(string.Empty, vm.StatusBadge);
    }

    [Fact]
    public void FetchFailed_NoCache_ShowsUnreachableBadge()
    {
        var vm = Vm(0);
        vm.LastRefreshFailed = true;
        Assert.Equal(0, vm.CachedServerCount);
        Assert.Equal(VPNRouter.Core.Localization.Strings.SubRefreshFailedEmpty, vm.StatusBadge);
    }

    [Fact]
    public void StatusBadge_RaisesPropertyChanged_OnFailedFlagFlip()
    {
        var vm = Vm(2);
        var fired = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) fired.Add(e.PropertyName); };
        vm.LastRefreshFailed = true;
        Assert.Contains(nameof(SubscriptionViewModel.StatusBadge), fired);
    }
}
