using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Localization;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AutoFailoverRecoveryAndPersistTests
{
    private static AppSettings SubscribeWith(params string[] serverNames)
    {
        var s = new AppSettings();
        s.App.ConfigMode = "subscribe";
        s.Vless.ActiveServer = serverNames[0];
        s.App.ActiveSubscriptionServer = serverNames[0];
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = serverNames.Select((n, i) => new VlessServerEntry
            {
                Name = n,
                Server = $"1.2.3.{i + 1}",
                Port = 443,
                Uuid = $"uuid-{i + 1}",
            }).ToList(),
        });
        return s;
    }

    [Fact]
    public async Task ResetCycle_AfterMaxAttemptsExhausted_RestoresFullPool()
    {
        var settings = SubscribeWith("srv-1", "srv-2", "srv-3", "srv-4", "srv-5");
        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(), restart: null, store: new InMemorySettingsStore());

        for (int i = 0; i < AutoFailoverEngine.MaxAttempts; i++)
        {
            var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
            Assert.True(o.Switched, $"switch #{i + 1} should succeed");
        }

        var capped = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
        Assert.False(capped.Switched);
        Assert.Contains(capped.UserFacingMessage, new[]
        {
            Strings.FailoverAllServersDown(engine.TriedServers.Count, "ru"),
            Strings.FailoverAllServersDown(engine.TriedServers.Count, "en"),
        });

        engine.ResetCycle();
        Assert.Empty(engine.TriedServers);

        var recovered = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
        Assert.True(recovered.Switched, "failover should recover after ResetCycle");
    }

    [Theory]
    [InlineData("accept", false)]
    [InlineData("accept", true)]
    [InlineData("refuse", false)]
    [InlineData("cancel", false)]
    public async Task CrossFeedFailoverCommitsTheWholeChosenRowOrRestoresThePreviousOne(string result, bool unnamed)
    {
        var settings = SubscribeWith("srv-a", "srv-b");
        var first = settings.App.Subscriptions.Single();
        var oldRow = first.Servers[0];
        var replacement = first.Servers[1];
        if (unnamed) replacement.Name = string.Empty;
        first.Servers.RemoveAt(1);
        var second = new SubscriptionEntry { Url = "https://other.invalid/sub", Servers = [replacement] };
        settings.App.Subscriptions.Add(second);
        settings.App.ActiveSubscriptionId = first.Id;
        settings.App.ActiveSubscriptionRow = SubscriptionEntry.RowKey(oldRow);
        var store = new InMemorySettingsStore();
        store.Save(SettingsLoader.Copy(settings));
        AppSettings? duringRestart = null;
        var engine = new AutoFailoverEngine(settings, new ConfigSanityCheck(), _ =>
        {
            duringRestart = SettingsLoader.Copy(settings);
            if (result == "cancel") throw new System.OperationCanceledException();
            return Task.FromResult(result == "accept");
        }, store: store);
        if (result == "cancel")
            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() => engine.HandleDeadConfigAsync("fixture"));
        else
            Assert.Equal(result == "accept", (await engine.HandleDeadConfigAsync("fixture")).Switched);
        Assert.Equal(second.Id, duringRestart?.App.ActiveSubscriptionId);
        Assert.Equal(SubscriptionEntry.RowKey(replacement), duringRestart?.App.ActiveSubscriptionRow);

        var expectedFeed = result == "accept" ? second : first;
        var expectedRow = expectedFeed.Servers.Single();
        foreach (var reopened in new[] { SettingsLoader.Copy(store.Load()), SettingsLoader.Copy(settings) })
        {
            Assert.Equal(expectedFeed.Id, reopened.App.ActiveSubscriptionId);
            Assert.Equal(SubscriptionEntry.RowKey(expectedRow), reopened.App.ActiveSubscriptionRow);
            Assert.Equal(expectedRow.Name, reopened.App.ActiveSubscriptionServer);
            VlessServersResolver.Resolve(reopened);
            Assert.Equal(expectedRow.Server, reopened.Vless.GetActiveServers()[0].Server);
        }
    }

    [Fact]
    public async Task Persist_DoesNotLeakResolverAggregateIntoVlessServers()
    {
        var store = new InMemorySettingsStore();
        var settings = SubscribeWith("srv-1", "srv-2");
        settings.Vless.Servers = new List<VlessServerEntry>
        {
            new() { Name = "srv-1", Server = "1.2.3.1", Port = 443, Uuid = "uuid-1" },
            new() { Name = "srv-2", Server = "1.2.3.2", Port = 443, Uuid = "uuid-2" },
        };

        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(), restart: null, store: store);

        var outcome = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);

        Assert.True(outcome.Switched);
        Assert.Equal("srv-2", outcome.NewActiveServer);

        Assert.NotNull(store.LastSave);
        Assert.Empty(store.LastSave!.Value.Settings.Vless.Servers);
        Assert.Equal("srv-2", store.LastSave!.Value.Settings.Vless.ActiveServer);
        Assert.Equal("srv-2", store.LastSave!.Value.Settings.App.ActiveSubscriptionServer);

        Assert.Equal(2, settings.Vless.Servers.Count);
    }
}
