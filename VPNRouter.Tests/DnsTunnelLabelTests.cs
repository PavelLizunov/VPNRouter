using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public class DnsTunnelLabelTests
{
    [Fact]
    public void IsDnsTunnel_TrueWhenProtocolSet()
    {
        Assert.True(new VlessServerEntry { Protocol = "dns-tunnel" }.IsDnsTunnel);
    }

    [Fact]
    public void IsDnsTunnel_TrueFromDnsDomain_EvenWhenProtocolLost()
    {
        var e = new VlessServerEntry { Protocol = "vless", DnsDomain = "tunnel.example.com" };
        Assert.True(e.IsDnsTunnel);
    }

    [Fact]
    public void IsDnsTunnel_TrueFromResolversOrCert()
    {
        Assert.True(new VlessServerEntry { Protocol = "vless", DnsResolvers = { "8.8.8.8:53" } }.IsDnsTunnel);
        Assert.True(new VlessServerEntry { Protocol = "vless", DnsLeafCertPem = "-----BEGIN CERTIFICATE-----..." }.IsDnsTunnel);
    }

    [Fact]
    public void IsDnsTunnel_FalseForNormalVless()
    {
        var e = new VlessServerEntry { Protocol = "vless", Server = "1.2.3.4", Security = "reality" };
        Assert.False(e.IsDnsTunnel);
    }

    [Fact]
    public void HostSubtitle_DnsTunnelEntry_ShowsDnsTunnel()
    {
        var vm = new ServerViewModel(new VlessServerEntry
        {
            Protocol = "dns-tunnel",
            Server = "1.2.3.4",
            DnsDomain = "tunnel.example.com",
        });
        Assert.Equal("dns-tunnel", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_ProtocolLostButDnsPayloadSurvives_StillDnsTunnel()
    {
        var vm = new ServerViewModel(new VlessServerEntry
        {
            Protocol = "vless",
            Server = "1.2.3.4",
            DnsDomain = "tunnel.example.com",
        });
        Assert.Equal("dns-tunnel", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_NormalVless_UnchangedTcpReality()
    {
        var vm = new ServerViewModel(new VlessServerEntry
        {
            Protocol = "vless",
            Server = "1.2.3.4",
            Security = "reality",
            Transport = new VlessTransportConfig { Type = "tcp" },
        });
        Assert.Equal("tcp + reality", vm.HostSubtitle);
    }
}
