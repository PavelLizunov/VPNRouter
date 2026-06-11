// v2.42.0-r2 — dns-tunnel server label (Android "tcp + reality" mislabel fix).
//
// HostSubtitle switched on the Protocol STRING, but Android's JSON cache can
// drop Protocol back to its "vless" default while the dns-tunnel payload
// (DnsDomain / DnsResolvers / DnsLeafCertPem) survives — so a working dns-tunnel
// server showed "tcp + reality" in the list. VlessServerEntry.IsDnsTunnel is now
// field-based (Protocol OR any dns payload), and HostSubtitle uses it.

using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;
using Xunit;

namespace VPNRouter.Tests;

public class DnsTunnelLabelTests
{
    // ── Core: field-based discriminator ────────────────────────────────────

    [Fact]
    public void IsDnsTunnel_TrueWhenProtocolSet()
    {
        Assert.True(new VlessServerEntry { Protocol = "dns-tunnel" }.IsDnsTunnel);
    }

    [Fact]
    public void IsDnsTunnel_TrueFromDnsDomain_EvenWhenProtocolLost()
    {
        // The exact Android symptom: Protocol round-tripped back to "vless" but
        // the dns payload survived. Still a dns-tunnel server.
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

    // ── App: HostSubtitle label ────────────────────────────────────────────

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
        // Android cache mislabel repro: Protocol="vless" but DnsDomain present.
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
        // Guard: the field-based detection must NOT mislabel a normal VLESS
        // server (no dns payload) — it keeps the transport + security subtitle.
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
