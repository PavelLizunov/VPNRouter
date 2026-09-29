using VPNRouter.App.ViewModels;
using VPNRouter.App.Localization;
using VPNRouter.Core.Models;

namespace VPNRouter.Tests;

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
            Security = "reality",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        };
        var vm = new ServerViewModel(entry);
        Assert.Equal("naive", vm.HostSubtitle);
    }

    [Fact]
    public void ProtocolUseCase_ShowsUserIntent()
    {
        var oldLang = Strings.Lang;
        try
        {
            var vless = new ServerViewModel(new VlessServerEntry
            {
                Protocol = "vless",
                Transport = new VlessTransportConfig { Type = "tcp" },
            });
            var hy2 = new ServerViewModel(new VlessServerEntry { Protocol = "hysteria2" });
            var dns = new ServerViewModel(new VlessServerEntry
            {
                Protocol = "dns-tunnel",
                DnsDomain = "t.example",
            });

            Strings.Lang = "en";
            Assert.Equal("Daily", vless.ProtocolUseCase);
            Assert.Equal("Games/voice", hy2.ProtocolUseCase);
            Assert.Equal("Emergency", dns.ProtocolUseCase);
            Assert.Contains("UDP-friendly", hy2.ProtocolUseCaseTooltip);

            Strings.Lang = "ru";
            Assert.Equal("Повседневно", vless.ProtocolUseCase);
            Assert.Equal("Игры/звонки", hy2.ProtocolUseCase);
            Assert.Equal("Аварийный", dns.ProtocolUseCase);
            Assert.Contains("UDP-friendly", hy2.ProtocolUseCaseTooltip);
        }
        finally
        {
            Strings.Lang = oldLang;
        }
    }

    [Fact]
    public void HostSubtitle_NaiveWithRealHy2Sibling_ShowsNaivePlusHy2()
    {
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
        var naive = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia NAIVE", Protocol = "naive", Server = "cdn.example.com",
            Port = 443, Security = "reality", PairGroup = "cdn",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "cdn.example.com" },
            Transport = new VlessTransportConfig { Type = "tcp" },
        });
        var list = new System.Collections.Generic.List<ServerViewModel> { naive };

        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive", naive.HostSubtitle);

        var hy2 = new ServerViewModel(new VlessServerEntry
        {
            Name = "Latvia HY2", Protocol = "hysteria2", Server = "213.155.15.93", Port = 8444, PairGroup = "cdn",
        });
        list.Add(hy2);
        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive + hy2", naive.HostSubtitle);

        list.Remove(hy2);
        ServerViewModel.RefreshUdpSiblingFlags(list);
        Assert.Equal("naive", naive.HostSubtitle);
    }
}
