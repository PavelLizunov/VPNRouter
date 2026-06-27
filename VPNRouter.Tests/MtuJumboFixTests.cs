using System;
using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// 2026-06-27: a jumbo TUN MTU (9000, the pre-v2.42 default) could get STUCK in a
/// persisted config — the 9000->1280 fix only ran on the v5->v6 step, so a config
/// that already passed v5->v6 on an older build never re-ran it, and Codex's
/// Migrate_6_to_7 only caught 1500. Diag 20260627-203104 caught a tester on schema
/// v6 + mtu 9000, PMTUD-blackholing DoH/joins -> Roblox Error 277, while users on
/// the 1280 default were fine. Two layers now prevent it: ConfigGenerator clamps
/// jumbo at GENERATION time (bulletproof, independent of migration state), and
/// Migrate_6_to_7 rewrites the stuck 9000 in persistence. See
/// plans/roblox-277-rca-2026-06-27.md.
/// </summary>
public sealed class MtuJumboFixTests
{
    [Theory]
    [InlineData(9000, 1280)]   // stuck pre-v2.42 jumbo default -> clamped
    [InlineData(4000, 1280)]   // any > 1500 -> clamped
    [InlineData(0, 1280)]      // invalid -> fallback
    [InlineData(-1, 1280)]     // invalid -> fallback
    [InlineData(1500, 1500)]   // legacy default passes the clamp (migration lowers it)
    [InlineData(1280, 1280)]   // current default unchanged
    [InlineData(1400, 1400)]   // deliberate custom preserved
    public void NormalizeTunMtu_ClampsJumboOnly(int input, int expected)
        => Assert.Equal(expected, ConfigGenerator.NormalizeTunMtu(input));

    [Fact]
    public void Generate_WithStuck9000_NeverEmitsJumboTunMtu()
    {
        var settings = MakeMinimalSettings(mtu: 9000);
        var config = ConfigGenerator.Generate(MakeProfile(), Array.Empty<string>(), settings);
        var tun = config.Inbounds.Find(i => i.Type == "tun");
        Assert.NotNull(tun);
        Assert.Equal(1280, tun!.Mtu); // generation-time clamp, regardless of persisted value
    }

    [Theory]
    [InlineData(9000, 1280)]   // stuck jumbo rewritten (the tester's exact case)
    [InlineData(1500, 1280)]   // legacy default rewritten
    [InlineData(1400, 1400)]   // deliberate custom preserved
    [InlineData(1280, 1280)]   // already safe, untouched
    public void Migrate_6_to_7_LowersLegacyAndStuckMtu(int input, int expected)
    {
        var s = new AppSettings { Tun = new TunSettings { Mtu = input } };
        var migrated = SettingsMigrator.Migrate(s, 6, 7);
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
