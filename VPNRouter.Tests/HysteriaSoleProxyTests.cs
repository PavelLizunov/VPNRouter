#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// diag 20260702-183129: a tester selected the "Germany HY2" (hysteria2) server but
/// the browser was terrible — 696x 5s i/o-timeout on <c>outbound/vless[proxy]</c>.
/// Root cause: the flow-based TCP/UDP split in <see cref="ConfigGenerator"/>
/// BuildOutbounds handed ALL TCP to a same-host VLESS-Reality sibling and only UDP to
/// Hy2, so browser TCP rode VLESS-over-TCP — throttled by RU TSPU, the very thing Hy2
/// avoids. Fix: when the user EXPLICITLY selects a Hy2/TUIC server (UDP-native,
/// carries TCP+UDP over one QUIC transport) it becomes the SOLE "proxy". A
/// VLESS-selected server still gets the deliberate TCP-vless / UDP-hy2 split.
/// </summary>
public sealed class HysteriaSoleProxyTests
{
    [Fact]
    public void Hy2Selected_ProxyIsHysteria2_NoUdpSplit()
    {
        var config = Generate("main-hy2");

        var proxy = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("hysteria2", proxy.Type);
        // Hy2 carries UDP natively — no separate proxy-udp sibling, no VLESS on TCP.
        Assert.DoesNotContain(config.Outbounds, o => o.Tag == "proxy-udp");
        Assert.DoesNotContain(config.Outbounds, o => o.Type == "vless");
    }

    [Fact]
    public void VlessSelected_KeepsTcpVlessUdpHy2Split()
    {
        // Regression guard: selecting the VLESS-Reality server keeps the deliberate
        // TCP->vless / UDP->hy2 split (good for games/voice on a working VLESS).
        var config = Generate("main-vless");

        var proxy = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        var udp = Assert.Single(config.Outbounds, o => o.Tag == "proxy-udp");
        Assert.Equal("hysteria2", udp.Type);
    }

    [Fact]
    public void Hy2Selected_QuicNotRejected()
    {
        // Hy2 carries QUIC over real UDP; a quic-reject rule would needlessly force
        // HTTP/3 apps back to TCP (through the same Hy2 tunnel). Must not appear even
        // though BlockQuicOnTcpProxy=true and there is no separate proxy-udp outbound.
        var config = Generate("main-hy2");

        Assert.DoesNotContain(config.Route.Rules, r =>
            string.Equals(r.Protocol, "quic", System.StringComparison.OrdinalIgnoreCase)
            && r.Action == "reject");
    }

    private static SingBoxConfig Generate(string activeServer) =>
        ConfigGenerator.Generate(
            new Profile { Name = "P", DnsMode = "vpn_only" },
            System.Array.Empty<string>(),
            Settings(activeServer));

    private static AppSettings Settings(string activeServer) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full", BlockQuicOnTcpProxy = true },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
        Vless = new VlessConfig
        {
            ActiveServer = activeServer,
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main-vless",
                    Protocol = "vless",
                    Server = "germany.example.com",
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "testkey", ShortId = "abcd" },
                },
                new()
                {
                    Name = "main-hy2",
                    Protocol = "hysteria2",
                    Server = "germany.example.com",
                    Port = 8444,
                    Password = "hy2-password",
                    Tls = new VlessTlsConfig { Enabled = true, ServerName = "germany.example.com" },
                },
            },
        },
    };
}
