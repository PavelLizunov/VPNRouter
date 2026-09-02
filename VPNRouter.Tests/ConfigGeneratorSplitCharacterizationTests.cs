using System.Text.Json;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class ConfigGeneratorSplitCharacterizationTests
{
    [Fact]
    public void AllFiftyMembersPreserved_AcrossSplitFilesOrMonolith()
    {
        var coreServicesDir = FindCoreServicesDirectory();
        var files = Directory.GetFiles(coreServicesDir, "ConfigGenerator*.cs")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(files);

        var allContent = string.Join("\n", files.Select(File.ReadAllText));

        var expectedMembers = new[]
        {
            "NormalizeTunMtu",
            "SelectTunStack",
            "AwgEndpointMtu",
            "ResolveEffectiveAppProcesses",
            "ComputeAppRoutingFingerprint",
            "Generate",
            "AdBlockRuleSetTag",
            "AdBlockRuleSetUrl",
            "AdBlockRuleSetFilename",
            "ApplyAdBlock",
            "ApplyCustomRules",
            "FindCustomRulesInsertionPoint",
            "BuildCustomRouteRule",
            "BuildCustomDnsRejectRule",
            "IsDomainTypeForDns",
            "MacHelperSuffixes",
            "MacKnownIoProcesses",
            "ExpandMacHelperNames",
            "EnsureCustomRuleSetEntry",
            "TryParsePortRange",
            "GeoIpRuleSetTag",
            "GeoSiteRuleSetTag",
            "ApplyGeoBypass",
            "SingBoxOptions",
            "Serialize",
            "PublicTldDenyList",
            "BuildDns",
            "BuildInbounds",
            "BuildOutbounds",
            "FindNaiveUdpSibling",
            "AddOutboundGroup",
            "BuildVlessOutbound",
            "BuildDnsTunnelOutbound",
            "SlipstreamProcessName",
            "ExtractResolverIps",
            "BuildVlessOutboundCore",
            "BuildHysteria2Outbound",
            "BuildAmneziaWgEndpoint",
            "BuildTuicOutbound",
            "BuildShadowsocksOutbound",
            "BuildNaiveOutbound",
            "ParseAlpnList",
            "BuildTransportConfig",
            "BuildTlsConfig",
            "BuildRoute",
            "BuildVpnDnsServer",
            "ToPlainDnsIp",
            "ParseDohHost",
            "ParseDohPort",
            "ParseDohPath"
        };

        foreach (var member in expectedMembers)
        {
            Assert.True(
                allContent.Contains(member, StringComparison.Ordinal),
                $"Expected member '{member}' was not found across ConfigGenerator source files.");
        }

        if (files.Count > 1)
        {
            var expectedFileNames = new[]
            {
                "ConfigGenerator.cs",
                "ConfigGenerator.Rules.cs",
                "ConfigGenerator.Dns.cs",
                "ConfigGenerator.Outbounds.cs",
                "ConfigGenerator.OutboundBuilders.cs",
                "ConfigGenerator.Route.cs"
            };

            var actualFileNames = files.Select(Path.GetFileName).ToList();
            foreach (var expected in expectedFileNames)
            {
                Assert.Contains(expected, actualFileNames);
            }

            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                Assert.Contains("public static partial class ConfigGenerator", text);
            }
        }
    }

    [Fact]
    public void Scenario1_SplitInclude_VlessDualOutbound_MatchesStructure()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = "include",
                BlockQuicOnTcpProxy = true
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "server-flow",
                        Server = "1.2.3.4",
                        Port = 443,
                        Uuid = "uuid-1",
                        Flow = "xtls-rprx-vision",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "key1", ShortId = "aa" }
                    },
                    new()
                    {
                        Name = "server-noflow",
                        Server = "5.6.7.8",
                        Port = 443,
                        Uuid = "uuid-2",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "key2", ShortId = "bb" }
                    }
                }
            }
        };

        var profile = new Profile
        {
            Name = "DefaultProfile",
            DnsMode = "vpn_only",
            Processes = new List<ProcessRule>
            {
                new() { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } }
            }
        };

        var config = ConfigGenerator.Generate(profile, new[] { "Discord.exe" }, settings);
        Assert.NotNull(config);

        Assert.Single(config.Inbounds);
        Assert.Equal("tun-in", config.Inbounds[0].Tag);
        Assert.Equal(OperatingSystem.IsMacOS() ? "gvisor" : "system", config.Inbounds[0].Stack);

        Assert.Contains(config.Outbounds, o => o.Tag == "proxy");
        Assert.Contains(config.Outbounds, o => o.Tag == "proxy-udp");
        Assert.Contains(config.Outbounds, o => o.Tag == "direct");
        Assert.Contains(config.Outbounds, o => o.Tag == "dns-direct");

        Assert.Equal("local-dns", config.Dns.Final);
        Assert.Contains(config.Dns.Rules, r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe") && r.Server == "vpn-dns");

        Assert.Equal("direct", config.Route.Final);
        Assert.Contains(config.Route.Rules, r => r.Protocol == "dns" && r.Action == "hijack-dns");
        Assert.Contains(config.Route.Rules, r => r.IpIsPrivate == true && r.Action == "route" && r.Outbound == "direct");
        Assert.Contains(config.Route.Rules, r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe") && r.Protocol == "quic" && r.Action == "reject");
        Assert.Contains(config.Route.Rules, r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe") && r.Network == "tcp" && r.Outbound == "proxy");
        Assert.Contains(config.Route.Rules, r => r.ProcessName != null && r.ProcessName.Contains("Discord.exe") && r.Network == "udp" && r.Outbound == "proxy-udp");

        var json = ConfigGenerator.Serialize(config);
        Assert.False(string.IsNullOrWhiteSpace(json));
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("route", out var routeEl));
        Assert.Equal("direct", routeEl.GetProperty("final").GetString());
    }

    [Fact]
    public void Scenario2_FullTunnel_Hysteria2_Brutal_BlockAds_MatchesStructure()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "full",
                BlockAds = true,
                CustomRulesPriority = "toggles_first"
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                ActiveServer = "hy2-node",
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "hy2-node",
                        Server = "9.9.9.9",
                        Port = 8443,
                        Protocol = "hysteria2",
                        Password = "secret-password",
                        ObfsType = "salamander",
                        ObfsPassword = "obfs-password",
                        HysteriaUpMbps = 50,
                        HysteriaDownMbps = 100
                    }
                }
            }
        };

        var profile = new Profile { Name = "FullProfile" };

        var config = ConfigGenerator.Generate(profile, Array.Empty<string>(), settings);
        Assert.NotNull(config);

        var proxyOutbound = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("hysteria2", proxyOutbound.Type);
        Assert.Equal(50, proxyOutbound.UpMbps);
        Assert.Equal(100, proxyOutbound.DownMbps);
        Assert.NotNull(proxyOutbound.Obfs);
        Assert.Equal("salamander", proxyOutbound.Obfs.Type);
        Assert.Equal("obfs-password", proxyOutbound.Obfs.Password);

        Assert.Equal("vpn-dns", config.Dns.Final);
        var vpnDns = Assert.Single(config.Dns.Servers, s => s.Tag == "vpn-dns");
        Assert.Equal("https", vpnDns.Type);
        Assert.Equal("dns.adguard-dns.com", vpnDns.Server);
        Assert.Equal(443, vpnDns.ServerPort);

        Assert.Equal("proxy", config.Route.Final);
    }

    [Fact]
    public void Scenario3_ExcludeMode_AmneziaWg_MatchesStructure()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = "exclude",
                RoutingAppsExclude = new List<string> { "chrome.exe" }
            },
            Tun = new TunSettings { Mtu = 1500 },
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                ActiveServer = "awg-node",
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "awg-node",
                        Server = "203.0.113.10",
                        Port = 51820,
                        Protocol = "amneziawg",
                        Awg = new AwgConfig
                        {
                            PrivateKey = "cHVibGljLWtleS0wMQ==",
                            PeerPublicKey = "cGVlci1rZXktMDE=",
                            Jc = 4,
                            Jmin = 40,
                            Jmax = 70,
                            S1 = 15,
                            S2 = 25,
                            H1 = "12345",
                            Address = new List<string> { "10.8.0.2/32" }
                        }
                    }
                }
            }
        };

        var profile = new Profile { Name = "ExcludeProfile" };

        var config = ConfigGenerator.Generate(profile, Array.Empty<string>(), settings);
        Assert.NotNull(config);

        Assert.NotNull(config.Endpoints);
        var endpoint = Assert.Single(config.Endpoints, e => e.Tag == "proxy");
        Assert.Equal("wireguard", endpoint.Type);
        Assert.Equal(1420, endpoint.Mtu);

        Assert.Single(config.Inbounds);
        Assert.Equal(1420, config.Inbounds[0].Mtu);

        Assert.Equal("vpn-dns", config.Dns.Final);
        Assert.Contains(config.Dns.Rules, r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe") && r.Server == "local-dns");

        Assert.Equal("proxy", config.Route.Final);
        Assert.Contains(config.Route.Rules, r => r.ProcessName != null && r.ProcessName.Contains("chrome.exe") && r.Outbound == "direct");
    }

    [Fact]
    public void Scenario4_ChainedDetour_CustomRulesCustomFirst_MatchesStructure()
    {
        var settings = new AppSettings
        {
            App = new AppConfig
            {
                LogLevel = "info",
                RoutingMode = "split",
                RoutingAppsMode = "include",
                CustomRulesPriority = "custom_first",
                CustomRules = new List<CustomRule>
                {
                    new() { Action = "block", Type = "domain", Value = "malicious.test", Enabled = true },
                    new() { Action = "direct", Type = "ip_cidr", Value = "192.168.1.0/24", Enabled = true }
                }
            },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                ActiveServer = "target-node",
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "upstream-node",
                        Server = "198.51.100.1",
                        Port = 443,
                        Uuid = "upstream-uuid",
                        Protocol = "vless",
                        OutboundId = "upstream-id",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "key1", ShortId = "11" }
                    },
                    new()
                    {
                        Name = "target-node",
                        Server = "198.51.100.2",
                        Port = 443,
                        Uuid = "target-uuid",
                        Protocol = "vless",
                        DetourVia = "upstream-id",
                        Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "key2", ShortId = "22" }
                    }
                }
            }
        };

        var profile = new Profile { Name = "ChainedProfile" };

        var config = ConfigGenerator.Generate(profile, new[] { "app.exe" }, settings);
        Assert.NotNull(config);

        var proxyOutbound = Assert.Single(config.Outbounds, o => o.Tag == "proxy");
        Assert.Equal("chain-entry", proxyOutbound.Detour);

        var chainEntry = Assert.Single(config.Outbounds, o => o.Tag == "chain-entry");
        Assert.Equal("198.51.100.1", chainEntry.Server);

        Assert.Contains(config.Dns.Rules, r => r.Action == "reject" && r.Domain != null && r.Domain.Contains("malicious.test"));
        Assert.Contains(config.Route.Rules, r => r.Action == "reject" && r.Domain != null && r.Domain.Contains("malicious.test"));
        Assert.Contains(config.Route.Rules, r => r.Action == "route" && r.Outbound == "direct" && r.IpCidr != null && r.IpCidr.Contains("192.168.1.0/24"));
    }

    private static string FindCoreServicesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate VPNRouter.Core/Services starting from {AppContext.BaseDirectory}");
    }
}
