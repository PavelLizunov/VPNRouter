using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AssistantRepairCandidateTests
{
    [Fact]
    public void Repair_parser_corrects_only_invalid_mtu_before_validation()
    {
        const string yaml = "tun:\n  mtu: 9000\n  auto_route: false\n";
        Assert.Null(SettingsLoader.ParseLiveCandidate(yaml));

        var candidate = Assert.IsType<AppSettings>(SettingsLoader.ParseRepairCandidate(yaml, out var mtu));
        Assert.Equal(TunSettings.DefaultMtu, mtu);
        Assert.Equal(TunSettings.DefaultMtu, candidate.Tun.Mtu);
        Assert.False(candidate.Tun.AutoRoute);
    }

    [Fact]
    public void Repair_parser_retains_other_validation_errors()
    {
        const string yaml = "app:\n  config_mode: unsupported\ntun:\n  mtu: 9000\n";
        Assert.Null(SettingsLoader.ParseRepairCandidate(yaml, out _));
    }

    [Fact]
    public void Server_trials_stay_inside_the_active_feed_and_compatible_protocol()
    {
        var settings = ServerSettings();
        var alternatives = AssistantRepairPolicy.AlternateServers(settings);
        var alternate = Assert.Single(alternatives);
        Assert.Equal("active-feed", alternate.FeedId);
        Assert.Equal("alternate", alternate.Entry.Name);
        Assert.Equal("selected", settings.App.ActiveSubscriptionServer);
        Assert.True(settings.Vless.AutoSelectBestServer);
    }

    [Fact]
    public void Server_trials_require_autoselect_and_an_unambiguous_active_row()
    {
        var settings = ServerSettings();
        settings.Vless.AutoSelectBestServer = false;
        Assert.Empty(AssistantRepairPolicy.AlternateServers(settings));
        settings.Vless.AutoSelectBestServer = true;
        settings.App.Subscriptions[0].Servers.Add(Server("selected", "203.0.113.24"));
        Assert.Empty(AssistantRepairPolicy.AlternateServers(settings));
    }

    private static AppSettings ServerSettings()
    {
        var settings = new AppSettings();
        settings.App.ConfigMode = "subscribe";
        settings.App.ActiveSubscriptionId = "active-feed";
        settings.App.ActiveSubscriptionServer = "selected";
        settings.Vless.AutoSelectBestServer = true;
        var otherProtocol = Server("other-protocol", "203.0.113.22");
        otherProtocol.Protocol = "vmess";
        var chained = Server("chained", "203.0.113.23");
        chained.DetourVia = "configured-hop";
        settings.App.Subscriptions =
        [
            new() { Id = "active-feed", Enabled = true, Servers =
                [Server("selected", "203.0.113.20"), Server("alternate", "203.0.113.21"), otherProtocol, chained] },
            new() { Id = "different-feed", Enabled = true, Servers = [Server("other-feed", "203.0.113.25")] },
        ];
        return settings;
    }

    private static VlessServerEntry Server(string name, string address) => new()
    {
        Name = name,
        Server = address,
        Uuid = "530a76bd-f001-4f42-abb9-0ae363769ae7",
    };
}
