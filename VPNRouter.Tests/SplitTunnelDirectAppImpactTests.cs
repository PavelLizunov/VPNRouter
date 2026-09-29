using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SplitTunnelDirectAppImpactTests
{
    private const string RoutedDiscord = "Discord.exe";
    private const string RoutedFirefox = "firefox.exe";
    private const string DirectApp = "chrome.exe";

    private static AppSettings SplitSettings()
    {
        return new AppSettings
        {
            App = new AppConfig { LogLevel = "info", RoutingMode = "split" },
            Tun = new TunSettings(),
            Dns = new DnsSettings { VpnDns = "https://1.1.1.1/dns-query" },
            SingBox = new SingBoxSettings(),
            Vless = new VlessConfig
            {
                Servers = new List<VlessServerEntry>
                {
                    new()
                    {
                        Name = "main", Server = "1.2.3.4", Port = 443,
                        Uuid = "b25684c3-90d6-454a-a911-4e0abba568b0",
                        Flow = "xtls-rprx-vision", Security = "reality",
                        Reality = new VlessRealityConfig
                        {
                            Enabled = true, ServerName = "www.microsoft.com", Fingerprint = "chrome",
                            PublicKey = "gDawCMB0X6iGXZkG8nZIFW5TaaW29x0DMzWijN-gc2A",
                            ShortId = "d86e92a0c6dd2271"
                        }
                    }
                }
            }
        };
    }

    private static Profile SplitProfile() => new()
    {
        Name = "SplitTest",
        DnsMode = "vpn_only",
        Processes = new List<ProcessRule>
        {
            new() { Name = RoutedDiscord, ScanPatterns = new[] { RoutedDiscord } },
            new() { Name = RoutedFirefox, ScanPatterns = new[] { RoutedFirefox } },
        }
    };

    private static SingBoxConfig GenerateSplit()
        => ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord, RoutedFirefox }, SplitSettings());

    [Fact]
    public void SplitMode_HijacksAllDns_ViaProtocolDnsRule()
    {
        var cfg = GenerateSplit();

        Assert.Contains(cfg.Route.Rules,
            r => r.Protocol == "dns" && r.Action == "hijack-dns");
    }

    [Fact]
    public void SplitMode_DirectApp_HasNoPerProcessDnsRule()
    {
        var cfg = GenerateSplit();

        var allProcessNamesInDnsRules = cfg.Dns.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .ToList();

        Assert.DoesNotContain(DirectApp, allProcessNamesInDnsRules);
        Assert.Contains(RoutedDiscord, allProcessNamesInDnsRules);
    }

    [Fact]
    public void SplitMode_DirectAppDns_FallsThroughToCloudflareDoH_NotSystemResolver()
    {
        var cfg = GenerateSplit();

        Assert.Equal("local-dns", cfg.Dns.Final);

        var localDns = cfg.Dns.Servers.Single(s => s.Tag == "local-dns");

        Assert.Equal("https", localDns.Type);
        Assert.Equal("8.8.8.8", localDns.Server);
        Assert.Equal("dns-direct", localDns.Detour);
        Assert.NotEqual("local", localDns.Type);
    }

    [Fact]
    public void SplitMode_LanSuffixes_RouteToSystemResolver_PublicStaysOnDoH()
    {
        var cfg = GenerateSplit();

        var sys = cfg.Dns.Servers.SingleOrDefault(s => s.Type == "local");
        Assert.NotNull(sys);
        Assert.Equal("dns-system", sys!.Tag);

        var lanIdx = cfg.Dns.Rules.FindIndex(r => r.Server == "dns-system" && r.DomainSuffix != null);
        Assert.True(lanIdx >= 0, "LAN split-DNS rule must exist");
        var procIdx = cfg.Dns.Rules.FindIndex(r => r.ProcessName != null);
        if (procIdx >= 0)
            Assert.True(lanIdx < procIdx, "LAN rule must precede per-process DNS rules");
        var lanRule = cfg.Dns.Rules[lanIdx];
        Assert.Contains("local", lanRule.DomainSuffix!);
        Assert.Contains("lan", lanRule.DomainSuffix!);
        Assert.Contains("home.arpa", lanRule.DomainSuffix!);
        Assert.Contains("internal", lanRule.DomainSuffix!);

        Assert.Equal("local-dns", cfg.Dns.Final);
        Assert.DoesNotContain("com", lanRule.DomainSuffix!);
    }

    [Fact]
    public void SplitMode_StrictDns_SuppressesLanSplit_AllDnsViaVpn()
    {
        var settings = SplitSettings();
        settings.App.StrictDns = true;
        var cfg = ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord, RoutedFirefox }, settings);

        Assert.DoesNotContain(cfg.Dns.Servers, s => s.Type == "local");
        Assert.DoesNotContain(cfg.Dns.Rules, r => r.Server == "dns-system");
        Assert.Equal("vpn-dns", cfg.Dns.Final);
    }

    [Fact]
    public void SplitMode_UserLanSuffixes_AreIncluded_DotStripped()
    {
        var settings = SplitSettings();
        settings.App.LanDnsSuffixes = new List<string> { ".corp", "home" };
        var cfg = ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord }, settings);

        var lanRule = cfg.Dns.Rules.First(r => r.Server == "dns-system" && r.DomainSuffix != null);
        Assert.Contains("corp", lanRule.DomainSuffix!);
        Assert.Contains("home", lanRule.DomainSuffix!);
        Assert.Contains("local", lanRule.DomainSuffix!);
    }

    [Fact]
    public void SplitMode_UserLanSuffix_BarePublicTld_IsRejected_NoLeak()
    {
        var settings = SplitSettings();
        settings.App.LanDnsSuffixes = new List<string> { "com", "corp.example.com" };
        var cfg = ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord }, settings);

        var lanRule = cfg.Dns.Rules.First(r => r.Server == "dns-system" && r.DomainSuffix != null);
        Assert.DoesNotContain("com", lanRule.DomainSuffix!);
        Assert.Contains("corp.example.com", lanRule.DomainSuffix!);
        Assert.Contains("local", lanRule.DomainSuffix!);
    }

    [Fact]
    public void SplitMode_Tun_AutoRouteExcludesLocalNetworks_NoRouteInclude()
    {
        var cfg = GenerateSplit();

        var tun = cfg.Inbounds.Single(i => i.Type == "tun");

        Assert.True(tun.AutoRoute);

        Assert.Equal(TunSettings.MandatoryLocalRouteExcludeAddress, tun.RouteExcludeAddress);

        Assert.Equal(TunSettings.DefaultMtu, tun.Mtu);
    }

    [Fact]
    public void SplitMode_RouteFinalIsDirect_DirectAppExitsDirect_ButViaTun()
    {
        var cfg = GenerateSplit();

        Assert.Equal("direct", cfg.Route.Final);

        var routeProcRules = cfg.Route.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!);
        Assert.DoesNotContain(DirectApp, routeProcRules);
    }

    [Fact]
    public void SplitWithLanDns_PassesSingBoxCheck()
    {
        var singBox = @"C:\ProgramData\VPNRouter\bin\sing-box.exe";
        if (!System.IO.File.Exists(singBox)) return;

        var json = ConfigGenerator.Serialize(GenerateSplit());
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"vpnrouter-lan-dns-{System.Guid.NewGuid()}.json");
        try
        {
            System.IO.File.WriteAllText(tmp, json);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = singBox, Arguments = $"check -c \"{tmp}\"",
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            Assert.True(p.ExitCode == 0,
                $"sing-box check failed on split+LAN-DNS config (exit {p.ExitCode}):\n{err}\n\n{json}");
        }
        finally { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); }
    }

    [Fact]
    public void Contrast_FullTunnel_DnsFinalIsVpnDns_RouteFinalIsProxy()
    {
        var settings = SplitSettings();
        settings.App.RoutingMode = "full";

        var cfg = ConfigGenerator.Generate(SplitProfile(), Array.Empty<string>(), settings);

        Assert.Equal("vpn-dns", cfg.Dns.Final);
        Assert.Equal("proxy", cfg.Route.Final);

        Assert.Contains(cfg.Route.Rules, r => r.Protocol == "dns" && r.Action == "hijack-dns");
    }
}
