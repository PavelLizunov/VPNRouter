using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class ConfigGeneratorDnsTunnelTests
{
    private const string Uuid = "11111111-1111-1111-1111-111111111111";

    private static Profile DiscordProfile() => new()
    {
        Name = "T",
        DnsMode = "vpn_only",
        Processes = new() { new ProcessRule { Name = "Discord.exe", ScanPatterns = new[] { "Discord.exe" } } }
    };

    private static AppSettings DnsTunnelSettings() => new()
    {
        App = new AppConfig
        {
            LogLevel = "info",
            ConfigMode = "subscribe",
            ActiveSubscriptionServer = "Emergency",
            Subscriptions = new List<SubscriptionEntry>
            {
                new()
                {
                    Name = "dt-sub",
                    Url = "https://example.com",
                    Enabled = true,
                    Servers = new List<VlessServerEntry>
                    {
                        new()
                        {
                            Protocol = "dns-tunnel",
                            Name = "Emergency",
                            Server = "tunnel.example.org",
                            DnsDomain = "tunnel.example.org",
                            DnsResolvers = new List<string> { "195.208.4.1:53", "195.208.5.1:53" },
                            DnsLeafCertPem = "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----",
                            Uuid = Uuid,
                        }
                    }
                }
            }
        },
        Tun = new TunSettings { InterfaceName = "VPNRouter-TUN", Ipv4Address = "172.19.0.1/30", Mtu = 9000, AutoRoute = true, StrictRoute = false },
        Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query", Strategy = "ipv4_only" },
        SingBox = new SingBoxSettings { ClashApi = "127.0.0.1:9090" },
        Vless = new VlessConfig()
    };

    [Fact]
    public void Generate_DnsTunnelServer_ProxyTargetsLocalSlipstreamPort_NoTls()
    {
        var settings = DnsTunnelSettings();
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        var proxy = config.Outbounds.FirstOrDefault(o => o.Tag == "proxy");
        Assert.NotNull(proxy);
        Assert.Equal("vless", proxy!.Type);
        Assert.Equal("127.0.0.1", proxy.Server);
        Assert.Equal(SlipstreamManager.DefaultLocalPort, proxy.ServerPort);
        Assert.Equal(Uuid, proxy.Uuid);
        Assert.Null(proxy.Tls);
        Assert.True(string.IsNullOrEmpty(proxy.Flow));
    }

    [Fact]
    public void Generate_DnsTunnel_FullTunnel_ExcludesSlipstreamBeforeHijackDns()
    {
        var settings = DnsTunnelSettings();
        settings.App.RoutingMode = "full";
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        var rules = config.Route.Rules;
        int hijackIdx = rules.FindIndex(r => r.Action == "hijack-dns");
        Assert.True(hijackIdx >= 0, "hijack-dns rule must exist");

        int ipIdx = rules.FindIndex(r =>
            r.Action == "route" && r.Outbound == "direct" && r.IpCidr != null &&
            r.IpCidr.Contains("195.208.4.1") && r.IpCidr.Contains("195.208.5.1"));
        Assert.True(ipIdx >= 0, "resolver-IP exclusion rule must exist");
        Assert.True(ipIdx < hijackIdx, "resolver-IP exclusion must precede hijack-dns");

        int procIdx = rules.FindIndex(r =>
            r.Action == "route" && r.Outbound == "direct" && r.ProcessName != null &&
            r.ProcessName.Any(p => p.StartsWith("slipstream-client", System.StringComparison.Ordinal)));
        Assert.True(procIdx >= 0, "slipstream process_name exclusion must exist");
        Assert.True(procIdx < hijackIdx, "process_name exclusion must precede hijack-dns");

        Assert.Equal("proxy", config.Route.Final);
    }

    [Fact]
    public void Generate_DnsTunnel_FullTunnel_ExcludesAuthoritativeEndpointToo()
    {
        var settings = DnsTunnelSettings();
        settings.App.RoutingMode = "full";
        settings.App.Subscriptions[0].Servers[0].DnsAuthoritative =
            new List<string> { "213.155.15.93:53" };
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        int hijackIdx = config.Route.Rules.FindIndex(r => r.Action == "hijack-dns");
        var ipRule = config.Route.Rules.FirstOrDefault(r =>
            r.Action == "route" && r.Outbound == "direct" && r.IpCidr != null &&
            r.IpCidr.Contains("213.155.15.93"));
        Assert.NotNull(ipRule);
        Assert.Contains("195.208.4.1", ipRule!.IpCidr!);
        Assert.True(config.Route.Rules.IndexOf(ipRule) < hijackIdx,
            "authoritative exclusion must precede hijack-dns");
    }

    [Fact]
    public void Generate_DnsTunnel_ResolverIpExtraction_SkipsHostnames_HandlesIpv6()
    {
        var settings = DnsTunnelSettings();
        settings.App.Subscriptions[0].Servers[0].DnsResolvers = new List<string>
        {
            "195.208.4.1:53",
            "dns.example.com:53",
            "[2001:db8::1]:853",
            "9.9.9.9",
        };
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        var ipRule = config.Route.Rules.FirstOrDefault(r =>
            r.Action == "route" && r.Outbound == "direct" && r.IpCidr != null &&
            r.IpCidr.Contains("195.208.4.1"));
        Assert.NotNull(ipRule);
        Assert.Contains("195.208.4.1", ipRule!.IpCidr!);
        Assert.Contains("2001:db8::1", ipRule.IpCidr!);
        Assert.Contains("9.9.9.9", ipRule.IpCidr!);
        Assert.DoesNotContain("dns.example.com", ipRule.IpCidr!);
    }

    [Fact]
    public void Generate_NormalVless_NoSlipstreamExclusion()
    {
        var settings = DnsTunnelSettings();
        settings.App.Subscriptions[0].Servers = new List<VlessServerEntry>
        {
            new() { Protocol = "vless", Name = "Normal", Server = "vless.example.org", Port = 443, Uuid = Uuid }
        };
        settings.App.ActiveSubscriptionServer = "Normal";
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);

        Assert.DoesNotContain(config.Route.Rules, r =>
            r.ProcessName != null &&
            r.ProcessName.Any(p => p.StartsWith("slipstream-client", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void Generate_DnsTunnel_FullTunnel_PassesSingBoxCheck()
    {
        const string singBoxPath = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!File.Exists(singBoxPath)) return;

        var settings = DnsTunnelSettings();
        settings.App.RoutingMode = "full";
        Assert.Single(VlessServersResolver.Resolve(settings));
        var config = ConfigGenerator.Generate(DiscordProfile(), new[] { "Discord.exe" }, settings);
        var json = ConfigGenerator.Serialize(config);

        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-dnstunnel-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempPath, json);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBoxPath,
                Arguments = $"check -c \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            Assert.True(proc.ExitCode == 0,
                $"sing-box check failed on dns-tunnel config (exit {proc.ExitCode}):\n{stderr}\n\nConfig:\n{json}");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
