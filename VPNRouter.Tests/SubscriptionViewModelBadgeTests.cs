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
    public void StatusBadge_RaisesPropertyChanged_OnFailedFlagFlip()
    {
        var vm = Vm(2);
        var fired = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) fired.Add(e.PropertyName); };
        vm.LastRefreshFailed = true;
        Assert.Contains(nameof(SubscriptionViewModel.StatusBadge), fired);
    }
}
