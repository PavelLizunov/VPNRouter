using System;
using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class MtuJumboFixTests
{
    [Theory]
    [InlineData(9000, 1420)]
    [InlineData(4000, 1420)]
    [InlineData(0, 1420)]
    [InlineData(-1, 1420)]
    [InlineData(575, 1420)]
    [InlineData(576, 576)]
    [InlineData(1501, 1420)]
    [InlineData(1500, 1500)]
    [InlineData(1420, 1420)]
    [InlineData(1400, 1400)]
    public void NormalizeTunMtu_ClampsOutsideContract(int input, int expected)
        => Assert.Equal(expected, ConfigGenerator.NormalizeTunMtu(input));

    [Fact]
    public void Generate_WithStuck9000_NeverEmitsJumboTunMtu()
    {
        var settings = MakeMinimalSettings(mtu: 9000);
        var config = ConfigGenerator.Generate(MakeProfile(), Array.Empty<string>(), settings);
        var tun = config.Inbounds.Find(i => i.Type == "tun");
        Assert.NotNull(tun);
        Assert.Equal(1420, tun!.Mtu);
    }

    [Theory]
    [InlineData(9000, 1280)]
    [InlineData(1500, 1280)]
    [InlineData(1400, 1400)]
    [InlineData(1280, 1280)]
    public void Migrate_6_to_7_LowersLegacyAndStuckMtu(int input, int expected)
    {
        var s = new AppSettings { Tun = new TunSettings { Mtu = input } };
        var migrated = SettingsMigrator.Migrate(s, 6, 7);
        Assert.Equal(expected, migrated.Tun.Mtu);
    }

    [Theory]
    [InlineData(9000, 1420)]
    [InlineData(1500, 1420)]
    [InlineData(1280, 1280)]
    [InlineData(1400, 1400)]
    public void Migrate_7_to_8_MovesLegacyDefaultsTo1420(int input, int expected)
    {
        var s = new AppSettings { Tun = new TunSettings { Mtu = input } };
        var migrated = SettingsMigrator.Migrate(s, 7, 8);
        Assert.Equal(expected, migrated.Tun.Mtu);
    }

    private static Profile MakeProfile() => new() { Name = "t", DnsMode = "vpn_only" };

    private static AppSettings MakeMinimalSettings(int mtu) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings { Mtu = mtu },
        Vless = new VlessConfig
        {
            ActiveServer = "main-vless",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main-vless",
                    Protocol = "vless",
                    Server = "game.example.com",
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "testkey", ShortId = "abcd" },
                },
            },
        },
    };
}
