using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPinnedServerConfigTests
{
    // The first same-host node is the Business choice; the flow sibling must
    // never become the main proxy for a verified no-flow choice.
    private const string Connection =
        "vless://primary@192.0.2.1:443?security=tls#Primary" +
        "\nvless://primary-flow@192.0.2.1:2443?security=tls&flow=xtls-rprx-vision#Primary%20Flow" +
        "\nvless://reserve@192.0.2.2:443?security=tls#Reserve";

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void PinnedExactRow_BeatsSameHostFlowSibling_AndPortalUsesIt(string mode)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, Connection,
            App.Services.BusinessBootstrap.ReadRules());
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false;
        settings.App.BlockQuicOnTcpProxy = false;

        var submitted = SettingsLoader.Copy(settings);
        var pinned = submitted.Vless.Servers.Single(server => server.Name == "Primary");
        submitted.Vless.ActiveServer = pinned.Name;
        submitted.Vless.AutoSelectBestServer = false;
        pinned.UseAsSingleOutbound = true;

        Assert.False(settings.Vless.Servers.Single(server => server.Name == "Primary")
            .UseAsSingleOutbound);
        Assert.Equal("Primary", settings.Vless.ActiveServer);

        var active = Assert.Single(submitted.Vless.GetActiveServers());
        Assert.Same(pinned, active);
        Assert.Equal(443, active.Port);

        var config = ConfigGenerator.Generate(new Profile(), [], submitted);
        BusinessDefaults.ApplyDns(config, submitted.App.CustomRules);
        BusinessDefaults.ApplyDomainTransports(config, false, false, false);
        ConfigGenerator.ApplyBusinessPortal(config, submitted);

        var proxy = Assert.Single(config.Outbounds.Where(outbound => outbound.Tag == "proxy"));
        Assert.Equal("192.0.2.1", proxy.Server);
        Assert.Equal(443, proxy.ServerPort);
        Assert.Null(proxy.Flow);
        Assert.DoesNotContain(config.Outbounds, outbound => outbound.Tag == "proxy-udp");
        Assert.DoesNotContain(config.Outbounds, outbound => outbound.ServerPort == 2443);

        var portal = Assert.Single(config.Outbounds
            .Where(outbound => outbound.Tag == ConfigGenerator.BusinessPortalOutbound));
        Assert.Equal("192.0.2.1", portal.Server);
        Assert.Equal(443, portal.ServerPort);
        Assert.Equal("business-portal",
            Assert.Single(config.Dns.Servers
                .Where(dns => dns.Tag == ConfigGenerator.BusinessPortalDns)).Detour);
        Assert.Contains(config.Route.Rules, rule =>
            rule.Outbound == ConfigGenerator.BusinessPortalOutbound &&
            rule.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true);
    }

    [Fact]
    public void PinnedExactRow_DoesNotChangeSavedHostSiblingFlags()
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, Connection,
            App.Services.BusinessBootstrap.ReadRules());
        var before = settings.Vless.Servers.Select(server =>
            (server.Name, server.UseAsSingleOutbound, server.Port)).ToArray();

        var submitted = SettingsLoader.Copy(settings);
        var selected = submitted.Vless.Servers.Single(server => server.Name == "Primary");
        submitted.Vless.ActiveServer = selected.Name;
        selected.UseAsSingleOutbound = true;

        var after = settings.Vless.Servers.Select(server =>
            (server.Name, server.UseAsSingleOutbound, server.Port)).ToArray();
        Assert.Equal(before, after);
        Assert.False(settings.Vless.Servers.Single(server => server.Name == "Primary")
            .UseAsSingleOutbound);
    }
}
