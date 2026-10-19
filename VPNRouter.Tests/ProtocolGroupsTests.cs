using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using VPNRouter.App.Controls;
using VPNRouter.App.ViewModels;
using VPNRouter.App.Views.Pages;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public class ProtocolGroupsTests
{
    private static VlessServerEntry Entry(string protocol, string? transport = null, string name = "srv") => new()
    {
        Name = name,
        Protocol = protocol,
        Server = name + ".example.net",
        Port = 443,
        Transport = new VlessTransportConfig { Type = transport ?? "tcp" },
    };

    [AvaloniaFact]
    public void SimpleConfigServer_ResolvesSelectedGeneratedServer()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var server = new ServerViewModel(Entry("hysteria2", null, "Germany HY2"));
        vm.SubscriptionServers.Add(server);
        vm.SelectedSubscriptionServer = server;
        vm.Servers.Add(server);
        vm.SelectedServer = server;

        Assert.Same(server, vm.SmpConfigServer);
        Assert.True(vm.SmpHasConfigServer);
    }

    [AvaloniaFact]
    public void SimpleConfigServer_IsNullWithoutServers()
    {
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        vm.SubscriptionServers.Clear();
        vm.Servers.Clear();

        Assert.Null(vm.SmpConfigServer);
        Assert.False(vm.SmpHasConfigServer);
    }

    [Theory]
    [InlineData("vless", "tcp", ProtocolGroup.Daily)]
    [InlineData("vless", "xhttp", ProtocolGroup.StealthWeb)]
    [InlineData("vless", "ws", ProtocolGroup.WebFallback)]
    [InlineData("vless", "grpc", ProtocolGroup.WebFallback)]
    [InlineData("hysteria2", null, ProtocolGroup.GamesVoice)]
    [InlineData("hy2", null, ProtocolGroup.GamesVoice)]
    [InlineData("tuic", null, ProtocolGroup.GamesVoice)]
    [InlineData("amneziawg", null, ProtocolGroup.LowPing)]
    [InlineData("naive", null, ProtocolGroup.WebOnly)]
    [InlineData("ss", null, ProtocolGroup.Fallback)]
    [InlineData("shadowsocks", null, ProtocolGroup.Fallback)]
    [InlineData("dns-tunnel", null, ProtocolGroup.Emergency)]
    [InlineData("VLESS", "XHTTP", ProtocolGroup.StealthWeb)]
    public void Of_MapsProtocolAndTransport(string protocol, string? transport, ProtocolGroup expected)
    {
        Assert.Equal(expected, ProtocolGroups.Of(Entry(protocol, transport)));
    }

    [Fact]
    public void Of_AwgConfigAndDnsDomainWinOverTheProtocolName()
    {
        var awg = Entry("vless");
        awg.Awg = new AwgConfig();
        Assert.Equal(ProtocolGroup.LowPing, ProtocolGroups.Of(awg));

        var dns = Entry("vless");
        dns.DnsDomain = "t.example";
        Assert.Equal(ProtocolGroup.Emergency, ProtocolGroups.Of(dns));

        Assert.Equal(ProtocolGroup.Daily, ProtocolGroups.Of(null));
    }

    [Fact]
    public void EveryGroup_HasItsOwnIcon_KnownToBothUis()
    {
        var groups = Enum.GetValues<ProtocolGroup>();
        var icons = groups.Select(ProtocolGroups.Icon).ToList();
        Assert.Equal(groups.Length, icons.Distinct().Count());
        foreach (var icon in icons)
        {
            Assert.True(UiIcons.Exists(icon), $"UiIcons has no {icon}");
            Assert.True(Pictogram.Has(icon), $"Pictogram has no {icon}");
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryGroup_HasColourTokens_InBothThemes(bool dark)
    {
        var app = Avalonia.Application.Current!;
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        foreach (var group in Enum.GetValues<ProtocolGroup>())
        {
            var prefix = ProtocolGroups.TokenPrefix(group);
            Assert.True(app.TryGetResource(prefix + "FgBrush", variant, out var fg) && fg is Avalonia.Media.ISolidColorBrush,
                $"{prefix}FgBrush missing in {variant}");
            Assert.True(app.TryGetResource(prefix + "BgBrush", variant, out var bg) && bg is Avalonia.Media.ISolidColorBrush,
                $"{prefix}BgBrush missing in {variant}");
        }
    }

    [Fact]
    public void ServerViewModel_ExposesTheGroupOfItsEntry()
    {
        Assert.Equal(ProtocolGroup.GamesVoice, new ServerViewModel(Entry("hysteria2")).ProtocolGroup);
        Assert.Equal(ProtocolGroup.StealthWeb, new ServerViewModel(Entry("vless", "xhttp")).ProtocolGroup);
    }

    [AvaloniaFact]
    public void SubscribeList_ShowsAColouredDiscPerServer()
    {
        var app = Avalonia.Application.Current!;
        using var vm = new MainWindowViewModel(new InMemorySettingsStore());
        var awg = Entry("amneziawg", null, "Finland AWG");
        awg.Awg = new AwgConfig();
        var dns = Entry("dns-tunnel", null, "Emergency DNS");
        dns.DnsDomain = "t.example";
        VlessServerEntry[] entries =
        [
            Entry("vless", "tcp", "Netherlands Reality"), Entry("hysteria2", null, "Germany HY2"), awg,
            Entry("vless", "xhttp", "Sweden XHTTP"), Entry("vless", "ws", "Poland WS"), Entry("naive", null, "Latvia Naive"),
            Entry("ss", null, "Turkey SS"), dns,
        ];
        foreach (var entry in entries) vm.SubscriptionServers.Add(new ServerViewModel(entry));

        var page = new SubscribePage { DataContext = vm };
        var window = new Window { Width = 520, Height = 900, Content = page };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("SurfaceAppBrush"));
        window.Show();
        var discs = page.GetVisualDescendants().OfType<ProtocolDisc>().ToList();
        Assert.Equal(entries.Length, discs.Count);
        Assert.Equal(Enum.GetValues<ProtocolGroup>().Length, discs.Select(d => d.Group).Distinct().Count());
        foreach (var disc in discs)
        {
            Assert.True(app.TryGetResource(ProtocolGroups.TokenPrefix(disc.Group) + "BgBrush", app.ActualThemeVariant, out var bg));
            Assert.Same(bg, disc.Background);
            var glyph = Assert.IsType<Pictogram>(disc.Child);
            Assert.Equal(ProtocolGroups.Icon(disc.Group), glyph.Id);
            Assert.NotNull(glyph.Foreground);
        }
        // Capture shows the window again, saves the frame and closes it.
        ScreenshotHelper.Capture(window, "protocol-discs-subscribe");
    }
}
