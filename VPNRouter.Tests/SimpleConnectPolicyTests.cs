using System.Collections.Generic;
using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SimpleConnectPolicyTests
{
    private static SubscriptionEntry Sub(string url, int servers, bool enabled = true) => new()
    {
        Name = "simple", Url = url, Enabled = enabled,
        Servers = new List<VlessServerEntry>(Enumerable.Range(0, servers).Select(i => new VlessServerEntry { Name = $"s{i}", Server = "1.1.1.1", Port = 443 })),
    };

    [Fact]
    public void SameUrlWithCachedServers_IsKept_NotReplacedOrRefetched()
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput("https://sub.example/c/1", new[] { Sub("https://sub.example/c/1", 13) });

        Assert.Equal(SubscriptionInputAction.KeepCached, action);
    }

    [Theory]
    [InlineData("https://sub.example/c/1/")]
    [InlineData("  https://SUB.example/c/1  ")]
    public void TrailingSlashCaseAndSpaces_DoNotMakeItANewUrl(string pasted)
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput(pasted, new[] { Sub("https://sub.example/c/1", 3) });

        Assert.Equal(SubscriptionInputAction.KeepCached, action);
    }

    [Fact]
    public void SameUrlWithoutServers_IsRefreshedInPlace()
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput("https://sub.example/c/1", new[] { Sub("https://sub.example/c/1", 0) });

        Assert.Equal(SubscriptionInputAction.KeepAndRefresh, action);
    }

    [Fact]
    public void ANewUrl_ReplacesTheHomeScreensSubscription()
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput("https://other.example/x", new[] { Sub("https://sub.example/c/1", 13) });

        Assert.Equal(SubscriptionInputAction.Replace, action);
    }

    [Fact]
    public void ADisabledSubscription_DoesNotCount()
    {
        var action = SimpleConnectPolicy.DecideSubscriptionInput("https://sub.example/c/1", new[] { Sub("https://sub.example/c/1", 13, enabled: false) });

        Assert.Equal(SubscriptionInputAction.Replace, action);
    }

    [Fact]
    public void FirstSubscriptionEver_IsReplace()
    {
        Assert.Equal(SubscriptionInputAction.Replace, SimpleConnectPolicy.DecideSubscriptionInput("https://sub.example/c/1", new List<SubscriptionEntry>()));
        Assert.Equal(SubscriptionInputAction.Replace, SimpleConnectPolicy.DecideSubscriptionInput("https://sub.example/c/1", null));
    }

    [Theory]
    [InlineData("Sweden", true, true)]
    [InlineData("Sweden", false, false)]
    [InlineData("Gone", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void SelectedServerIsProbedFirst_OnlyWhenItExistsAndTheIntentIsGeneral(string? active, bool general, bool expected)
    {
        var candidates = new[] { new VlessServerEntry { Name = "Sweden", Server = "1.1.1.1", Port = 443 }, new VlessServerEntry { Name = "Iceland", Server = "2.2.2.2", Port = 443 } };

        Assert.Equal(expected, SimpleConnectPolicy.ShouldProbeSelectedFirst(active, candidates, general));
    }
}
