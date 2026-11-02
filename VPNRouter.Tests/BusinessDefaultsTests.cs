using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessDefaultsTests
{
    internal const string SampleConnection = "vless://test-user@192.0.2.1:443?security=tls&type=tcp#Pilot";

    [Fact]
    public void EmbeddedRules_ResolveOwnerConflictAndPreserveAllSourceProcesses()
    {
        var rules = BusinessBootstrap.ReadRules();
        Assert.Equal(43, rules.Count);
        Assert.All(rules, r => Assert.True(r.Enabled));
        Assert.Equal("direct", Assert.Single(rules, r => r.Value == "in-mag.ru").Action);
        Assert.Empty(CustomRulesParser.DetectConflicts(rules));
        Assert.Equal(new[] { "Telegram.exe", "WhatsApp.exe", "Discord.exe", "AnyDesk.exe", "Claude.exe", "iMe.exe" },
            rules.Where(r => r.Type == "process_name").Select(r => r.Value));
    }

    [Theory]
    [InlineData("split", "direct")]
    [InlineData("full", "proxy")]
    public void Preset_ReusesGeneratorAndRoutesDestinationsBeforeProcesses(string mode, string final)
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, SampleConnection, BusinessBootstrap.ReadRules());
        settings.App.RoutingMode = mode;
        settings.App.BypassRussianTraffic = false; // Geo routing has its own existing contracts.
        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        BusinessDefaults.ApplyDns(config, settings.App.CustomRules);
        Assert.Equal(final, config.Route.Final);
        Assert.Equal(mode == "split" ? "local-dns" : "vpn-dns", config.Dns.Final);
        Assert.Equal(6, settings.App.RoutingAppsInclude.Count);
        var direct = config.Route.Rules.FindIndex(r => r.DomainSuffix?.Contains("in-mag.ru") == true);
        var proxyProcess = config.Route.Rules.FindIndex(r => r.ProcessName?.Contains("Telegram.exe") == true && r.Outbound == "proxy");
        Assert.True(direct >= 0 && proxyProcess > direct);
        if (mode == "split")
        {
            var quic = config.Route.Rules.FindIndex(r => r.ProcessName?.Contains("Telegram.exe") == true && r.Action == "reject");
            Assert.True(quic >= 0 && quic < proxyProcess);
        }
        Assert.Contains(config.Route.Rules, r => r.IpCidr?.Contains("195.211.120.35/32") == true && r.Outbound == "direct");
        Assert.Contains("195.211.120.35/32", config.Inbounds[0].RouteExcludeAddress!);
        Assert.Contains(config.Dns.Rules, r => r.DomainSuffix?.Contains("uiscom.ru") == true && r.Server == "local-dns");
        Assert.Contains(config.Dns.Rules, r => r.DomainSuffix?.Contains("youtube.com") == true && r.Server == "vpn-dns");
        Assert.Contains(config.Route.Rules, r => r.DomainSuffix?.Contains("youtube.com") == true && r.Outbound == "proxy");
    }

    [Fact]
    public void Preset_LeavesUdpSiblingSelectionToTheExistingAppRoutingPath()
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, SampleConnection, BusinessBootstrap.ReadRules());
        settings.App.BypassRussianTraffic = false;
        var first = settings.Vless.Servers[0];
        first.Flow = "xtls-rprx-vision";
        settings.Vless.Servers.Add(new VlessServerEntry
        {
            Name = "UDP", Server = first.Server, Port = first.Port,
            Uuid = "test-udp", Security = "tls", Flow = ""
        });
        var config = ConfigGenerator.Generate(new Profile(), [], settings);
        var firstProcessRule = config.Route.Rules.First(r => r.ProcessName?.Contains("Telegram.exe") == true);
        Assert.Equal("udp", firstProcessRule.Network);
        Assert.Equal("proxy-udp", firstProcessRule.Outbound);
        Assert.DoesNotContain(settings.App.CustomRules, r => r.Type == "process_name" && r.Action == "proxy");
    }

    [Fact]
    public void Preset_MissingConnectionFailsBeforeChangingSettings()
    {
        var settings = new AppSettings();
        Assert.Throws<InvalidDataException>(() => BusinessDefaults.Apply(settings, "", BusinessBootstrap.ReadRules()));
        Assert.Empty(settings.Vless.Servers);
    }

    [Fact]
    public void Dns_RussianBypassPrecedesProxyAndDirectExceptionsPrecedeApps()
    {
        var config = new SingBoxConfig();
        config.Dns.Rules = [new DnsRule { RuleSet = ["vpnrouter-geosite-ru"], Server = "vpn-dns" }];
        BusinessDefaults.ApplyDns(config, BusinessBootstrap.ReadRules());
        var ru = config.Dns.Rules.FindIndex(r => r.RuleSet != null);
        Assert.Equal("local-dns", config.Dns.Rules[ru].Server);
        Assert.True(config.Dns.Rules.FindIndex(r => r.DomainSuffix?.Contains("uiscom.ru") == true) < ru);
        Assert.True(config.Dns.Rules.FindIndex(r => r.DomainSuffix?.Contains("youtube.com") == true) > ru);
    }

    [Fact]
    public void Preset_DefaultsToDirectAndEnablesExistingRecovery()
    {
        var settings = new AppSettings();
        BusinessDefaults.Apply(settings, SampleConnection, BusinessBootstrap.ReadRules());
        Assert.Equal("split", settings.App.RoutingMode);
        Assert.True(settings.App.BypassRussianTraffic);
        Assert.True(settings.Monitoring.RestartOnFailure);
        Assert.False(settings.App.StrictDns);
        Assert.False(settings.App.DnsLeakLockdown);
        Assert.False(settings.Update.AutoCheck);
    }
}
