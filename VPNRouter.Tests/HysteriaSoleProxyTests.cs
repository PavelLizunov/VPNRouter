#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HysteriaSoleProxyTests
{
    [Fact]
    public void Hy2Selected_ProxyIsHysteria2_NoUdpSplit()
    {
        var config = Generate("main-hy2");

        var proxy = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("hysteria2", proxy.Type);
        Assert.DoesNotContain(config.Outbounds, o => o.Tag == "proxy-udp");
        Assert.DoesNotContain(config.Outbounds, o => o.Type == "vless");
    }

    [Fact]
    public void Hy2Selected_QuicNotRejected()
    {
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
