using System.Collections.Generic;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.38.0-r7 — pins the honest "couldn't refresh — showing cached" badge on
/// the subscription card. Born from the Z:\surito 2026-05-29 diagnosis: a
/// provider DPI-flap made the subscription fetch fail, the card dropped to
/// "0s · —", and the user read it as "configs lost / account banned" — when in
/// fact the API was returning servers fine and (where cached) they're preserved.
/// The badge distinguishes (a) failed-but-cached from (b) failed-and-empty
/// (provider unreachable), instead of a bare, alarming "0 servers".
/// </summary>
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
        // "showing cached" — NOT the empty/unreachable variant.
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
