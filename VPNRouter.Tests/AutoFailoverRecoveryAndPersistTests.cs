using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.44.3 P1 failover hardening:
/// (#3) ResetCycle must restore the full pool after a successful connect, so a
/// later mid-session failure can fail over again instead of staying permanently
/// "all dead" once MaxAttempts lifetime switches were hit (VpnEngine.OnConnected
/// now calls ResetCycle; this pins the engine-level recovery semantics it relies
/// on).
/// (#5) Persisting a failover swap must NOT serialize the resolver-aggregated
/// vless.servers into YAML (subscription-leak class).
/// </summary>
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

    // P1 #3: after MaxAttempts failovers the cycle is exhausted; a successful
    // connect (which VpnEngine.OnConnected signals via ResetCycle) restores the
    // pool so failover works again.
    [Fact]
    public async Task ResetCycle_AfterMaxAttemptsExhausted_RestoresFullPool()
    {
        var settings = SubscribeWith("srv-1", "srv-2", "srv-3", "srv-4", "srv-5");
        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(), restart: null, store: new InMemorySettingsStore());

        // MaxAttempts (3) successful switches.
        for (int i = 0; i < AutoFailoverEngine.MaxAttempts; i++)
        {
            var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
            Assert.True(o.Switched, $"switch #{i + 1} should succeed");
        }

        // Cap reached — next failover gives up with the "all dead" alert.
        var capped = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
        Assert.False(capped.Switched);
        Assert.Contains("Все серверы недоступны", capped.UserFacingMessage);

        // A successful connect resets the cycle (this is what OnConnected calls).
        engine.ResetCycle();
        Assert.Empty(engine.TriedServers);

        // Failover works again on the now-restored pool.
        var recovered = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);
        Assert.True(recovered.Switched, "failover should recover after ResetCycle");
    }

    // P1 #5: persisting the swap must not leak the in-memory resolver aggregate
    // (vless.servers) into the saved settings.
    [Fact]
    public async Task Persist_DoesNotLeakResolverAggregateIntoVlessServers()
    {
        var store = new InMemorySettingsStore();
        var settings = SubscribeWith("srv-1", "srv-2");
        // Simulate VlessServersResolver having aggregated the subscription into the
        // in-memory Vless.Servers (the leak source — empty on disk in subscribe mode).
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

        // The PERSISTED settings must NOT carry the aggregate in vless.servers...
        Assert.NotNull(store.LastSave);
        Assert.Empty(store.LastSave!.Value.Settings.Vless.Servers);
        // ...but the active-server selection IS persisted.
        Assert.Equal("srv-2", store.LastSave!.Value.Settings.Vless.ActiveServer);
        Assert.Equal("srv-2", store.LastSave!.Value.Settings.App.ActiveSubscriptionServer);

        // The in-memory settings keep the aggregate for THIS session's restart.
        Assert.Equal(2, settings.Vless.Servers.Count);
    }
}
