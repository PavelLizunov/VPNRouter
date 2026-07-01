using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.3 (2026-05-17): pin the load-time placeholder-fingerprint sweep
/// that <see cref="SettingsMigrator.PruneKnownPlaceholders"/> performs on
/// every yaml load. The pass is aggressive — any entry tagged by
/// <see cref="PlaceholderDefense"/> as a known-bad fingerprint is removed
/// outright from the legacy scalar trio, manual server list, and every
/// subscription. False-positive bans are worse than false negatives here
/// (a banned valid server kills VPN), so the tests double-check that
/// only the known-bad fingerprints get wiped.
///
/// <para>Counterpart in production code:
/// <c>VPNRouter.Core.Services.SettingsMigrator.PruneKnownPlaceholders</c>.
/// Counterpart in the loader pipeline: <see cref="SettingsLoader.LoadCore"/>
/// post-migration step that stamps
/// <see cref="AppConfig.PlaceholderPruneCount"/> when count > 0.</para>
/// </summary>
public class SettingsMigratorPlaceholderTests
{
    // Canonical placeholder fingerprints — same constants as
    // ConfigSanityCheck.KnownPlaceholder*. Re-declared as local
    // string literals so test failures point at the wrong-value with
    // grep-friendly context rather than a "set membership" abstraction.
    private const string BadPubkey  = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string BadShortId = "78ca7952";
    private const string BadServer  = "195.135.255.216";

    private static VlessServerEntry MakeClean(string name, string ip = "10.0.0.1", int port = 443)
        => new()
        {
            Name = name,
            Server = ip,
            Port = port,
            Uuid = "uuid-" + name,
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                PublicKey = "clean-pk-" + name,
                ShortId = "clean-sid",
                ServerName = name + ".example",
            },
        };

    private static VlessServerEntry MakePlaceholder(string name)
        => new()
        {
            Name = name,
            Server = BadServer,
            Port = 443,
            Uuid = "uuid-x",
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                PublicKey = BadPubkey,
                ShortId = BadShortId,
                ServerName = "yahoo.com",
            },
        };

    [Fact]
    public void PruneKnownPlaceholders_ScalarVlessFields_Wiped()
    {
        var s = new AppSettings();
        s.Vless.Server = "1.2.3.4";
        s.Vless.Port = 443;
        s.Vless.Uuid = "some-uuid";
        s.Vless.Reality = new VlessRealityConfig
        {
            Enabled = true,
            PublicKey = BadPubkey,    // ← placeholder hit
            ShortId = "clean-sid",
            ServerName = "yahoo.com",
        };

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(1, count);
        Assert.Equal(string.Empty, s.Vless.Server);
        Assert.Equal(0, s.Vless.Port);
        Assert.Equal(string.Empty, s.Vless.Uuid);
        Assert.Equal(string.Empty, s.Vless.Reality.PublicKey);
        Assert.Equal(string.Empty, s.Vless.Reality.ShortId);
    }

    [Fact]
    public void PruneKnownPlaceholders_VlessServersList_RemovesMatches()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(MakeClean("good-1"));
        s.Vless.Servers.Add(MakePlaceholder("bad-1"));
        s.Vless.Servers.Add(MakeClean("good-2"));

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(1, count);
        Assert.Equal(2, s.Vless.Servers.Count);
        Assert.DoesNotContain(s.Vless.Servers, e => e.Name == "bad-1");
        Assert.Contains(s.Vless.Servers, e => e.Name == "good-1");
        Assert.Contains(s.Vless.Servers, e => e.Name == "good-2");
    }

    [Fact]
    public void PruneKnownPlaceholders_SubscriptionServers_RemovesMatches()
    {
        var s = new AppSettings();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub-1",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                MakePlaceholder("ph-1"),
                MakeClean("ok-1", "11.0.0.1"),
                MakePlaceholder("ph-2"),
                MakeClean("ok-2", "12.0.0.2"),
            },
        });

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(2, count);
        var remaining = s.App.Subscriptions[0].Servers;
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.Name == "ph-1");
        Assert.DoesNotContain(remaining, e => e.Name == "ph-2");
        Assert.Contains(remaining, e => e.Name == "ok-1");
        Assert.Contains(remaining, e => e.Name == "ok-2");
    }

    [Fact]
    public void PruneKnownPlaceholders_ActiveServer_NulledIfRemoved()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(MakePlaceholder("bad-active"));
        s.Vless.Servers.Add(MakeClean("good-other"));
        s.Vless.ActiveServer = "bad-active";

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(1, count);
        Assert.True(string.IsNullOrEmpty(s.Vless.ActiveServer));
        // good-other survives — we don't auto-promote, but it's still in the list.
        Assert.Single(s.Vless.Servers);
        Assert.Equal("good-other", s.Vless.Servers[0].Name);
    }

    [Fact]
    public void PruneKnownPlaceholders_ActiveServer_PreservedIfStillExists()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(MakeClean("good-entry"));
        s.Vless.Servers.Add(MakePlaceholder("bad-entry"));
        s.Vless.ActiveServer = "good-entry";

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(1, count);
        Assert.Equal("good-entry", s.Vless.ActiveServer);
    }

    [Fact]
    public void PruneKnownPlaceholders_AllClean_ReturnsZero()
    {
        var s = new AppSettings();
        s.Vless.Server = "1.2.3.4";
        s.Vless.Port = 443;
        s.Vless.Uuid = "clean-uuid";
        s.Vless.Reality = new VlessRealityConfig
        {
            Enabled = true,
            PublicKey = "totally-clean-pubkey-here",
            ShortId = "abcd1234",
            ServerName = "real.example.com",
        };
        s.Vless.Servers.Add(MakeClean("clean-1"));
        s.Vless.Servers.Add(MakeClean("clean-2", "5.6.7.8"));
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                MakeClean("sub-srv-1"),
                MakeClean("sub-srv-2", "9.9.9.9"),
            },
        });
        s.Vless.ActiveServer = "clean-1";

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        Assert.Equal(0, count);
        // Nothing got mutated.
        Assert.Equal("1.2.3.4", s.Vless.Server);
        Assert.Equal(443, s.Vless.Port);
        Assert.Equal("clean-uuid", s.Vless.Uuid);
        Assert.Equal("totally-clean-pubkey-here", s.Vless.Reality.PublicKey);
        Assert.Equal(2, s.Vless.Servers.Count);
        Assert.Equal(2, s.App.Subscriptions[0].Servers.Count);
        Assert.Equal("clean-1", s.Vless.ActiveServer);
    }

    [Fact]
    public void PruneKnownPlaceholders_MultipleSources_AccumulatesCount()
    {
        var s = new AppSettings();

        // (1) Scalar.
        s.Vless.Server = "anywhere";
        s.Vless.Reality = new VlessRealityConfig
        {
            Enabled = true,
            PublicKey = BadPubkey,    // hit
            ShortId = "clean",
            ServerName = "x",
        };

        // (2) Vless.Servers entry.
        s.Vless.Servers.Add(MakePlaceholder("manual-bad"));
        s.Vless.Servers.Add(MakeClean("manual-good"));

        // (3) Subscription server entry.
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                MakePlaceholder("sub-bad"),
                MakeClean("sub-good"),
            },
        });

        var count = SettingsMigrator.PruneKnownPlaceholders(s, null);

        // 1 scalar + 1 from Vless.Servers + 1 from subscription = 3.
        Assert.Equal(3, count);
        Assert.Equal(string.Empty, s.Vless.Server);
        Assert.Single(s.Vless.Servers);
        Assert.Equal("manual-good", s.Vless.Servers[0].Name);
        Assert.Single(s.App.Subscriptions[0].Servers);
        Assert.Equal("sub-good", s.App.Subscriptions[0].Servers[0].Name);
    }
}
