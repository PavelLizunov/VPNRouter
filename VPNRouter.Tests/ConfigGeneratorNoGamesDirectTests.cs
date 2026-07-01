#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorNoGamesDirectTests
{
    [Fact]
    public void FullTunnel_WithUdpProxy_DoesNotEmitRobloxDirectRule()
    {
        var config = ConfigGenerator.Generate(
            new Profile { Name = "Games", DnsMode = "vpn_only" },
            Array.Empty<string>(),
            CreateFullTunnelUdpSplitSettings());

        Assert.Contains(config.Outbounds, o => o.Tag == "proxy-udp");
        Assert.DoesNotContain(config.Route.Rules, IsRobloxDirectRule);
        Assert.Contains(config.Route.Rules, r =>
            string.Equals(r.Network, "udp", StringComparison.OrdinalIgnoreCase)
            && r.Action == "route"
            && r.Outbound == "proxy-udp");
    }

    private static bool IsRobloxDirectRule(RouteRule rule) =>
        rule.Action == "route"
        && rule.Outbound == "direct"
        && rule.ProcessName?.Contains("RobloxPlayerBeta.exe") == true;

    private static AppSettings CreateFullTunnelUdpSplitSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            RoutingMode = "full",
            BlockQuicOnTcpProxy = true,
        },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings(),
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
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = "testkey",
                        ShortId = "abcd",
                    },
                },
                new()
                {
                    Name = "main-hy2",
                    Protocol = "hysteria2",
                    Server = "game.example.com",
                    Port = 8444,
                    Password = "hy2-password",
                    Tls = new VlessTlsConfig
                    {
                        Enabled = true,
                        ServerName = "game.example.com",
                    },
                },
            },
        },
    };
}
