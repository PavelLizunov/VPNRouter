using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class SettingsMigratorLegacyVlessServersCleanupTests
{
    private static VlessServerEntry MakeServer(string name, string ip, int port = 443, string uuid = "uuid-x")
        => new()
        {
            Name = name,
            Server = ip,
            Port = port,
            Uuid = uuid,
            Security = "reality",
            Reality = new VlessRealityConfig
            {
                Enabled = true,
                PublicKey = "pk-" + name,
                ShortId = "sid-" + name,
                ServerName = name + ".example",
            },
        };

    [Fact]
    public void Cleanup_NoSubscriptions_LeavesVlessServersIntact()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(MakeServer("manual-1", "1.2.3.4"));
        s.Vless.Servers.Add(MakeServer("manual-2", "5.6.7.8"));
        s.Vless.ActiveServer = "manual-2";

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Equal(2, s.Vless.Servers.Count);
        Assert.Equal("manual-2", s.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_SubscriptionsButAllDisabled_LeavesVlessServersIntact()
    {
        var s = new AppSettings();
        s.Vless.Servers.Add(MakeServer("manual-1", "1.2.3.4"));
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub1",
            Enabled = false,
            Servers = new List<VlessServerEntry> { MakeServer("sub-srv", "9.9.9.9") },
        });

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Single(s.Vless.Servers);
    }

    [Fact]
    public void Cleanup_StasFixture_RemovesOrphans_PreservesActiveServer_BR4()
    {
        var s = new AppSettings();

        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "simple",
            Enabled = true,
            Servers = new List<VlessServerEntry>
            {
                MakeServer("de-01 443 Khunrath", "104.194.156.93"),
                MakeServer("is-01 443 Khunrath", "93.95.226.167"),
                MakeServer("nk-01 8443 Khunrath", "194.87.222.111", port: 8443),
            },
        });

        s.Vless.Servers.Add(MakeServer("khunrath_ln", "195.135.255.216"));
        s.Vless.Servers.Add(MakeServer("is-01-grpc-test", "93.95.226.167", port: 8444));
        s.Vless.ActiveServer = "khunrath_ln";

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Single(s.Vless.Servers);
        Assert.Equal("khunrath_ln", s.Vless.Servers[0].Name);
        Assert.Equal("khunrath_ln", s.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_KeepsEntriesThatMatchSubscriptionByCompositeKey()
    {
        var sharedServer = MakeServer("paris-01", "100.64.0.1");
        var s = new AppSettings();

        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "premium",
            Enabled = true,
            Servers = new List<VlessServerEntry> { sharedServer },
        });

        s.Vless.Servers.Add(sharedServer);
        s.Vless.Servers.Add(MakeServer("orphan", "1.2.3.4"));
        s.Vless.ActiveServer = "paris-01";

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Single(s.Vless.Servers);
        Assert.Equal("paris-01", s.Vless.Servers[0].Name);
        Assert.Equal("paris-01", s.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_IsIdempotent_DoubleApplyIsSameAsSingle()
    {
        var s = new AppSettings();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry> { MakeServer("only", "1.1.1.1") },
        });
        s.Vless.Servers.Add(MakeServer("orphan-1", "8.8.8.8"));
        s.Vless.Servers.Add(MakeServer("orphan-2", "9.9.9.9"));
        s.Vless.ActiveServer = "orphan-1";

        SettingsMigrator.CleanupOrphanVlessServers(s);
        var afterFirst = s.Vless.Servers.Count;
        SettingsMigrator.CleanupOrphanVlessServers(s);
        var afterSecond = s.Vless.Servers.Count;

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, afterSecond);
        Assert.Equal("orphan-1", s.Vless.Servers[0].Name);
    }

    [Fact]
    public void Cleanup_ActiveServerSurvives_WhenItPointsToKeptEntry()
    {
        var s = new AppSettings();
        var keep = MakeServer("paris-01", "100.64.0.1");
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry> { keep },
        });
        s.Vless.Servers.Add(keep);
        s.Vless.Servers.Add(MakeServer("orphan", "8.8.8.8"));
        s.Vless.ActiveServer = "paris-01";

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Equal("paris-01", s.Vless.ActiveServer);
    }

    [Fact]
    public void Migrate_FromV2_PerformsCleanup_PreservesActive_AdvancesToV3()
    {
        var s = new AppSettings { SchemaVersion = 2 };
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry> { MakeServer("alive", "1.1.1.1") },
        });
        s.Vless.Servers.Add(MakeServer("user-manual", "8.8.8.8"));
        s.Vless.Servers.Add(MakeServer("stale-orphan", "9.9.9.9"));
        s.Vless.ActiveServer = "user-manual";

        var migrated = SettingsMigrator.Migrate(s, from: 2, to: 3);

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.Single(migrated.Vless.Servers);
        Assert.Equal("user-manual", migrated.Vless.Servers[0].Name);
        Assert.Equal("user-manual", migrated.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_HandlesEmptyVlessServersGracefully()
    {
        var s = new AppSettings();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry> { MakeServer("only", "1.1.1.1") },
        });

        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Empty(s.Vless.Servers);
    }
}
