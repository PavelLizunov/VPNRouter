using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AssistantRepairCandidateTests
{
    [Fact]
    public void Repair_parser_corrects_only_invalid_mtu_before_validation()
    {
        string yaml = $"schema_version: {AppSettings.CurrentSchemaVersion}\ntun:\n  mtu: 9000\n  auto_route: false\n";
        Assert.Null(SettingsLoader.ParseLiveCandidate(yaml));

        var candidate = Assert.IsType<AppSettings>(SettingsLoader.ParseRepairCandidate(yaml, out var mtu));
        Assert.Equal(TunSettings.DefaultMtu, mtu);
        Assert.Equal(TunSettings.DefaultMtu, candidate.Tun.Mtu);
        Assert.False(candidate.Tun.AutoRoute);
    }

    [Fact]
    public void Repair_parser_retains_other_validation_errors()
    {
        string yaml = $"schema_version: {AppSettings.CurrentSchemaVersion}\napp:\n  config_mode: unsupported\ntun:\n  mtu: 9000\n";
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

    [Fact]
    public void Server_trials_respect_a_single_outbound_selection_with_autoselect_enabled()
    {
        var settings = ServerSettings();
        settings.App.Subscriptions[0].Servers[0].UseAsSingleOutbound = true;
        Assert.Empty(AssistantRepairPolicy.AlternateServers(settings));
    }

    [Fact]
    public void A_trial_routes_one_server_with_the_saved_row_identity_and_keeps_the_automatic_pool()
    {
        var saved = ServerSettings();
        saved.App.BypassRussianTraffic = false;
        var alternate = Assert.Single(AssistantRepairPolicy.AlternateServers(saved));
        var rowKey = SubscriptionEntry.RowKey(alternate.Entry);
        saved.App.ActiveSubscriptionServer = alternate.Entry.Name;
        saved.App.ActiveSubscriptionRow = rowKey;

        var runtime = VpnEngine.CopySessionSettings(saved);
        var row = Assert.IsType<VlessServerEntry>(runtime.App.Subscriptions[0].FindExactRow(alternate.Entry.Name, rowKey));
        row.PinForRepair();
        var sources = new Dictionary<string, VlessServerEntry>();
        var json = ConfigPipeline.Generate(new Profile { Name = "repair" }, [], runtime, proxySources: sources);
        var tunnel = new AcceptedTunnelInfo(runtime, json, "", null, sources);

        Assert.False(tunnel.UsesAutoGroup);
        Assert.Equal(alternate.FeedId, tunnel.Server.FeedId);
        Assert.Equal(rowKey, tunnel.Server.RowKey);
        Assert.Same(row, Assert.Single(runtime.Vless.GetActiveServers()));
        Assert.False(alternate.Entry.UseAsSingleOutbound);
        var reloaded = SettingsLoader.Copy(runtime);
        Assert.True(reloaded.Vless.AutoSelectBestServer);
        Assert.False(reloaded.App.Subscriptions[0].Servers[1].RequiresSingleOutbound());
        VlessServersResolver.Resolve(reloaded);
        Assert.True(reloaded.Vless.GetActiveServers().Count > 1);
        Assert.Same(alternate.Entry, saved.App.Subscriptions[0].FindExactRow(tunnel.Server.Name, tunnel.Server.RowKey));
    }

    [Fact]
    public void Server_trials_skip_an_alternate_with_its_own_explicit_pin()
    {
        var settings = ServerSettings();
        settings.App.Subscriptions[0].Servers[1].UseAsSingleOutbound = true;
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
        Security = "none",
        Uuid = "530a76bd-f001-4f42-abb9-0ae363769ae7",
    };
}
