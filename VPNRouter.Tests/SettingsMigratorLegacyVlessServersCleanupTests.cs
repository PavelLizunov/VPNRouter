using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// F-B (2026-05-11): pin the legacy <c>vless.servers</c> cleanup pass
/// that the v2→v3 schema migration runs on first load. Closes the
/// stas-class shadow-override bug at the settings-load layer.
///
/// <para>Source of the bug: stas had pasted a placeholder VLESS URI in
/// the old direct-VLESS mode (pre-subscription era). When he later added
/// a subscription, the placeholder entries lived on in
/// <see cref="VlessConfig.Servers"/> AND <see cref="VlessConfig.ActiveServer"/>
/// still pointed at one of them. The resolver always preferred
/// <c>vless.servers</c> over <c>subscriptions[*].servers</c>, so the
/// generated sing-box outbound used the dead placeholder IP — silent
/// leak with no UI signal. See
/// <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §1 Fix-B.</para>
///
/// <para>F-A and F-D handle the resolver / validator layers; this file
/// pins the migrator layer.</para>
/// </summary>
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
        // Direct VLESS mode (no subs). Legacy users in pre-subscription
        // era are the only "source of truth" for vless.servers — never
        // touch them.
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

        // No ENABLED subscription with servers → legacy entries are
        // still authoritative.
        Assert.Single(s.Vless.Servers);
    }

    [Fact]
    public void Cleanup_StasFixture_RemovesOrphans_PreservesActiveServer_BR4()
    {
        // BR-4 refinement (brat 2026-05-19): the orphan cleanup now
        // distinguishes "stale auto-migrated duplicate" from "user-added
        // manual entry the user selected as active". An entry referenced
        // by vless.active_server represents user intent — keep it, even
        // when it doesn't match a subscription server.
        //
        // In the original stas scenario, the orphan placeholder credentials
        // are now caught by SettingsMigrator.PruneKnownPlaceholders (the
        // dedicated placeholder cleanup pass run AFTER migration). That
        // catches stas-class entries by Reality pubkey fingerprint instead
        // of by name-mismatch — more precise + doesn't over-strip the
        // brat-class manual fallback.
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

        // BR-4: active entry preserved, other orphan stripped.
        Assert.Single(s.Vless.Servers);
        Assert.Equal("khunrath_ln", s.Vless.Servers[0].Name);
        Assert.Equal("khunrath_ln", s.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_KeepsEntriesThatMatchSubscriptionByCompositeKey()
    {
        // If by coincidence the user previously pasted an entry that's
        // ALSO in their subscription (same name/server/port/uuid), keep
        // it. The cleanup is conservative — only orphans go.
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
        // BR-4 update: active server preserved (orphan-1), other orphans
        // stripped. Idempotency still holds — second apply preserves the
        // same single active entry.
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

        // BR-4: orphan-1 kept (active), orphan-2 stripped → 1 entry.
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
        // BR-4: end-to-end migrator step preserves the active orphan
        // (user intent signal). Only non-active orphans are stripped.
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
        // BR-4: user-manual kept, stale-orphan stripped.
        Assert.Single(migrated.Vless.Servers);
        Assert.Equal("user-manual", migrated.Vless.Servers[0].Name);
        Assert.Equal("user-manual", migrated.Vless.ActiveServer);
    }

    [Fact]
    public void Cleanup_HandlesEmptyVlessServersGracefully()
    {
        // Defensive: cleanup must be a noop when vless.servers is empty,
        // even with an enabled subscription.
        var s = new AppSettings();
        s.App.Subscriptions.Add(new SubscriptionEntry
        {
            Name = "sub",
            Enabled = true,
            Servers = new List<VlessServerEntry> { MakeServer("only", "1.1.1.1") },
        });

        // Should not throw, should not crash.
        SettingsMigrator.CleanupOrphanVlessServers(s);

        Assert.Empty(s.Vless.Servers);
    }
}
