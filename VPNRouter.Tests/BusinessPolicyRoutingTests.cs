using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection(Fakes.SafeModeStateCollection.Name)]
public sealed class BusinessPolicyRoutingTests
{
    [Fact]
    public void DedicatedBusinessCi_CompilesTheBusinessGeneratorBranches()
    {
        if (Environment.GetEnvironmentVariable("VPNROUTER_EXPECT_BUSINESS_POLICY_TESTS") == "1")
            Assert.True(ProductEdition.IsBusiness, "The policy CI job must compile the Business edition.");
    }

    [Theory]
    [InlineData("split", "direct")]
    [InlineData("split", "proxy")]
    [InlineData("full", "direct")]
    [InlineData("full", "proxy")]
    public void ExplicitSite_UsesSameDirectionBeforeGeoAndAppsInBothModes(string mode, string action)
    {
        using var geo = new GeoFixture();
        var settings = new AppSettings();
        var policy = Snapshot(new BusinessPolicyRule("avito.ru", action, "Edited existing site"));
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules(), policy);
        settings.App.RoutingMode = mode;

        var config = ConfigGenerator.Generate(new Profile(), [], settings, null, null,
            resolveRuleSet: null, checkGeoContents: RuleSetSamples.CheckFixtureContentsAsync);
        if (!ProductEdition.IsBusiness)
        {
            BusinessDefaults.ApplyDns(config, settings.App.CustomRules);
            ConfigGenerator.ApplyBusinessPortal(config, settings);
        }

        var siteRoute = config.Route.Rules.FindIndex(r => r.DomainSuffix?.Contains("avito.ru") == true && r.Action == "route");
        var geoRoute = config.Route.Rules.FindIndex(r => r.RuleSet?.Contains("vpnrouter-geosite-ru") == true);
        var siteDns = config.Dns.Rules.FindIndex(r => r.DomainSuffix?.Contains("avito.ru") == true);
        var geoDns = config.Dns.Rules.FindIndex(r => r.RuleSet?.Contains("vpnrouter-geosite-ru") == true);
        Assert.True(siteRoute >= 0 && geoRoute > siteRoute);
        Assert.True(siteDns >= 0 && geoDns > siteDns);
        Assert.Equal(action, config.Route.Rules[siteRoute].Outbound);
        Assert.Equal(action == "direct" ? "local-dns" : "vpn-dns", config.Dns.Rules[siteDns].Server);
        Assert.Equal(mode == "full" ? "proxy" : "direct", config.Route.Final);

        var appRoute = config.Route.Rules.FindIndex(r => r.ProcessName?.Contains("Telegram.exe") == true);
        var appDns = config.Dns.Rules.FindIndex(r => r.ProcessName?.Contains("Telegram.exe") == true);
        if (mode == "split")
        {
            Assert.True(appRoute > siteRoute);
            Assert.True(appDns > siteDns);
        }

        Assert.Contains(config.Route.Rules, r => r.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true && r.Outbound == ConfigGenerator.BusinessPortalOutbound);
        Assert.Contains(config.Dns.Rules, r => r.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true && r.Server == ConfigGenerator.BusinessPortalDns);
        Assert.Contains(config.Route.Rules, r => r.IpCidr?.Contains("195.211.120.35/32") == true && r.Outbound == "direct");
        Assert.Contains("195.211.120.35/32", config.Inbounds[0].RouteExcludeAddress!);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("full")]
    public void ProxySite_UsesUdpSiblingBeforeTcpRouteInBothModes(string mode)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SiblingConnection, BusinessBootstrap.ReadRules(),
            Snapshot(new BusinessPolicyRule("example.com", "proxy", "")));
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false;
        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        Assert.Contains(config.Outbounds, o => o.Tag == "proxy-udp");
        if (!ProductEdition.IsBusiness)
        {
            BusinessDefaults.ApplyDomainTransports(config, true, false, settings.App.BlockQuicOnTcpProxy);
            ConfigGenerator.ApplyBusinessPortal(config, settings);
        }
        var rules = config.Route.Rules.Where(r => r.DomainSuffix?.Contains("example.com") == true).ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Equal("udp", rules[0].Network);
        Assert.Equal("proxy-udp", rules[0].Outbound);
        Assert.Equal("proxy", rules[1].Outbound);
        Assert.DoesNotContain(rules, r => r.Action == "reject");
        Assert.Contains(config.Route.Rules, r => r.DomainSuffix?.Contains(BusinessPolicyDomains.PortalDomain) == true && r.Network == "udp" && r.Action == "reject");
    }

    [Theory]
    [InlineData("split", true)]
    [InlineData("full", true)]
    [InlineData("split", false)]
    [InlineData("full", false)]
    public void TcpOnlyProxySite_PreservesQuicFallbackChoiceWithoutBlockingDirectSites(string mode, bool blockQuic)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules(),
            Snapshot(new BusinessPolicyRule("example.com", "proxy", ""), new BusinessPolicyRule("example.org", "direct", "")));
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false;
        settings.App.BlockQuicOnTcpProxy = blockQuic;
        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        Assert.DoesNotContain(config.Outbounds, o => o.Tag == "proxy-udp");
        if (!ProductEdition.IsBusiness)
            BusinessDefaults.ApplyDomainTransports(config, false, false, blockQuic);
        var rules = config.Route.Rules.Where(r => r.DomainSuffix?.Contains("example.com") == true).ToArray();
        Assert.Equal(blockQuic ? 3 : 1, rules.Length);
        Assert.Equal("proxy", rules[^1].Outbound);
        if (blockQuic)
        {
            Assert.Equal("reject", rules[0].Action);
            Assert.Equal("udp", rules[0].Network);
            Assert.Equal(new[] { 443 }, rules[0].Port);
            Assert.Equal("reject", rules[1].Action);
            Assert.Equal("quic", rules[1].Protocol);
        }
        Assert.Equal("direct", Assert.Single(config.Route.Rules,
            r => r.DomainSuffix?.Contains("example.org") == true).Outbound);
    }

    [Fact]
    public void NativeUdpProxySite_DoesNotInsertTcpFallbackGuards()
    {
        var config = new SingBoxConfig();
        config.Route.Rules = [new RouteRule { DomainSuffix = ["example.com"], Action = "route", Outbound = "proxy" }];
        BusinessDefaults.ApplyDomainTransports(config, hasUdpProxy: false,
            proxyCarriesUdpNatively: true, blockQuicOnTcpProxy: true);
        var site = Assert.Single(config.Route.Rules);
        Assert.Equal("proxy", site.Outbound);
        Assert.Null(site.Network);
    }

    [Fact]
    public void FullReplacement_DoesNotRestoreSeededSitesAndKeepsProtectedRules()
    {
        var seed = BusinessBootstrap.ReadRules();
        var effective = BusinessPolicyRouting.Compose(seed, Snapshot(new BusinessPolicyRule("example.com", "proxy", "New")));
        Assert.DoesNotContain(effective, r => r.Value is "avito.ru" or "youtube.com");
        Assert.Contains(effective, r => r.Value == "example.com" && r.Action == "proxy");
        Assert.All(BusinessPolicyDomains.ProtectedDirectDomains, domain =>
            Assert.Equal("direct", Assert.Single(effective, r => r.Value == domain).Action));
        Assert.Equal("proxy", Assert.Single(effective, r => r.Value == BusinessPolicyDomains.PortalDomain).Action);
        Assert.Equal(seed.Where(r => r.Type == "process_name").Select(r => r.Value),
            effective.Where(r => r.Type == "process_name").Select(r => r.Value));
        var sip = effective.FindIndex(r => r.Value == "195.211.120.35/32");
        Assert.True(sip >= 0 && sip < effective.FindIndex(r => r.Value == "example.com"));
    }

    [Fact]
    public void EmptyPublishedList_DiffersFromFirstUseSeed()
    {
        var seed = BusinessBootstrap.ReadRules();
        var firstUse = BusinessPolicyRouting.Compose(seed, null);
        var empty = BusinessPolicyRouting.Compose(seed, Snapshot());
        Assert.Contains(firstUse, r => r.Value == "youtube.com");
        Assert.DoesNotContain(empty, r => r.Value == "youtube.com");
        Assert.Contains(empty, r => r.Type == "process_name" && r.Value == "Claude.exe");
        Assert.Contains(empty, r => r.Value == BusinessPolicyDomains.PortalDomain);
    }

    [Fact]
    public void ComposedRules_DoNotAliasSeedOrOtherCaptures()
    {
        var seed = BusinessBootstrap.ReadRules();
        var first = BusinessPolicyRouting.Compose(seed, null);
        var second = BusinessPolicyRouting.Compose(seed, null);
        Assert.Single(first, r => r.Value == "youtube.com").Action = "direct";
        Assert.Single(first, r => r.Value == "Telegram.exe").Value = "changed.exe";
        Assert.Equal("proxy", Assert.Single(seed, r => r.Value == "youtube.com").Action);
        Assert.Equal("proxy", Assert.Single(second, r => r.Value == "youtube.com").Action);
        Assert.Contains(second, r => r.Value == "Telegram.exe");
    }

    [Fact]
    public void InvalidProtectedOverride_DoesNotChangeSettings()
    {
        var settings = new AppSettings();
        settings.Vless.ActiveServer = "Previous";
        var policy = Snapshot(new BusinessPolicyRule("admin.corp.ninitux.com", "direct", ""));
        Assert.Throws<InvalidDataException>(() => BusinessDefaults.Apply(settings,
            BusinessDefaultsTests.SampleConnection, BusinessBootstrap.ReadRules(), policy));
        Assert.Equal("Previous", settings.Vless.ActiveServer);
    }

    private static BusinessPolicySnapshot Snapshot(params BusinessPolicyRule[] rules) =>
        new("corp-test", "test-key", 1, 1, false, rules, new string('A', 64));

    private sealed class GeoFixture : IDisposable
    {
        private readonly string _previous = AppPaths.DataDir;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "corp-policy-routing-" + Guid.NewGuid().ToString("N"));

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
