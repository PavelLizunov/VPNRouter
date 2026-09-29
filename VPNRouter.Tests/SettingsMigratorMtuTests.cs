using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class SettingsMigratorMtuTests
{
    [Fact]
    public void Migrate_5_to_6_LowersJumboMtu()
    {
        var s = new AppSettings { SchemaVersion = 5 };
        s.Tun.Mtu = 9000;

        var r = SettingsMigrator.Migrate(s, 5, 6);

        Assert.Equal(1280, r.Tun.Mtu);
        Assert.Equal(6, r.SchemaVersion);
    }

    [Fact]
    public void Migrate_5_to_6_KeepsCustomMtu()
    {
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

    [Fact]
    public void Migrate_6_to_7_LowersLegacy1500Mtu()
    {
        var s = new AppSettings { SchemaVersion = 6 };
        s.Tun.Mtu = 1500;

        var r = SettingsMigrator.Migrate(s, 6, 7);

        Assert.Equal(1280, r.Tun.Mtu);
        Assert.Equal(7, r.SchemaVersion);
    }

    [Fact]
    public void Migrate_6_to_7_KeepsCustomMtu()
    {
        var s = new AppSettings { SchemaVersion = 6 };
        s.Tun.Mtu = 1400;

        var r = SettingsMigrator.Migrate(s, 6, 7);

        Assert.Equal(1400, r.Tun.Mtu);
        Assert.Equal(7, r.SchemaVersion);
    }

    [Fact]
    public void Migrate_5_to_7_LowersLegacy1500Mtu()
    {
        var s = new AppSettings { SchemaVersion = 5 };
        s.Tun.Mtu = 1500;

        var r = SettingsMigrator.Migrate(s, 5, 7);

        Assert.Equal(1280, r.Tun.Mtu);
        Assert.Equal(7, r.SchemaVersion);
    }

    [Theory]
    [InlineData(1280, 1280)]
    [InlineData(1500, 1420)]
    [InlineData(0, 1420)]
    [InlineData(-1, 1420)]
    [InlineData(9000, 1420)]
    [InlineData(1332, 1332)]
    [InlineData(1380, 1380)]
    [InlineData(1400, 1400)]
    [InlineData(1499, 1499)]
    [InlineData(1200, 1200)]
    public void Migrate_7_to_8_RewritesDefaultsAndInvalidOnly(int input, int expected)
    {
        var s = new AppSettings { SchemaVersion = 7 };
        s.Tun.Mtu = input;

        var r = SettingsMigrator.Migrate(s, 7, 8);

        Assert.Equal(expected, r.Tun.Mtu);
        Assert.Equal(8, r.SchemaVersion);
    }
}
