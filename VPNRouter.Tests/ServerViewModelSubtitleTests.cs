using VPNRouter.App.ViewModels;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// ServerViewModel.HostSubtitle protocol-aware label — v2.41.1-r4
//
// brat screenshot: a NaiveProxy server was mislabeled "tcp + reality" in the
// Servers list. naive fell into the VLESS default branch of the subtitle switch,
// which renders Transport.Type ("tcp") + Security ("reality") — both default
// fields that BuildNaiveOutbound ignores. The label must read "naive".
// ═══════════════════════════════════════════════════════════════════════════════

public class ServerViewModelSubtitleTests
{
    [Fact]
    public void HostSubtitle_NaiveServer_ShowsNaive_NotTcpReality()
    {
        var entry = new VlessServerEntry
        {
            Name = "Latvia NAIVE",
            Protocol = "naive",
            Server = "cdn.example.com",
            Port = 443,
            Security = "reality", // default field BuildNaiveOutbound ignores
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("naive", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_Vless_StillShowsTransportPlusSecurity()
    {
        var entry = new VlessServerEntry
        {
            Name = "Germany VLESS",
            Protocol = "vless",
            Server = "1.2.3.4",
            Port = 443,
            Security = "reality",
            Transport = new VlessTransportConfig { Type = "tcp" },
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("tcp + reality", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_Hysteria2_Unaffected()
    {
        var entry = new VlessServerEntry
        {
            Name = "Latvia HY2",
            Protocol = "hysteria2",
            Server = "1.2.3.4",
            Port = 8444,
            ObfsType = "salamander",
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("hysteria2 + salamander", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_NaiveWithRealHy2Sibling_ShowsNaivePlusHy2()
    {
        // r8 #6: "naive + hy2" requires a REAL UDP-capable sibling in the
        // collection (set via RefreshUdpSiblingFlags), not just a PairGroup tag.
        var naive = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com",
            Port = 443, Security = "reality", PairGroup = "cdn",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        });
        var hy2 = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia HY2", Protocol = "hysteria2", Server = "213.155.15.93",
            Port = 8444, PairGroup = "cdn",
        });
        ServerViewModel.RefreshUdpSiblingFlags(new[] { naive, hy2 });
        Assert.Equal("naive + hy2", naive.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_NaivePairTagButNoSibling_ShowsNaiveOnly()
    {
        // r8 #6: a PairGroup tag with NO matching Hy2/TUIC sibling must NOT claim
        // "naive + hy2" — the label can't promise a pairing config-gen won't make.
        var naive = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com",
            Port = 443, Security = "reality", PairGroup = "cdn",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        });
        ServerViewModel.RefreshUdpSiblingFlags(new[] { naive });
        Assert.Equal("naive", naive.HostSubtitle);
    }
}
