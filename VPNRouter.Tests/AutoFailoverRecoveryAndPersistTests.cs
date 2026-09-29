using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        Assert.Contains("Все серверы недоступны", capped.UserFacingMessage);

        engine.ResetCycle();
        Assert.Empty(engine.TriedServers);

        var recovered = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
        Assert.True(recovered.Switched, "failover should recover after ResetCycle");
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
