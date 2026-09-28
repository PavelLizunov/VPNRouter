#nullable enable

using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class AwgDnsAndMtuTests : IDisposable
{
    private readonly bool? _previousAwgOverride;

    public AwgDnsAndMtuTests()
    {
        _previousAwgOverride = SingBoxFeatures.OverrideAwg;
        SingBoxFeatures.OverrideAwg = true;
    }

    public void Dispose() => SingBoxFeatures.OverrideAwg = _previousAwgOverride;

    [Fact]
    public void Awg_BlockAdsOn_VpnDnsIsPlainUdpAdGuard()
    {
        var config = Generate(AwgSettings(blockAds: true));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("udp", vpnDns.Type);
        Assert.Equal("94.140.14.14", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
        Assert.Null(vpnDns.DomainResolver);
        Assert.Null(vpnDns.Path);
        Assert.Null(vpnDns.ServerPort);
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
    public void Awg_BlockAdsOff_HostnameVpnDnsFallsBackToGoogle()
    {
        var config = Generate(AwgSettings(blockAds: false, vpnDns: "https://dns.google/dns-query"));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("udp", vpnDns.Type);
        Assert.Equal("8.8.8.8", vpnDns.Server);
    }

    [Fact]
    public void Vless_VpnDnsStaysDoH()
    {
        var config = Generate(VlessSettings(vpnDns: "https://dns.google/dns-query"));

        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpnDns.Type);
        Assert.Equal("dns.google", vpnDns.Server);
        Assert.Equal("proxy", vpnDns.Detour);
        Assert.NotNull(vpnDns.DomainResolver);
    }

    [Fact]
    public void Awg_TunMtuClampedToEndpointMtu()
    {
        var config = Generate(AwgSettings(tunMtu: 1500));

        var tun = Assert.Single(config.Inbounds, i => i.Type == "tun");
        Assert.Equal(ConfigGenerator.AwgEndpointMtu, tun.Mtu);
    }

    [Fact]
    public void Awg_TunMtuPreservesLowerUserSetting()
    {
        var config = Generate(AwgSettings(tunMtu: 1200));

        var tun = Assert.Single(config.Inbounds, i => i.Type == "tun");
        Assert.Equal(1200, tun.Mtu);
    }

    [Fact]
    public void Vless_TunMtuNotClamped()
    {
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

    [Fact]
    public void Tun_EndpointIndependentNat_Enabled_TransportIndependent()
    {
        var vless = Assert.Single(Generate(VlessSettings()).Inbounds, i => i.Type == "tun");
        Assert.True(vless.EndpointIndependentNat);

        var awg = Assert.Single(Generate(AwgSettings()).Inbounds, i => i.Type == "tun");
        Assert.True(awg.EndpointIndependentNat);
    }

    private static SingBoxConfig Generate(AppSettings settings) =>
        ConfigGenerator.Generate(
            new Profile { Name = settings.Vless.ActiveServer!, DnsMode = "vpn_only" },
            Array.Empty<string>(),
            settings);

    private static AppSettings AwgSettings(bool blockAds = true, string vpnDns = "https://1.1.1.1/dns-query", int tunMtu = TunSettings.DefaultMtu) => new()
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

    private static AppSettings VlessSettings(string vpnDns = "https://dns.google/dns-query", int tunMtu = TunSettings.DefaultMtu) => new()
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
