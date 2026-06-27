using System;
using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// T2 (2026-06-27): Hysteria2 Brutal calibration. VPNRouter used to emit no up/down on
/// the HY2 outbound -> sing-box ran BBR, which on a TSPU-throttled RU path can't mask the
/// access-leg loss that times Roblox out (277). A calibrated up/down (parsed from the
/// hysteria2 URI ?up=&down=) engages Brutal. Unset (0) keeps BBR (backward-compatible).
/// Value must be ~70-80% of measured goodput; see plans/roblox-tester-vps-spec-2026-06-27.md.
/// </summary>
public sealed class HysteriaBrutalCalibrationTests
{
    [Fact]
    public void Parse_Hy2Uri_WithUpDown_SetsBrutalBandwidth()
    {
        var e = ServerUriParser.Parse(
            "hysteria2://pw@1.2.3.4:8443/?obfs=salamander&obfs-password=x&up=50&down=100#HY2");
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal(50, e.HysteriaUpMbps);
        Assert.Equal(100, e.HysteriaDownMbps);
    }

    [Fact]
    public void Parse_Hy2Uri_WithoutUpDown_LeavesUnset_BBR()
    {
        var e = ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8443/?obfs=salamander#HY2");
        Assert.Equal(0, e.HysteriaUpMbps);
        Assert.Equal(0, e.HysteriaDownMbps);
    }

    [Fact]
    public void Parse_Hy2Uri_MbpsSuffix_Tolerated()
    {
        var e = ServerUriParser.Parse("hysteria2://pw@1.2.3.4:8443/?up=45mbps&down=90mbps#HY2");
        Assert.Equal(45, e.HysteriaUpMbps);
        Assert.Equal(90, e.HysteriaDownMbps);
    }

    [Fact]
    public void Generate_Hy2Server_WithCalibration_EmitsUpDownMbps()
    {
        var cfg = ConfigGenerator.Generate(MakeProfile(), Array.Empty<string>(), MakeHy2Settings(up: 50, down: 100));
        var hy2 = cfg.Outbounds.First(o => o.Type == "hysteria2");
        Assert.Equal(50, hy2.UpMbps);
        Assert.Equal(100, hy2.DownMbps);
    }

    [Fact]
    public void Generate_Hy2Server_WithoutCalibration_OmitsUpDown_BBR()
    {
        var cfg = ConfigGenerator.Generate(MakeProfile(), Array.Empty<string>(), MakeHy2Settings(up: 0, down: 0));
        var hy2 = cfg.Outbounds.First(o => o.Type == "hysteria2");
        Assert.Null(hy2.UpMbps);   // omitted -> sing-box BBR
        Assert.Null(hy2.DownMbps);
    }

    private static Profile MakeProfile() => new() { Name = "t", DnsMode = "vpn_only" };

    private static AppSettings MakeHy2Settings(int up, int down) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = "hy2",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "hy2", Protocol = "hysteria2", Server = "1.2.3.4", Port = 8443,
                    Password = "pw", ObfsType = "salamander", ObfsPassword = "x",
                    HysteriaUpMbps = up, HysteriaDownMbps = down,
                    Tls = new VlessTlsConfig { Enabled = true, ServerName = "1.2.3.4" },
                },
            },
        },
    };
}
