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
    public void HostSubtitle_AmneziaWg_ShowsAmneziaWg_NotTcpReality()
    {
        // v2.45.0-r3: an awg:// entry carries empty Uuid + default
        // Security="reality", so without a field-based check it fell into the
        // VLESS default branch and rendered "tcp + reality" — the exact thing
        // the user reported ("при добавлении AWG пишет что это vless").
        var entry = new VlessServerEntry
        {
            Name = "main-brat",
            Protocol = "amneziawg",
            Server = "104.194.156.93",
            Port = 51820,
            Security = "reality", // default field the AWG endpoint ignores
            Awg = new AwgConfig
            {
                PrivateKey = "XJRWW/WbfydGk7/7Kn3LLn+70XoT6se7SX9zUztOuKU=",
                PeerPublicKey = "iLtvwNI8UxIFHB9wNjyMud7/nofHJ5IBZaMC/knnWT0=",
                Address = new System.Collections.Generic.List<string> { "10.66.0.23/32" },
                Jc = 7,
            },
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("amneziawg + obfs", vm.HostSubtitle);
    }

    [Fact]
    public void HostSubtitle_AmneziaWg_DetectedByFieldEvenIfProtocolDropped()
    {
        // Field-based robustness: even if a serialization round-trip reset
        // Protocol to the "vless" default, the Awg payload still identifies
        // this as AmneziaWG (mirrors the dns-tunnel IsDnsTunnel approach).
        var entry = new VlessServerEntry
        {
            Name = "main-brat",
            Protocol = "vless", // simulate dropped discriminator
            Server = "93.95.226.167",
            Port = 51822,
            Security = "reality",
            Awg = new AwgConfig { PeerPublicKey = "abc=", PrivateKey = "def=" }, // Jc=0 -> no obfs hint
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("amneziawg", vm.HostSubtitle);
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

    [Fact]
    public void RefreshUdpSiblingFlags_TracksManualAddAndRemove()
    {
        // r9 follow-up #1: subtitle stays correct when the user adds/removes
        // servers manually (the CollectionChanged hook re-runs RefreshUdpSiblingFlags).
        var naive = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com",
            Port = 443, Security = "reality", PairGroup = "cdn",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        });
        var list = new System.Collections.Generic.List<ServerViewModel> { naive };

        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive", naive.HostSubtitle);            // no sibling yet

        var hy2 = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia HY2", Protocol = "hysteria2", Server = "213.155.15.93", Port = 8444, PairGroup = "cdn",
        });
        list.Add(hy2);
        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive + hy2", naive.HostSubtitle);      // sibling added

        list.Remove(hy2);
        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive", naive.HostSubtitle);            // sibling removed
    }
}
