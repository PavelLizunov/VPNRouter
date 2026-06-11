// v2.42.0-r3 — TUN MTU 9000 -> 1280 fix + migration.
//
// The old 9000 jumbo TUN MTU (stack=system) put oversized HTTP/2 segments on the
// wire that the real 1500-MTU path couldn't carry; with PMTUD broken they were
// RST, so browsers got ERR_CONNECTION_CLOSED on YouTube / Google over TCP-only
// (VLESS) proxies (small clients + UDP/QUIC proxies were unaffected). Confirmed
// via diagnose.ps1 on a real user (h2 FAIL + tun mtu 9000). New default is 1280
// (IPv6 minimum, traverses any path); SettingsMigrator v5->v6 lowers existing
// 9000 configs while leaving deliberately-customised MTUs alone.

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class SettingsMigratorMtuTests
{
    [Fact]
    public void NewSettings_DefaultMtuIs1280()
    {
        Assert.Equal(1280, new AppSettings().Tun.Mtu);
    }

    [Fact]
    public void CurrentSchemaVersion_Is6()
    {
        Assert.Equal(6, AppSettings.CurrentSchemaVersion);
    }

    [Fact]
    public void Migrate_5_to_6_LowersJumboMtu()
    {
        // An existing config persisted with the old 9000 default.
        var s = new AppSettings { SchemaVersion = 5 };
        s.Tun.Mtu = 9000;

        var r = SettingsMigrator.Migrate(s, 5, 6);

        Assert.Equal(1280, r.Tun.Mtu);
        Assert.Equal(6, r.SchemaVersion);
    }

    [Fact]
    public void Migrate_5_to_6_KeepsCustomMtu()
    {
        // A user who deliberately set a non-default MTU must keep it.
        var s = new AppSettings { SchemaVersion = 5 };
        s.Tun.Mtu = 1450;

        var r = SettingsMigrator.Migrate(s, 5, 6);

        Assert.Equal(1450, r.Tun.Mtu);
    }

    [Fact]
    public void Migrate_5_to_6_IsIdempotentOnAlreadyLowMtu()
    {
        var s = new AppSettings { SchemaVersion = 5 };
        s.Tun.Mtu = 1280;

        var r = SettingsMigrator.Migrate(s, 5, 6);

        Assert.Equal(1280, r.Tun.Mtu);
    }
}
