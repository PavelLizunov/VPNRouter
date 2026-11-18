using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;

namespace VPNRouter.Tests;

public class FreeConfigSelectionTests
{
    private const string Address = "vless://fixture-user@192.0.2.41:443?";
    private const string Checked = Address + "security=tls&sni=checked.example&type=ws&path=%2Fchecked#Profile";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyUsesOnlyTheVerifiedProfileAtASharedAddress(bool autoSelect)
    {
        var saved = ServerUriParser.Parse(Address + "security=tls&sni=saved.example&type=tcp#profile");
        var settings = new AppSettings();
        settings.App.BypassRussianTraffic = false;
        settings.App.BlockAds = false;
        settings.Vless.Servers = [saved];
        settings.Vless.AutoSelectBestServer = autoSelect;

        var servers = settings.Vless.Servers;
        var index = FreeConfigSelection.SelectOrAdd(servers, ServerUriParser.Parse(Checked));
        settings.Vless.ActiveServer = servers[index].Name;

        Assert.Equal("Profile #2", settings.Vless.ActiveServer);
        Assert.Same(saved, servers[0]);
        Assert.False(saved.UseAsSingleOutbound);
        Assert.Equal("saved.example", saved.Tls.ServerName);
        Assert.Equal("tcp", saved.Transport.Type);
        Assert.Same(servers[index], Assert.Single(settings.Vless.GetActiveServers()));
        var config = ConfigGenerator.Generate(new Profile { DnsMode = "vpn_only" }, [], settings);
        var outbound = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("vless", outbound.Type);
        Assert.Equal("checked.example", outbound.Tls?.ServerName);
        Assert.Equal("/checked", outbound.Transport?.Path);
        Assert.Equal(index, FreeConfigSelection.SelectOrAdd(servers, ServerUriParser.Parse(Checked)));
        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public void DifferentPasswordAtTheSameAddressKeepsBothProfiles()
    {
        var saved = ServerUriParser.Parse("hysteria2://saved-secret@192.0.2.42:443/#saved");
        var verified = ServerUriParser.Parse("hysteria2://checked-secret@192.0.2.42:443/#checked");
        var servers = new List<VlessServerEntry> { saved };
        var index = FreeConfigSelection.SelectOrAdd(servers, verified);
        Assert.Equal(1, index);
        Assert.Equal("saved-secret", saved.Password);
        Assert.Equal("checked-secret", servers[index].Password);
    }

    [Fact]
    public void ApplyReusesAnExactProfileWithItsExistingName()
    {
        var saved = ServerUriParser.Parse(Checked);
        saved.Name = "My profile";
        var servers = new List<VlessServerEntry> { saved };
        Assert.Equal(0, FreeConfigSelection.SelectOrAdd(servers, ServerUriParser.Parse(Checked)));
        Assert.Same(saved, Assert.Single(servers));
        Assert.Equal("My profile", saved.Name);
    }
}
