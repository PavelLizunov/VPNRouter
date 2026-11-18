using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection(Fakes.SafeModeStateCollection.Name)]
public sealed class BusinessPolicyPortalTransportTests
{
    [Theory]
    [InlineData("split", "tcp")]
    [InlineData("full", "tcp")]
    [InlineData("split", "ws")]
    [InlineData("full", "ws")]
    [InlineData("split", "xhttp")]
    [InlineData("full", "xhttp")]
    public void Portal_UsesExactSelectedVlessDespiteSameHostSiblings(string mode, string transport)
    {
        var previous = SingBoxFeatures.OverrideXhttp;
        SingBoxFeatures.OverrideXhttp = true;
        try
        {
            var settings = Settings(mode);
            var selected = settings.Vless.Servers[0];
            selected.Transport.Type = transport;
            selected.Transport.Path = "/selected";
            if (transport == "tcp")
            {
                selected.Security = "reality";
                selected.Flow = "xtls-rprx-vision";
                selected.Reality.PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A";
                selected.Reality.ShortId = "d86e92a0c6dd2271";
            }
            settings.Vless.Servers.AddRange(
            [
                Entry("Other VLESS", selected.Server, 8443, transport == "ws" ? "xhttp" : "ws"),
                Entry("Hysteria", selected.Server, 2443, protocol: "hysteria2"),
                Entry("TUIC", selected.Server, 3443, protocol: "tuic")
            ]);

            var config = Generate(settings);
            AssertExactPortal(config, selected);
            Assert.Equal(transport == "tcp" ? null : transport,
                Assert.Single(config.Outbounds, o => o.Tag == ConfigGenerator.BusinessPortalOutbound).Transport?.Type);
            // Ordinary traffic retains the full sibling pool and UDP behavior.
            Assert.Contains(config.Outbounds, o => o.Type == "hysteria2");
            Assert.Contains(config.Outbounds, o => o.Type == "tuic");
            var site = config.Route.Rules.Where(r => r.DomainSuffix?.Contains("example.com") == true).ToArray();
            Assert.Equal(transport == "tcp" ? "proxy-udp" : "proxy", site[0].Outbound);
            Assert.DoesNotContain(site, r => r.Action == "reject");
        }
        finally { SingBoxFeatures.OverrideXhttp = previous; }
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void Portal_TracksHostChoiceWithoutAutoSelectionOrHealthSubstitution(string mode)
    {
        var previousDir = AppPaths.DataDir;
        var root = Path.Combine(Path.GetTempPath(), "portal-health-" + Guid.NewGuid().ToString("N"));
        AppPaths.OverrideDataDir(root);
        ServerHealthStore.ResetForTests();
        try
        {
            var settings = Settings(mode);
            var reserve = Entry("Reserve", "192.0.2.2", 9443);
            settings.Vless.Servers.Add(reserve);
            settings.Vless.AutoSelectBestServer = true;
            var primary = settings.Vless.Servers[0];
            ServerHealthStore.Record(primary, ServerHealthVerdict.ProtocolHandshakeBlockedLikely);
            var narrowed = Generate(settings, _ => false);
            Assert.Equal(reserve.Server, Assert.Single(narrowed.Outbounds, o => o.Tag == "proxy").Server);
            AssertExactPortal(narrowed, primary);

            settings.Vless.ActiveServer = "reserve";
            AssertExactPortal(Generate(settings), reserve);
            settings.Vless.ActiveServer = primary.Name;
            AssertExactPortal(Generate(settings), primary);
        }
        finally
        {
            ServerHealthStore.ResetForTests();
            AppPaths.OverrideDataDir(previousDir);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("split", true)]
    [InlineData("split", false)]
    [InlineData("full", true)]
    [InlineData("full", false)]
    public void Portal_RejectsAllUdpBeforeSiteAppAndGeoRulesIncludingSubdomains(string mode, bool blockQuic)
    {
        using var geo = new GeoFixture();
        var settings = Settings(mode);
        settings.App.BypassRussianTraffic = true;
        settings.App.BlockQuicOnTcpProxy = blockQuic;
        settings.App.CustomRules.Insert(0, new CustomRule
        {
            Type = "domain_suffix", Value = "ninitux.com", Action = "direct", Enabled = true
        });
        var config = Generate(settings);
        var portalRoute = config.Route.Rules.FindIndex(r => r.Outbound == ConfigGenerator.BusinessPortalOutbound);
        Assert.True(config.Route.Rules.FindIndex(r => r.RuleSet?.Contains("vpnrouter-geosite-ru") == true) > portalRoute);
        foreach (var host in new[] { BusinessPolicyDomains.PortalDomain, "child." + BusinessPolicyDomains.PortalDomain })
        {
            foreach (var port in new[] { 53, 443, 8443 })
            {
                var udp = FirstRoute(config, host, "udp", port);
                Assert.Equal("reject", udp.Action);
                Assert.Null(udp.Outbound);
            }
            Assert.Equal(ConfigGenerator.BusinessPortalOutbound, FirstRoute(config, host, "tcp", 443).Outbound);
            Assert.Equal(ConfigGenerator.BusinessPortalDns, config.Dns.Rules.First(r => MatchesDomain(r.DomainSuffix, host)).Server);
        }
        Assert.Equal("direct", FirstRoute(config, "other.ninitux.com", "udp", 443).Outbound);
        if (mode == "split")
            Assert.True(config.Route.Rules.FindIndex(r => r.ProcessName?.Contains("Telegram.exe") == true) > portalRoute);
        else
            Assert.Equal("proxy", config.Route.Final);
        Assert.Contains(config.Route.Rules, r => r.IpCidr?.Contains("195.211.120.35/32") == true && r.Outbound == "direct");
        Assert.Contains(config.Dns.Rules, r => r.DomainSuffix?.Contains("uiscom.ru") == true && r.Server == "local-dns");
    }

    [Theory]
    [InlineData("split", "missing")]
    [InlineData("full", "missing")]
    [InlineData("split", "chained")]
    [InlineData("full", "chained")]
    [InlineData("split", "sibling")]
    [InlineData("full", "sibling")]
    [InlineData("split", "duplicate")]
    [InlineData("full", "duplicate")]
    [InlineData("split", "invalid")]
    [InlineData("full", "invalid")]
    [InlineData("split", "unavailable")]
    [InlineData("full", "unavailable")]
    public void Portal_InvalidSelectionFailsClosedWithoutProxyFallback(string mode, string failure)
    {
        var settings = Settings(mode);
        var selected = settings.Vless.Servers[0];
        var previous = SingBoxFeatures.OverrideXhttp;
        SingBoxFeatures.OverrideXhttp = false;
        try
        {
            switch (failure)
            {
                case "missing": settings.Vless.ActiveServer = "removed"; break;
                case "chained": selected.DetourVia = "entry"; break;
                case "sibling":
                    settings.Vless.Servers.Add(Entry("UDP", selected.Server, 2443));
                    settings.Vless.ActiveServer = "UDP";
                    break;
                case "duplicate": settings.Vless.Servers.Add(Entry(selected.Name, "192.0.2.2", 443)); break;
                case "invalid": selected.Uuid = ""; break;
                case "unavailable": selected.Transport.Type = "xhttp"; break;
            }
            var config = new SingBoxConfig();
            void AssertRejected(Action action)
            {
                if (failure == "invalid") Assert.Throws<InvalidDataException>(action);
                else Assert.Throws<InvalidOperationException>(action);
            }
            AssertRejected(() => ConfigGenerator.ApplyBusinessPortal(config, settings));
            Assert.DoesNotContain(config.Outbounds, o => o.Tag == ConfigGenerator.BusinessPortalOutbound);
            Assert.DoesNotContain(config.Dns.Servers, d => d.Tag == ConfigGenerator.BusinessPortalDns);
            if (ProductEdition.IsBusiness)
                AssertRejected(() => ConfigGenerator.Generate(new Profile(), [], settings));
        }
        finally { SingBoxFeatures.OverrideXhttp = previous; }
    }

    [Fact]
    public void StandardGenerator_DoesNotAddBusinessPortalTransport()
    {
        if (ProductEdition.IsBusiness) return;
        var config = ConfigGenerator.Generate(new Profile(), [], Settings("split"));
        Assert.DoesNotContain(config.Outbounds, o => o.Tag == ConfigGenerator.BusinessPortalOutbound);
        Assert.DoesNotContain(config.Dns.Servers, d => d.Tag == ConfigGenerator.BusinessPortalDns);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public async Task Failover_RestartsWithAvailableBusinessChoiceAndPreservesStandardPool(string mode)
    {
        var previous = SingBoxFeatures.OverrideXhttp;
        SingBoxFeatures.OverrideXhttp = false;
        try
        {
            var settings = Settings(mode);
            var active = settings.Vless.Servers[0];
            var sibling = Entry("Same host WS", active.Server, 8443, "ws");
            var reserve = Entry("Reserve", "192.0.2.4", 443);
            settings.Vless.Servers.AddRange([
                sibling,
                Entry("Other UDP", "192.0.2.2", 2443, protocol: "hysteria2"),
                Entry("Unavailable XHTTP", "192.0.2.3", 443, "xhttp"),
                reserve
            ]);
            var store = new Fakes.InMemorySettingsStore();
            var restarts = 0;
            var engine = new AutoFailoverEngine(settings, new ConfigSanityCheck(), restart: _ =>
            {
                restarts++;
                var config = ConfigGenerator.Generate(new Profile(), [], settings);
                if (ProductEdition.IsBusiness) AssertExactPortal(config, reserve);
                return Task.FromResult(true);
            }, store: store);

            var result = await engine.HandleDeadConfigAsync("synthetic failure", TestContext.Current.CancellationToken);
            Assert.True(result.Switched);
            Assert.Equal(ProductEdition.IsBusiness ? reserve.Name : sibling.Name, result.NewActiveServer);
            Assert.Equal(1, restarts);
            Assert.Equal(result.NewActiveServer, store.Load().Vless.ActiveServer);
            Assert.DoesNotContain(reserve.Name, engine.TriedServers);
        }
        finally { SingBoxFeatures.OverrideXhttp = previous; }
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void Portal_EmptySelectionUsesFirstBusinessChoice(string mode)
    {
        var settings = Settings(mode);
        var selected = settings.Vless.Servers[0];
        settings.Vless.ActiveServer = "";
        settings.Vless.Servers.Insert(0, Entry("Not a Business choice", "192.0.2.3", 2443, protocol: "hysteria2"));
        var config = new SingBoxConfig();
        ConfigGenerator.ApplyBusinessPortal(config, settings);
        AssertExactPortal(config, selected);
    }

    private static AppSettings Settings(string mode)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules(),
            new BusinessPolicySnapshot("corp-test", "test-key", 1, 1, false,
                [new BusinessPolicyRule("example.com", "proxy", "")], new string('A', 64)));
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false;
        settings.App.BlockQuicOnTcpProxy = false;
        return settings;
    }

    private static VlessServerEntry Entry(string name, string host, int port, string transport = "tcp", string protocol = "vless") => new()
    {
        Name = name, Server = host, Port = port, Protocol = protocol, Uuid = "test-user-" + name,
        Password = "test-password", Security = "tls", Transport = new() { Type = transport }
    };

    private static SingBoxConfig Generate(AppSettings settings, Func<VlessServerEntry, bool>? alive = null)
    {
        var config = ConfigGenerator.Generate(new Profile(), [], settings, null, alive, resolveRuleSet: null,
            checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
        if (!ProductEdition.IsBusiness)
        {
            BusinessDefaults.ApplyDns(config, settings.App.CustomRules);
            BusinessDefaults.ApplyDomainTransports(config, config.Outbounds.Any(o => o.Tag == "proxy-udp"), false,
                settings.App.BlockQuicOnTcpProxy);
            ConfigGenerator.ApplyBusinessPortal(config, settings);
        }
        return config;
    }

    private static void AssertExactPortal(SingBoxConfig config, VlessServerEntry selected)
    {
        var portal = Assert.Single(config.Outbounds, o => o.Tag == ConfigGenerator.BusinessPortalOutbound);
        Assert.Equal("vless", portal.Type);
        Assert.Equal(selected.Server, portal.Server);
        Assert.Equal(selected.Port, portal.ServerPort);
        Assert.Equal(selected.Uuid, portal.Uuid);
        Assert.Null(portal.Detour);
        Assert.Null(portal.Outbounds);
        var dns = Assert.Single(config.Dns.Servers, d => d.Tag == ConfigGenerator.BusinessPortalDns);
        Assert.Equal("https", dns.Type);
        Assert.Equal(ConfigGenerator.BusinessPortalOutbound, dns.Detour);
        Assert.Equal("local-dns", dns.DomainResolver!.Server);
        Assert.DoesNotContain(config.Route.Rules, r => r.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true && r.Outbound is "proxy" or "proxy-udp");
    }

    private static bool MatchesDomain(List<string>? suffixes, string host) => suffixes?.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal)) == true;

    private static RouteRule FirstRoute(SingBoxConfig config, string host, string network, int port) =>
        config.Route.Rules.First(r => r.Action is "route" or "reject" &&
            r.IpCidr == null && r.IpIsPrivate != true &&
            (r.DomainSuffix == null || MatchesDomain(r.DomainSuffix, host)) &&
            (r.Network == null || r.Network == network) && (r.Port == null || r.Port.Contains(port)));

    private sealed class GeoFixture : IDisposable
    {
        private readonly string _previous = AppPaths.DataDir;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "portal-geo-" + Guid.NewGuid().ToString("N"));

        internal GeoFixture()
        {
            AppPaths.OverrideDataDir(_root);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, RuleSetSamples.GeoIpRule());
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, RuleSetSamples.UpstreamGeositeTldRu());
        }

        public void Dispose()
        {
            AppPaths.OverrideDataDir(_previous);
            Directory.Delete(_root, recursive: true);
        }
    }
}
