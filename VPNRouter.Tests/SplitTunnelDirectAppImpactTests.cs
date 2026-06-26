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
///   restriction) and is clamped to the 1280 TUN MTU;</item>
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
                        Name = "main", Server = "1.2.3.4", Port = 443, Uuid = "u",
                        Flow = "xtls-rprx-vision", Security = "reality",
                        Reality = new VlessRealityConfig { PublicKey = "k", ShortId = "ab" }
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

    [Fact]
    public void SplitMode_HasNoLocalSystemResolver_SoLanNamesCannotResolve()
    {
        var cfg = GenerateSplit();

        // The ONLY DNS servers are vpn-dns (DoH via proxy) and local-dns (DoH
        // via direct). Neither is type:"local" (the system resolver that could
        // answer LAN/mDNS/intranet names). And no DNS rule routes private/LAN
        // domains to such a resolver. => a direct app resolving "nas.local"
        // hits Cloudflare DoH, which cannot answer it. This is gap G6.
        Assert.DoesNotContain(cfg.Dns.Servers, s => s.Type == "local");
        Assert.DoesNotContain(cfg.Dns.Rules,
            r => r.Server != null && r.Server.Contains("local", StringComparison.OrdinalIgnoreCase)
                 && (r.DomainSuffix != null || r.Domain != null));
    }

    // ── Mechanism 2: the TUN captures ALL traffic (direct apps included) ─────

    [Fact]
    public void SplitMode_Tun_AutoRouteCapturesEverything_NoRouteInclude()
    {
        var cfg = GenerateSplit();

        var tun = cfg.Inbounds.Single(i => i.Type == "tun");

        // auto_route installs the default route → ALL traffic enters the TUN.
        Assert.True(tun.AutoRoute);

        // There is NO route-INCLUDE restriction in the model — only
        // route_exclude_address (WG coexistence), which is null here. So nothing
        // limits capture to "only routed apps"; direct apps traverse the TUN.
        Assert.Null(tun.RouteExcludeAddress);

        // And they are clamped to the 1280 TUN MTU, same as routed apps.
        Assert.Equal(1280, tun.Mtu);
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
