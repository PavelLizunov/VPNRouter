using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P1.5 AutoFailover User-Intent Guard (audit handoff): a failover swap is
/// committed/persisted ONLY when the replacement start is confirmed. When the
/// restart delegate returns false — the shape <c>ExecuteProbeFailoverRestartAsync</c>
/// takes after a user Disconnect cancels the session mid-failover — the engine must
/// roll the in-memory selection back, persist NOTHING, and surface NO message, so
/// the next Connect uses the server the user last intentionally chose.
/// </summary>
public sealed class AutoFailoverUserIntentGuardTests
{
    private static AppSettings SubscribeWith(params string[] names)
    {
        var s = new AppSettings();
        s.App.ConfigMode = "subscribe";
        s.Vless.ActiveServer = names[0];
        s.App.ActiveSubscriptionServer = names[0];
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "main",
            Url = "https://example.com/sub",
            Enabled = true,
            Servers = names.Select((n, i) => new VlessServerEntry
            {
                Name = n,
                Server = $"1.2.3.{i + 1}",
                Port = 443,
                Uuid = $"uuid-{i + 1}",
            }).ToList(),
        });
        return s;
    }

    // Test 1 + 2 — restart returns false (user Disconnect / failed bring-up):
    // no persist, in-memory reverted, no user-facing message.
    [Fact]
    public async Task RestartReturnsFalse_DoesNotPersist_RevertsSelection_StaysQuiet()
    {
        var settings = SubscribeWith("srv-1", "srv-2");
        var store = new InMemorySettingsStore();
        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(),
            restart: _ => Task.FromResult(false),   // ExecuteProbeFailoverRestartAsync after a user Disconnect
            store: store);

        var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);

        Assert.False(o.Switched);
        Assert.Null(o.NewActiveServer);
        Assert.Null(o.UserFacingMessage);                              // no failover announcement post-disconnect
        Assert.Equal("srv-1", settings.Vless.ActiveServer);            // in-memory reverted to the user's choice
        Assert.Equal("srv-1", settings.App.ActiveSubscriptionServer);
        Assert.Equal(0, store.SaveCount);                             // nothing persisted to disk
    }

    // Test 3 — restart succeeds: the new server is persisted and the switch is committed.
    [Fact]
    public async Task RestartSucceeds_PersistsNewServer_Switched()
    {
        var settings = SubscribeWith("srv-1", "srv-2");
        var store = new InMemorySettingsStore();
        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(),
            restart: _ => Task.FromResult(true),
            store: store);

        var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);

        Assert.True(o.Switched);
        Assert.Equal("srv-2", o.NewActiveServer);
        Assert.Equal("srv-2", settings.Vless.ActiveServer);
        Assert.True(store.SaveCount >= 1);
        Assert.Equal("srv-2", store.LastSave!.Value.Settings.Vless.ActiveServer);   // committed B on disk
    }

    // Test 4 — null delegate (pre-start / caller-driven recovery) preserves the
    // legacy switch+persist behaviour.
    [Fact]
    public async Task NullDelegate_PreservesSwitchAndPersist()
    {
        var settings = SubscribeWith("srv-1", "srv-2");
        var store = new InMemorySettingsStore();
        var engine = new AutoFailoverEngine(settings, new ConfigSanityCheck(), restart: null, store: store);

        var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);

        Assert.True(o.Switched);
        Assert.Equal("srv-2", settings.Vless.ActiveServer);
        Assert.True(store.SaveCount >= 1);
    }

    // A thrown (non-cancellation) restart is also "not confirmed" — same guard.
    [Fact]
    public async Task RestartThrows_DoesNotPersist_Reverts()
    {
        var settings = SubscribeWith("srv-1", "srv-2");
        var store = new InMemorySettingsStore();
        var engine = new AutoFailoverEngine(
            settings, new ConfigSanityCheck(),
            restart: _ => throw new System.InvalidOperationException("bring-up blew up"),
            store: store);

        var o = await engine.HandleDeadConfigAsync("dead", CancellationToken.None);

        Assert.False(o.Switched);
        Assert.Equal("srv-1", settings.Vless.ActiveServer);
        Assert.Equal(0, store.SaveCount);
    }
}
