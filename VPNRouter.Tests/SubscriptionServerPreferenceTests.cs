using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class SubscriptionServerPreferenceTests
{
    [Fact]
    public async Task CategorySurvivesServiceResolutionAndOnlyRecoversWithinItsGroup()
    {
        var outside = Row("outside", "same.invalid", "ws");
        var first = Row("daily", "same.invalid");
        var replacement = Row("daily", "backup.invalid");
        var settings = SubscriptionSettings(outside, first, replacement);
        settings.App.SubscriptionPreference = SubscriptionServerPreference.InCategory(ProtocolGroup.Daily);
        settings = VpnEngine.CopySessionSettings(settings);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(3, await SubscriptionResolver.ResolveAsync(settings, false, ct: ct));
        Assert.Equal("subscribe", settings.App.ConfigMode);
        Assert.Equal(ProtocolGroup.Daily, settings.App.SubscriptionPreference?.GetCategory());
        Assert.Equal(first.Server, Assert.Single(VlessServersResolver.GetActiveServers(settings)).Server);

        var store = new InMemorySettingsStore();
        store.Save(settings);
        var failover = new AutoFailoverEngine(settings, new ConfigSanityCheck(), store: store);
        Assert.True((await failover.HandleDeadConfigAsync("dead", ct)).Switched);
        Assert.Equal(replacement.Server, Assert.Single(VlessServersResolver.GetActiveServers(settings)).Server);
        Assert.Equal(ProtocolGroup.Daily, store.Load().App.SubscriptionPreference?.GetCategory());
        Assert.False((await failover.HandleDeadConfigAsync("dead", ct)).Switched);
    }

    [Fact]
    public async Task MissingCategoryReportsUnavailableInsteadOfAnEmptySubscription()
    {
        var settings = SubscriptionSettings(Row("web", "web.invalid", "ws"));
        settings.App.SubscriptionPreference = SubscriptionServerPreference.InCategory(ProtocolGroup.Daily);
        var store = new InMemorySettingsStore();
        var failover = new AutoFailoverEngine(settings, new ConfigSanityCheck(), store: store);

        var result = await failover.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken);

        Assert.False(result.Switched);
        Assert.Equal(VPNRouter.Core.Localization.Strings.SmpSelectionUnavailable, result.UserFacingMessage);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ManualPinKeepsTheExactRowOrReportsItMissing()
    {
        var sibling = Row("same", "same.invalid", "ws");
        var pinned = Row("same", "same.invalid");
        var manual = Row("manual", "manual.invalid");
        var settings = SubscriptionSettings(sibling, pinned);
        settings.Vless.Servers = [manual];
        settings.App.SubscriptionPreference = new()
        {
            FeedId = settings.App.Subscriptions[0].Id,
            RowKey = SubscriptionEntry.RowKey(pinned),
        };
        settings = VpnEngine.CopySessionSettings(settings);
        Assert.Equal(settings.App.SubscriptionPreference?.RowKey,
            SubscriptionEntry.RowKey(Assert.Single(VlessServersResolver.GetActiveServers(settings))));
        Assert.Equal(2, VlessServersResolver.Resolve(settings).Count);
        var store = new InMemorySettingsStore();
        var failover = new AutoFailoverEngine(settings, new ConfigSanityCheck(), store: store);
        Assert.False((await failover.HandleDeadConfigAsync("dead", TestContext.Current.CancellationToken)).Switched);
        Assert.Equal(0, store.SaveCount);

        settings.App.Subscriptions[0].Servers.RemoveAt(1);
        Assert.Empty(VlessServersResolver.Resolve(settings));
        Assert.Empty(VlessServersResolver.GetActiveServers(settings));
    }

    [Fact]
    public void SelectionKeepsTheRequiredHopFromTheSameFeed()
    {
        var upstream = Row("hop", "hop.invalid", "ws");
        upstream.OutboundId = "fixture-hop";
        var exit = Row("selected", "exit.invalid");
        exit.DetourVia = upstream.OutboundId;
        var settings = SubscriptionSettings(upstream, exit);
        settings.App.SubscriptionPreference = new()
        {
            FeedId = settings.App.Subscriptions[0].Id,
            RowKey = SubscriptionEntry.RowKey(exit),
        };

        VlessServersResolver.Resolve(settings);
        Assert.Equal(new[] { exit, upstream }, VlessServersResolver.GetActiveServers(settings));
    }

    [Fact]
    public void CategoryExcludesExactOnlyRoutes()
    {
        var chained = Row("chain", "chain.invalid");
        chained.DetourVia = "fixture-hop";
        var direct = Row("plain", "plain.invalid");
        var settings = SubscriptionSettings(chained, direct);
        settings.App.SubscriptionPreference = SubscriptionServerPreference.InCategory(ProtocolGroup.Daily);

        Assert.Null(SubscriptionChainSelection.Capture(settings.App));
        Assert.Same(direct, Assert.Single(VlessServersResolver.GetActiveServers(settings)));
        settings.App.Subscriptions[0].Servers.Remove(direct);
        Assert.Empty(VlessServersResolver.Resolve(settings));
    }

    [Fact]
    public void ChainPinCanFollowANewOpaqueIdForTheSameRegisteredRoute()
    {
        var hop = Row("hop", "hop.invalid");
        hop.OutboundId = "old-hop";
        var target = Row("target", "target.invalid");
        target.DetourVia = hop.OutboundId;
        var settings = SubscriptionSettings(hop, target);
        var feed = settings.App.Subscriptions[0];
        settings.App.SubscriptionPreference = new() { FeedId = feed.Id, RowKey = SubscriptionEntry.RowKey(target) };
        var previous = Assert.IsType<SubscriptionChainSelection>(SubscriptionChainSelection.Capture(settings.App));

        var newHop = Row("hop", "hop.invalid");
        newHop.OutboundId = "new-hop";
        var newTarget = Row("target", "target.invalid");
        newTarget.DetourVia = newHop.OutboundId;
        feed.Servers = [newHop, newTarget];
        previous.RebindPin(settings.App, settings.App.Subscriptions, newTarget);

        Assert.Equal(SubscriptionEntry.RowKey(newTarget), settings.App.SubscriptionPreference?.RowKey);
        Assert.Equal(new[] { newTarget, newHop }, VlessServersResolver.GetActiveServers(settings));
    }

    private static AppSettings SubscriptionSettings(params VlessServerEntry[] servers)
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.Subscriptions = [new() { Servers = [.. servers] }];
        settings.Vless.AutoSelectBestServer = true;
        return settings;
    }

    private static VlessServerEntry Row(string name, string host, string transport = "") => new()
    {
        Name = name,
        Server = host,
        Port = 443,
        Security = "none",
        Uuid = "00000000-0000-4000-8000-000000000001",
        Transport = new() { Type = transport },
    };
}
