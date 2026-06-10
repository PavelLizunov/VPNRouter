using System.Collections.Generic;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.41.x — config generation for a dns-tunnel (slipstream) server. The VLESS
/// outbound must target the local slipstream front (127.0.0.1:DefaultLocalPort)
/// with the uuid set and NO TLS / Reality / flow — the tunnel does its own
/// QUIC-TLS. See plans/dns-tunnel-slipstream-integration-2026-06-10.md.
/// </summary>
public class ConfigGeneratorDnsTunnelTests
{
    private const string Uuid = "11111111-1111-1111-1111-111111111111";

    private static Profile DiscordProfile() => new()
    {
        Name = "T",
        DnsMode = "vpn_only",
        Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
    };

    private static AppSettings DnsTunnelSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            ConfigMode = "subscribe",
            ActiveSubscriptionServer = "Emergency",
            Subscriptions = new List<SubscriptionEntry>
            {
                new()
                {
                    Name = "dt-sub",
                    Url = "https://example.com",
                    Enabled = true,
                    Servers = new List<VlessServerEntry>
                    {
                        new()
                        {
                            Protocol = "dns-tunnel",
                            Name = "Emergency",
                            Server = "tunnel.example.org",
                            DnsDomain = "tunnel.example.org",
                            DnsResolvers = new List<string> { "195.208.4.1:53", "195.208.5.1:53" },
                            DnsLeafCertPem = "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----",
                            Uuid = Uuid,
                        }
                    }
                }
            }
        },
        Tun = new TunSettings { InterfaceName = "VPNRouter-TUN", Ipv4Address = "172.19.0.1/30", Mtu = 9000, AutoRoute = true, StrictRoute = false },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query", Strategy = "ipv4_only" },
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
        Vless = new VlessConfig()
    };

    [Fact]
    public void Generate_DnsTunnelServer_ProxyTargetsLocalSlipstreamPort_NoTls()
    {
        var settings = DnsTunnelSettings();
        Assert.Single(VlessServersResolver.Resolve(settings)); // subscribe → aggregate into Vless.Servers
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("vless", proxy!.Type);
        Assert.Equal("127.0.0.1", proxy.Server);                       // local slipstream front
        Assert.Equal(SlipstreamManager.DefaultLocalPort, proxy.ServerPort);
        Assert.Equal(Uuid, proxy.Uuid);                                 // reused VLESS uuid
        Assert.Null(proxy.Tls);                                         // no TLS — tunnel does QUIC-TLS
        Assert.True(string.IsNullOrEmpty(proxy.Flow));                  // no xtls flow
    }
}
