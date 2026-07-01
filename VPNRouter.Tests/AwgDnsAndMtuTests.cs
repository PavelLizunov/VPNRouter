#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.45.0-r8 (2026-07-01): over a UDP-native AmneziaWG tunnel the DoH TLS
/// handshake blackholes on the fixed 1280 WireGuard endpoint MTU — diag
/// 20260701-122336 showed 548 DNS exchanges >=5s (cold DoH handshakes up to 56s),
/// which made every Dota 2 region ping time out ("Задержка: ОШИБКА"). Two fixes:
/// (1) vpn-dns resolves via PLAIN UDP inside the encrypted tunnel instead of DoH;
/// (2) the TUN MTU is capped to the AWG endpoint MTU so oversized app packets
/// can't blackhole either. Both are scoped to the UDP-native case — a VLESS/Reality
/// TCP tunnel keeps DoH (TCP MSS auto-clamps) and its TUN MTU is left untouched.
/// </summary>
public sealed class AwgDnsAndMtuTests : IDisposable
{
    private readonly bool? _previousAwgOverride;

    public AwgDnsAndMtuTests()
    {
        _previousAwgOverride = SingBoxFeatures.OverrideAwg;
        SingBoxFeatures.OverrideAwg = true;
    }

    public void Dispose() => SingBoxFeatures.OverrideAwg = _previousAwgOverride;

    // ─── plain-UDP DNS (AmneziaWG) ────────────────────────────────────────────

    [Fact]
    public void Awg_BlockAdsOn_VpnDnsIsPlainUdpAdGuard()
    {
        var config = Generate(AwgSettings(blockAds: true));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("udp", vpnDns.Type);
        Assert.Equal("94.140.14.14", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
        Assert.Null(vpnDns.DomainResolver);   // literal IP -> no DoH-hostname bootstrap
        Assert.Null(vpnDns.Path);
        Assert.Null(vpnDns.ServerPort);       // default 53 omitted
    }

    [Fact]
    public void Awg_BlockAdsOff_VpnDnsUsesConfiguredIpLiteral()
    {
        var config = Generate(AwgSettings(blockAds: false, vpnDns: "https://1.1.1.1/dns-query"));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("udp", vpnDns.Type);
        Assert.Equal("1.1.1.1", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
    }

    [Fact]
    public void Awg_BlockAdsOff_HostnameVpnDnsFallsBackToCloudflare()
    {
        // A DoH *hostname* can't be a plain-UDP target (it would need resolving
        // over the very tunnel we're bootstrapping). Fall back to a literal IP.
        var config = Generate(AwgSettings(blockAds: false, vpnDns: "https://dns.google/dns-query"));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("udp", vpnDns.Type);
        Assert.Equal("1.1.1.1", vpnDns.Server);
    }

    [Fact]
    public void Vless_VpnDnsStaysDoH()
    {
        // The TCP tunnel keeps DoH — TCP MSS auto-clamps so the handshake survives,
        // and DoH hides the queries from the exit. Unchanged from r7.
        var config = Generate(VlessSettings(vpnDns: "https://dns.google/dns-query"));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpnDns.Type);
        Assert.Equal("dns.google", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
        Assert.NotNull(vpnDns.DomainResolver);   // DoH hostname still bootstraps
    }

    // ─── TUN MTU cap (AmneziaWG) ──────────────────────────────────────────────

    [Fact]
    public void Awg_TunMtuClampedToEndpointMtu()
    {
        // 1337 is what the live diag carried; it exceeds the 1280 AWG endpoint MTU.
        var config = Generate(AwgSettings(tunMtu: 1337));

        var tun = Assert.Single(config.Inbounds, i => i.Type == "tun");
        Assert.Equal(ConfigGenerator.AwgEndpointMtu, tun.Mtu);   // 1280
    }

    [Fact]
    public void Awg_TunMtuLowerThanEndpoint_Preserved()
    {
        // The clamp is a ceiling (Math.Min): a smaller user value survives.
        var config = Generate(AwgSettings(tunMtu: 1200));

        var tun = Assert.Single(config.Inbounds, i => i.Type == "tun");
        Assert.Equal(1200, tun.Mtu);
    }

    [Fact]
    public void Vless_TunMtuNotClamped()
    {
        // A TCP tunnel MSS-clamps adaptively, so the larger TUN MTU is left as-is.
        var config = Generate(VlessSettings(tunMtu: 1337));

        var tun = Assert.Single(config.Inbounds, i => i.Type == "tun");
        Assert.Equal(1337, tun.Mtu);
    }

    [Fact]
    public void Awg_EndpointMtuIsTheClampConstant()
    {
        var config = Generate(AwgSettings());
        var endpoint = Assert.Single(config.Endpoints!);
        Assert.Equal(ConfigGenerator.AwgEndpointMtu, endpoint.Mtu);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static SingBoxConfig Generate(AppSettings settings) =>
        ConfigGenerator.Generate(
            new Profile { Name = settings.Vless.ActiveServer!, DnsMode = "vpn_only" },
            Array.Empty<string>(),
            settings);

    private static AppSettings AwgSettings(bool blockAds = true, string vpnDns = "https://1.1.1.1/dns-query", int tunMtu = 1280) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full", BlockAds = blockAds },
        Dns = new DnsSettings { VpnDns = vpnDns },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings { Mtu = tunMtu },
        Vless = new VlessConfig
        {
            ActiveServer = "awg",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "awg",
                    Protocol = "amneziawg",
                    Server = "1.2.3.4",
                    Port = 51820,
                    Awg = new AwgConfig
                    {
                        PrivateKey = "XJRWW/WbfydGk7/7Kn3LLn+70XoT6se7SX9zUztOuKU=",
                        Address = new() { "10.13.13.2/32" },
                        PeerPublicKey = "iLtvwNI8UxIFHB9wNjyMud7/nofHJ5IBZaMC/knnWT0=",
                        Jc = 4, Jmin = 40, Jmax = 70, S1 = 86, S2 = 574, H1 = "1234567890",
                    },
                },
            },
        },
    };

    private static AppSettings VlessSettings(string vpnDns = "https://dns.google/dns-query", int tunMtu = 1280) => new()
    {
        App = new AppConfig { LogLevel = "info", RoutingMode = "full" },
        Dns = new DnsSettings { VpnDns = vpnDns },
        SingBox = new SingBoxSettings(),
        Tun = new TunSettings { Mtu = tunMtu },
        Vless = new VlessConfig
        {
            ActiveServer = "main-vless",
            Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main-vless",
                    Protocol = "vless",
                    Server = "vless.example.com",
                    Port = 443,
                    Uuid = "11111111-1111-1111-1111-111111111111",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                        ShortId = "d86e92a0c6dd2271",
                    },
                },
            },
        },
    };
}
