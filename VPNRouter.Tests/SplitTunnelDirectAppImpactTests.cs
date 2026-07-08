using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Proof (2026-06-27) for the user's validation question: "can the VPN in
/// split mode affect apps that are supposed to go fully DIRECT?"
///
/// <para>Answer, demonstrated against the REAL generated sing-box config:
/// <b>YES</b>. VPNRouter's split-tunnel is "TUN captures everything → split at
/// the route/DNS layer inside sing-box", not "direct apps never touch the
/// tunnel". So traffic from a non-routed (direct) app still:
/// <list type="number">
///   <item>enters the TUN (auto_route default route, no route-include
///   restriction) and uses the same TUN MTU;</item>
///   <item>has its DNS HIJACKED (route rule protocol=dns → hijack-dns) and
///   resolved through Cloudflare DoH (local-dns), NOT the app's system/ISP/LAN
///   resolver — which breaks LAN/intranet name resolution (gap G6).</item>
/// </list></para>
///
/// <para>These are characterization tests: they pin TODAY's behaviour so the
/// claim is reproducible and any future fix (G6 split-DNS for LAN) flips them
/// deliberately. See plans/smart-connect-and-diag-followups-2026-06-26.md.</para>
/// </summary>
public class SplitTunnelDirectAppImpactTests
{
    // Routed apps (go through the proxy). A "direct app" is anything NOT here.
    private const string RoutedDiscord = "Discord.exe";
    private const string RoutedFirefox = "firefox.exe";
    private const string DirectApp = "chrome.exe"; // NOT routed → should be "fully direct"

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

    // ── Mechanism 1: ALL DNS is hijacked, regardless of app ──────────────────

    [Fact]
    public void SplitMode_HijacksAllDns_ViaProtocolDnsRule()
    {
        var cfg = GenerateSplit();

        // A single global route rule {protocol:dns, action:hijack-dns} captures
        // EVERY app's :53 DNS — there is no per-app exemption. So a direct app's
        // DNS is intercepted by sing-box just like a routed app's.
        Assert.Contains(cfg.Route.Rules,
            r => r.Protocol == "dns" && r.Action == "hijack-dns");
    }

    // ── Mechanism 1b: a direct app gets NO per-process DNS rule → it falls
    //    through to dns.final, which is Cloudflare DoH (not the system resolver).

    [Fact]
    public void SplitMode_DirectApp_HasNoPerProcessDnsRule()
    {
        var cfg = GenerateSplit();

        // Only the routed apps get per-process DNS rules; the direct app does not.
        var allProcessNamesInDnsRules = cfg.Dns.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!)
            .ToList();

        Assert.DoesNotContain(DirectApp, allProcessNamesInDnsRules);
        // sanity: the routed apps ARE covered (so the "direct app excluded" is
        // meaningful, not just an empty-rules artefact).
        Assert.Contains(RoutedDiscord, allProcessNamesInDnsRules);
    }

    [Fact]
    public void SplitMode_DirectAppDns_FallsThroughToCloudflareDoH_NotSystemResolver()
    {
        var cfg = GenerateSplit();

        // dns.final is where a direct app's (unmatched) lookups go.
        Assert.Equal("local-dns", cfg.Dns.Final);

        var localDns = cfg.Dns.Servers.Single(s => s.Tag == "local-dns");

        // It is Cloudflare DNS-over-HTTPS via the direct outbound — NOT a
        // type:"local" (getaddrinfo/system) server. So the direct app's DNS
        // never reaches its configured/ISP/LAN resolver.
        Assert.Equal("https", localDns.Type);
        Assert.Equal("1.1.1.1", localDns.Server);
        Assert.Equal("dns-direct", localDns.Detour);
        Assert.NotEqual("local", localDns.Type);
    }

    // ── Mechanism 1c (gap G6): no split-DNS path for LAN/intranet names ──────

    // G6 FIX (2026-06-27): this test previously pinned the GAP (no system
    // resolver => LAN names unresolvable). It now pins the FIX: LAN suffixes
    // route to a type:local system resolver, public domains stay on DoH.
    [Fact]
    public void SplitMode_LanSuffixes_RouteToSystemResolver_PublicStaysOnDoH()
    {
        var cfg = GenerateSplit();

        // A type:local (system) resolver now exists for LAN/mDNS names.
        var sys = cfg.Dns.Servers.SingleOrDefault(s => s.Type == "local");
        Assert.NotNull(sys);
        Assert.Equal("dns-system", sys!.Tag);

        // A DNS rule routes the private suffixes to it, and it PRECEDES the
        // per-process rules so a LAN name beats any per-process rule (geo/adblock
        // rule_set rules may sit ahead of it, but they don't match LAN suffixes).
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

        // Public domains do NOT match the suffix rule => still fall through to
        // dns.final = local-dns (Cloudflare DoH). No ISP leak for public names.
        Assert.Equal("local-dns", cfg.Dns.Final);
        Assert.DoesNotContain("com", lanRule.DomainSuffix!);
    }

    [Fact]
    public void SplitMode_StrictDns_SuppressesLanSplit_AllDnsViaVpn()
    {
        var settings = SplitSettings();
        settings.App.StrictDns = true;
        var cfg = ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord, RoutedFirefox }, settings);

        // StrictDns = user explicitly wants ALL DNS via the VPN; the LAN split
        // must be suppressed (LAN-name breakage is the documented tradeoff).
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
        Assert.Contains("corp", lanRule.DomainSuffix!);   // leading dot stripped
        Assert.Contains("home", lanRule.DomainSuffix!);
        Assert.Contains("local", lanRule.DomainSuffix!);  // built-ins preserved
    }

    [Fact]
    public void SplitMode_UserLanSuffix_BarePublicTld_IsRejected_NoLeak()
    {
        var settings = SplitSettings();
        // "com" bare would route ALL *.com to the system/ISP resolver — must be
        // refused. A specific multi-label internal domain is still allowed.
        settings.App.LanDnsSuffixes = new List<string> { "com", "corp.example.com" };
        var cfg = ConfigGenerator.Generate(SplitProfile(), new[] { RoutedDiscord }, settings);

        var lanRule = cfg.Dns.Rules.First(r => r.Server == "dns-system" && r.DomainSuffix != null);
        Assert.DoesNotContain("com", lanRule.DomainSuffix!);              // bare public TLD blocked
        Assert.Contains("corp.example.com", lanRule.DomainSuffix!);       // specific internal kept
        Assert.Contains("local", lanRule.DomainSuffix!);                  // built-ins intact
    }

    // ── Mechanism 2: the TUN captures non-local traffic (direct apps included) ─────

    [Fact]
    public void SplitMode_Tun_AutoRouteExcludesLocalNetworks_NoRouteInclude()
    {
        var cfg = GenerateSplit();

        var tun = cfg.Inbounds.Single(i => i.Type == "tun");

        // auto_route installs the default route; local/private ranges are excluded
        // before sing-box sees them.
        Assert.True(tun.AutoRoute);

        Assert.Equal(TunSettings.MandatoryLocalRouteExcludeAddress, tun.RouteExcludeAddress);

        // And they use the same TUN MTU as routed apps.
        Assert.Equal(TunSettings.DefaultMtu, tun.Mtu);
    }

    // ── Mechanism 3: direct apps exit via the direct outbound (route.final) ──
    //    — but per Mechanism 2 they still went THROUGH the TUN/sing-box first.

    [Fact]
    public void SplitMode_RouteFinalIsDirect_DirectAppExitsDirect_ButViaTun()
    {
        var cfg = GenerateSplit();

        Assert.Equal("direct", cfg.Route.Final);

        // The direct app has no process route rule pinning it to proxy.
        var routeProcRules = cfg.Route.Rules
            .Where(r => r.ProcessName != null)
            .SelectMany(r => r.ProcessName!);
        Assert.DoesNotContain(DirectApp, routeProcRules);
    }

    // ── Contrast: full tunnel differs, proving the above is split-specific ───

    // De-risk G6: the generated split config (type:local dns-system + LAN rule +
    // ipv4_only) must be valid sing-box JSON. Skips on CI without the binary.
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

        // Full tunnel: everything (incl. DNS) rides the proxy. Confirms the
        // split-mode local-dns/direct behaviour above is mode-specific, not a
        // constant of the generator.
        Assert.Equal("vpn-dns", cfg.Dns.Final);
        Assert.Equal("proxy", cfg.Route.Final);

        // hijack-dns is present in BOTH modes (DNS is always captured).
        Assert.Contains(cfg.Route.Rules, r => r.Protocol == "dns" && r.Action == "hijack-dns");
    }
}
