using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class ConfigGeneratorTests
{
    [Fact]
    public void LinuxTun_DnsIsOwnedByTheApplication_NotNativeInteractiveHelpers()
    {
        var config = ConfigGenerator.Generate(CreateProfile(), [], CreateSettings());
        Assert.Equal(OperatingSystem.IsLinux() ? "disabled" : null, config.Inbounds.Single().DnsMode);
        Assert.Contains(config.Route.Rules, rule => rule.Action == "hijack-dns");
        var json = ConfigGenerator.Serialize(config);
        if (OperatingSystem.IsLinux())
            Assert.Contains("\"dns_mode\": \"disabled\"", json);
        else
            Assert.DoesNotContain("\"dns_mode\"", json);
    }

    private static AppSettings CreateSettings(int serverCount = 1)
    {
        var settings = new AppSettings
        {
            App = new AppConfig { LogLevel = "info" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig()
        };

        if (serverCount == 1)
        {
            settings.Vless.Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main",
                    Server = "1.2.3.4",
                    Port = 443,
                    Uuid = "test-uuid",
                    Flow = "xtls-rprx-vision",
                    Security = "reality",
                    Reality = new VlessRealityConfig
                    {
                        PublicKey = "testkey",
                        ShortId = "abcd"
                    }
                }
            };
        }
        else
        {
            settings.Vless.Servers = new List<VlessServerEntry>
            {
                new()
                {
                    Name = "main",
                    Server = "1.2.3.4",
                    Port = 443,
                    Uuid = "uuid-1",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "key1", ShortId = "aa" }
                },
                new()
                {
                    Name = "backup",
                    Server = "5.6.7.8",
                    Port = 443,
                    Uuid = "uuid-2",
                    Security = "reality",
                    Reality = new VlessRealityConfig { PublicKey = "key2", ShortId = "bb" }
                }
            };
        }

        return settings;
    }

    private static Profile CreateProfile(string dnsMode = "vpn_only")
    {
        return new Profile
        {
            Name = "TestProfile",
            DnsMode = dnsMode,
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } },
                new() { Name = "firefox.exe", ScanPatterns = new[] { "firefox.exe" } }
            }
        };
    }

    [Fact]
    public void SingleServer_ProxyOutboundIsVless()
    {
        var settings = CreateSettings(serverCount: 1);
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        Assert.Equal("1.2.3.4", proxy.Server);
    }

    [Fact]
    public void FullTunnel_BypassRuAndBlockAds_PreservesSniffPrefix()
    {
        var settings = CreateSettings();
        settings.App.RoutingMode = "full";
        settings.App.BypassRussianTraffic = true;
        settings.App.BlockAds = true;

        if (!GeoDataDownloader.AreGeoFilesAvailable())
        {
            return;
        }

        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var rules = config.Route.Rules;
        Assert.True(rules.Count >= 5, $"expected ≥5 rules, got {rules.Count}");
        Assert.Equal("sniff", rules[0].Action);
        Assert.Equal("hijack-dns", rules[1].Action);
        Assert.True(rules[2].IpIsPrivate, "rule[2] should be the private-ip → direct rule");

        var tail = rules.Skip(3).ToList();
        Assert.Contains(tail, r =>
            r.RuleSet != null
            && r.RuleSet.Contains("vpnrouter-geosite-ru")
            && r.Action == "route"
            && r.Outbound == "direct");
        Assert.Contains(tail, r =>
            r.RuleSet != null
            && r.RuleSet.Contains("vpnrouter-adblock")
            && r.Action == "reject");
        Assert.Equal("proxy", config.Route.Final);

        var geoDnsRule = config.Dns.Rules.Single(r =>
            r.RuleSet?.Contains("vpnrouter-geosite-ru") == true);
        Assert.Equal("vpn-dns", geoDnsRule.Server);
        Assert.Equal("proxy", config.Dns.Servers.Single(s => s.Tag == "vpn-dns").Detour);
        Assert.DoesNotContain(config.Dns.Servers, s => s.Tag == "vpnrouter-dns-ru");
    }

    [Theory]
    [InlineData("full", "include", true)]
    [InlineData("split", "include", true)]
    [InlineData("split", "exclude", true)]
    [InlineData("full", "include", false)]
    [InlineData("split", "include", false)]
    [InlineData("split", "exclude", false)]
    public void GeoBypass_RoutesDomainsAndIpsBeforeAppRules_AndKeepsTunnelDns(
        string routingMode, string appsMode, bool enabled)
    {
        var previousDataDir = AppPaths.DataDir;
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-generated-geo-dns-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(tempDataDir);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, RuleSetSamples.IntactEnvelope(10 * 1024));
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, RuleSetSamples.UpstreamGeositeTldRu());

            var settings = CreateSettings();
            settings.App.BypassRussianTraffic = enabled;
            settings.App.RoutingMode = routingMode;
            settings.App.RoutingAppsMode = appsMode;
            settings.App.RoutingAppsInclude = new List<string> { "firefox.exe" };
            settings.App.RoutingAppsExclude = new List<string> { "firefox.exe" };
            var config = ConfigGenerator.Generate(CreateProfile(), Array.Empty<string>(), settings);
            var serialized = System.Text.Json.Nodes.JsonNode.Parse(ConfigGenerator.Serialize(config))!;
            var routeRules = serialized["route"]!["rules"]!.AsArray();
            var bypassRules = routeRules.Where(r => r?["rule_set"]?.AsArray()
                .Any(tag => tag?.ToString() == "vpnrouter-geosite-ru") == true).ToList();

            Assert.Equal(routingMode == "full" || appsMode == "exclude" ? "proxy" : "direct", config.Route.Final);
            if (!enabled)
            {
                Assert.Empty(bypassRules);
                Assert.DoesNotContain("vpnrouter-geo", ConfigGenerator.Serialize(config), StringComparison.Ordinal);
                return;
            }

            var routeRule = Assert.Single(bypassRules)!;
            Assert.Equal(new[] { "vpnrouter-geosite-ru", "vpnrouter-geoip-ru" },
                routeRule["rule_set"]!.AsArray().Select(tag => tag!.ToString()));
            Assert.Equal("direct", routeRule["outbound"]!.ToString());
            Assert.Equal("route", routeRule["action"]!.ToString());
            Assert.Equal(3, routeRules.IndexOf(routeRule));
            Assert.Equal("sniff", routeRules[0]!["action"]!.ToString());
            Assert.Equal("hijack-dns", routeRules[1]!["action"]!.ToString());
            Assert.True(routeRules[2]!["ip_is_private"]!.GetValue<bool>());
            Assert.Contains(config.Route.RuleSet!, r => r.Tag == "vpnrouter-geoip-ru");
            if (routingMode == "split")
            {
                var appRule = Assert.Single(config.Route.Rules.Where(r => r.ProcessName?.Contains("firefox.exe") == true
                    && r.Action == "route"));
                Assert.True(config.Route.Rules.IndexOf(appRule) > 3);
                Assert.Equal(appsMode == "exclude" ? "direct" : "proxy", appRule.Outbound);
            }

            var geoRule = config.Dns.Rules.Single(r =>
                r.RuleSet?.Contains("vpnrouter-geosite-ru") == true);
            Assert.Equal("vpn-dns", geoRule.Server);
            Assert.Equal("proxy", config.Dns.Servers.Single(s => s.Tag == "vpn-dns").Detour);
            Assert.DoesNotContain(config.Dns.Servers, s => s.Tag == "vpnrouter-dns-ru");
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [Fact]
    public void GeoBypass_DamagedGeositeFile_IsLeftOutOfTheConfig()
    {
        var previousDataDir = AppPaths.DataDir;
        var tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-generated-geo-damaged-{Guid.NewGuid():N}");
        try
        {
            AppPaths.OverrideDataDir(tempDataDir);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, RuleSetSamples.IntactEnvelope(10 * 1024));
            File.WriteAllBytes(AppPaths.GeoSiteRuPath, RuleSetSamples.ZeroFilled(155));

            var settings = CreateSettings();
            settings.App.BypassRussianTraffic = true;
            var config = ConfigGenerator.Generate(CreateProfile(), Array.Empty<string>(), settings);

            Assert.DoesNotContain(config.Route.RuleSet ?? new List<RuleSetEntry>(), r => r.Tag.StartsWith("vpnrouter-geo", StringComparison.Ordinal));
            Assert.DoesNotContain(config.Route.Rules, r => r.RuleSet?.Any(tag => tag.StartsWith("vpnrouter-geo", StringComparison.Ordinal)) == true);
            Assert.DoesNotContain(config.Dns.Rules, r => r.RuleSet?.Any(tag => tag.StartsWith("vpnrouter-geo", StringComparison.Ordinal)) == true);
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
            if (Directory.Exists(tempDataDir))
                Directory.Delete(tempDataDir, recursive: true);
        }
    }

    [Fact]
    public void AutoSelect_On_MultiSameProtocol_ProxyIsUrltestWithShape()
    {
        var settings = CreateSettings(serverCount: 2);
        settings.Vless.AutoSelectBestServer = true;
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("urltest", proxy.Type);
        Assert.NotNull(proxy.Outbounds);
        Assert.Equal(2, proxy.Outbounds!.Count);
        Assert.Contains("vless-main", proxy.Outbounds);
        Assert.Contains("vless-backup", proxy.Outbounds);
        Assert.Equal("http://www.gstatic.com/generate_204", proxy.Url);
        Assert.Equal("3m", proxy.Interval);
        Assert.Equal(150, proxy.Tolerance);
        Assert.False(proxy.InterruptExistConnections);
    }

    [Fact]
    public void AutoSelect_On_ChildVlessOutboundsExist()
    {
        var settings = CreateSettings(serverCount: 2);
        settings.Vless.AutoSelectBestServer = true;
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var main = config.Outbounds.First(o => o.Tag == "vless-main");
        Assert.Equal("vless", main.Type);
        Assert.Equal("1.2.3.4", main.Server);
        Assert.Equal("uuid-1", main.Uuid);

        var backup = config.Outbounds.First(o => o.Tag == "vless-backup");
        Assert.Equal("vless", backup.Type);
        Assert.Equal("5.6.7.8", backup.Server);
        Assert.Equal("uuid-2", backup.Uuid);
    }

    [Fact]
    public void AutoSelect_Off_MultiServer_ProxyIsSingleActive_NotUrltest()
    {
        var settings = CreateSettings(serverCount: 2);
        var profile = CreateProfile();

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.First(o => o.Tag == "proxy");
        Assert.Equal("vless", proxy.Type);
        Assert.Equal("1.2.3.4", proxy.Server);
        Assert.DoesNotContain(config.Outbounds, o => o.Type == "urltest");
    }

    [Fact]
    public void DnsFinal_IsLocalDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Equal("local-dns", config.Dns.Final);
    }

    [Fact]
    public void DnsServers_DefaultsEncryptDirectDnsAndTunnelVpnDns()
    {
        var settings = CreateSettings();
        var config = ConfigGenerator.Generate(CreateProfile(), new[] { "Discord.exe" }, settings);

        var local = config.Dns.Servers.Single(s => s.Tag == "local-dns");
        Assert.Equal("https", local.Type);
        Assert.Equal("8.8.8.8", local.Server);
        Assert.Equal("/dns-query", local.Path);
        Assert.Equal("dns-direct", local.Detour);

        var vpn = config.Dns.Servers.Single(s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpn.Type);
        Assert.Equal("proxy", vpn.Detour);
        Assert.DoesNotContain(config.Dns.Servers, s => s.Type == "udp" && s.Detour == "dns-direct");
    }

    [Fact]
    public void DnsRule_VpnOnly_UsesVpnDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "vpn_only");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var dnsRule = config.Dns.Rules.FirstOrDefault(r => r.ProcessName != null);
        Assert.NotNull(dnsRule);
        Assert.Equal("vpn-dns", dnsRule.Server);
    }

    [Fact]
    public void DnsRule_SmartMode_UsesLocalDns()
    {
        var settings = CreateSettings();
        var profile = CreateProfile(dnsMode: "smart");
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var dnsRule = config.Dns.Rules.FirstOrDefault(r => r.ProcessName != null);
        Assert.NotNull(dnsRule);
        Assert.Equal("local-dns", dnsRule.Server);
    }

    [Fact]
    public void ProcessNames_PreservesCase()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "Firefox.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var routeRule = config.Route.Rules.First(r => r.ProcessName != null);
        Assert.Contains("Discord.exe", routeRule.ProcessName!);
        Assert.Contains("Firefox.exe", routeRule.ProcessName!);
    }

    [Fact]
    public void ProcessNames_FiltersWildcards()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var processes = new[] { "Discord.exe", "chrome*", "fire?.exe" };

        var config = ConfigGenerator.Generate(profile, processes, settings);

        var routeRule = config.Route.Rules.First(r => r.ProcessName != null);
        Assert.Contains("Discord.exe", routeRule.ProcessName!);
        Assert.DoesNotContain("chrome*", routeRule.ProcessName!);
        Assert.DoesNotContain("fire?.exe", routeRule.ProcessName!);
    }

    [Fact]
    public void RouteRules_SniffRuleIsFirst()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var firstRule = config.Route.Rules[0];
        Assert.Equal("sniff", firstRule.Action);
        Assert.Equal("300ms", firstRule.Timeout);
    }

    [Fact]
    public void RouteRules_HijackDnsIsSecond()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var secondRule = config.Route.Rules[1];
        Assert.Equal("hijack-dns", secondRule.Action);
        Assert.Equal("dns", secondRule.Protocol);
    }

    [Fact]
    public void InboundTun_NoSniffFields()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        var json = ConfigGenerator.Serialize(config);
        Assert.DoesNotContain("sniff_override_destination", json);
        Assert.DoesNotContain("\"sniff\": true", json);
        Assert.DoesNotContain("\"sniff\": false", json);
    }

    [Fact]
    public void DirectOutbound_AlwaysPresent()
    {
        var settings = CreateSettings();
        var profile = CreateProfile();
        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);

        Assert.Contains(config.Outbounds, o => o.Tag == "direct" && o.Type == "direct");
    }
}
