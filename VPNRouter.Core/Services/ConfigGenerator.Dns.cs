using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    // ─── DNS (sing-box 1.12+ format) ──────────────────────────────────────────

    /// <summary>
    /// Common public TLDs refused as bare LAN suffixes (G6 leak guard) — adding
    /// one would route every lookup under it to the system/ISP resolver in
    /// plaintext. Not exhaustive; just the obvious foot-guns.
    /// </summary>
    private static readonly HashSet<string> PublicTldDenyList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "net", "org", "io", "co", "dev", "app", "ru", "info", "biz",
            "online", "xyz", "me", "tv", "cc", "us", "uk", "de", "edu", "gov"
        };

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings, bool isExcludeMode = false, bool? strictDnsOverride = null, bool proxyIsUdpNative = false)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        // v2.42.0 StrictDns runtime failover: HealthMonitor can pass
        // strictDnsOverride=false to suppress "all DNS via tunnel" when the
        // proxy is unreachable (germany endless-loading). null = honour the
        // persisted setting. Full-tunnel / exclude mode still force vpn-dns
        // regardless — there StrictDns isn't the sole driver and all traffic
        // legitimately rides the tunnel. See StrictDnsFailoverPolicy.
        var strictDns = strictDnsOverride ?? settings.App.StrictDns;

        // AM-1: in exclude mode `processes` holds the apps we are KEEPING
        // direct, so route.final flips to "proxy". The DNS default
        // mirrors that: by default DNS goes through the VPN; only the
        // listed exclude-apps get the local resolver (so they don't leak
        // their queries inside the tunnel when they're not even using
        // it). StrictDns and Full tunnel keep their existing semantics
        // (override to vpn-dns).
        var defaultVpnDns = isFullTunnel || isExcludeMode || strictDns;

        var dns = new SingBoxDns
        {
            // ipv4_only protects from IPv6 leaks (when VPN tunnels only IPv4) AND
            // skips slow AAAA queries (+100-300ms each). Disable only if user
            // explicitly wants IPv6 via dns.strategy in config.yaml.
            // G5 (2026-06-27): also force ipv4_only whenever the TUN itself carries
            // no IPv6 — an AAAA answer can't traverse an IPv4-only tunnel, so
            // skipping it avoids the "address not valid in its context" dial-fails
            // and the per-query stall, independent of ForceIpv4Only.
            Strategy = (settings.App.ForceIpv4Only || !settings.Tun.Ipv6Enabled) ? "ipv4_only" : null,
            // Strict DNS: all queries via VPN (no leaks possible).
            // Full tunnel: all DNS through VPN by default.
            // Exclude mode (AM-1): unmatched apps go via VPN, so DNS final = vpn-dns.
            // Include mode split tunnel: unmatched apps go direct, so DNS final = local-dns.
            Final = defaultVpnDns ? "vpn-dns" : "local-dns",
            Servers = new List<DnsServer>
            {
                // Tunnelled resolver (Detour="proxy"). DoH over a TCP tunnel, plain
                // UDP over a UDP-native (AmneziaWG) tunnel — see BuildVpnDnsServer.
                BuildVpnDnsServer(settings, proxyIsUdpNative),
                // Local DNS — Cloudflare DoH via dns-direct outbound (real NIC).
                // type:local would call getaddrinfo() → system resolver → ISP DNS,
                // which leaks queries to ISP for any process not in the routed list
                // (e.g. Windows DnsCache svchost.exe). DoH via Cloudflare hides queries.
                new()
                {
                    Tag        = "local-dns",
                    Type       = "https",
                    Server     = "1.1.1.1",
                    Path       = "/dns-query",
                    Detour     = "dns-direct"
                }
            },
            Rules = new List<DnsRule>()
        };

        // G6 (2026-06-27): split-DNS for private / LAN domains. Without this,
        // sing-box's blanket DNS hijack (route protocol=dns -> hijack-dns) sends
        // EVERY app's lookups — including DIRECT (non-routed) apps in split
        // tunnel — to the remote DoH (local-dns / vpn-dns), which cannot answer
        // LAN names (nas.local, printer.lan). Route private suffixes to the
        // SYSTEM resolver instead; public domains don't match and fall through to
        // dns.final unchanged (no ISP leak). Suppressed under StrictDns (the user
        // opted into all-DNS-via-VPN, accepting LAN breakage).
        if (settings.App.ResolveLanViaSystemDns && !strictDns)
        {
            dns.Servers.Add(new DnsServer
            {
                Tag  = "dns-system",
                Type = "local" // OS resolver — knows LAN/mDNS names, bypasses TUN
            });

            var lanSuffixes = new List<string> { "local", "lan", "home.arpa", "internal" };
            if (settings.App.LanDnsSuffixes != null)
            {
                foreach (var s in settings.App.LanDnsSuffixes)
                {
                    var t = s?.Trim().TrimStart('.');
                    if (string.IsNullOrEmpty(t)) continue;
                    // Leak guard (review nit, 2026-06-27): a bare PUBLIC TLD as a
                    // LAN suffix would route every lookup under it to the system /
                    // ISP resolver in plaintext — the exact leak DoH prevents.
                    // Refuse single-label public TLDs; legit private suffixes
                    // ("corp", "lan") and specific multi-label internal domains
                    // ("corp.example.com") are still allowed.
                    if (!t!.Contains('.') && PublicTldDenyList.Contains(t))
                        continue;
                    if (!lanSuffixes.Contains(t, StringComparer.OrdinalIgnoreCase))
                        lanSuffixes.Add(t);
                }
            }

            // LAN rule precedes the per-process rules (so a LAN name beats them);
            // adblock/reject rules may still Insert(0) ahead — correct, reject wins.
            dns.Rules.Add(new DnsRule
            {
                DomainSuffix = lanSuffixes,
                Action       = "route",
                Server       = "dns-system"
            });
        }

        if (isFullTunnel)
        {
            // Full tunnel: all DNS goes through vpn-dns (via Final above).
            // No per-process rules needed. (The T4 "resolve game domains off-proxy"
            // band-aid was removed 2026-07-02: the DNS root cause is fixed — plain-UDP
            // vpn-dns, not congested DoH — and it broke StrictDns by sending game DNS
            // to the real NIC. Dota's failure is WSAENOBUFS, not DNS; see
            // plans/goal-codex-awg-games-dns-comprehensive-2026-07-02.md.)
        }
        else if (isExcludeMode)
        {
            // Exclude mode: listed apps must resolve their queries via
            // the local resolver so the lookups don't leak into the
            // tunnel they're explicitly bypassing. profile.DnsMode is
            // irrelevant here (it's a property of the legacy profile
            // system); we mirror routing intent on the DNS layer.
            // Under StrictDns, the all-DNS-via-VPN guarantee overrides this.
            if (processes.Count > 0)
            {
                var dnsServer = strictDns ? "vpn-dns" : "local-dns";
                dns.Rules.Add(new DnsRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Server      = dnsServer
                });
            }
        }
        else
        {
            // Include split tunnel: the targeted processes are routed through the
            // proxy, so their DNS MUST resolve through the tunnel too — otherwise
            // their lookups fall through to dns.final = local-dns (real NIC) and the
            // resolver sees the user's real IP for exactly the app they routed for
            // privacy. v2.40.0-r9 (#1 core-audit HIGH): dns_mode="direct" previously
            // SKIPPED this rule entirely (`profile.DnsMode != "direct"` guard) →
            // silent DNS leak, one-click reachable via the shipped Privacy_Shell
            // profile. Now a routed process ALWAYS gets a per-process DNS rule:
            //   smart   → local-dns (the explicit "tunnel traffic, local DoH for
            //             geo-CDN nearness" opt-in; an encrypted-DoH tradeoff),
            //   vpn_only / direct / anything else → vpn-dns (tunnel the DNS).
            // Under StrictDns, the all-DNS-via-VPN guarantee overrides smart mode.
            if (processes.Count > 0)
            {
                var dnsServer = strictDns || profile.DnsMode != "smart" ? "vpn-dns" : "local-dns";
                dns.Rules.Add(new DnsRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Server      = dnsServer
                });
            }
        }

        return dns;
    }


    /// <summary>
    /// The resolver whose queries ride the proxy tunnel. For a TCP tunnel
    /// (VLESS/Reality) we use DoH — extra privacy from the exit, and TCP MSS
    /// auto-clamps so the TLS handshake survives the path. For a UDP-native tunnel
    /// (AmneziaWG/WireGuard) the DoH TLS handshake's large ServerHello flight
    /// blackholes on the fixed 1280 endpoint MTU (diag 20260701-122336: cold DoH
    /// exchanges 12-56s -> Dota region pings time out), so we resolve via PLAIN UDP
    /// inside the already-encrypted tunnel: one small packet each way, no handshake,
    /// no DoH-hostname bootstrap, leak-safe (never leaves the tunnel). AdGuard's
    /// plain-DNS IP keeps ad-blocking when BlockAds is on.
    /// </summary>
    private static DnsServer BuildVpnDnsServer(AppSettings settings, bool proxyIsUdpNative)
    {
        if (proxyIsUdpNative)
        {
            return new DnsServer
            {
                Tag    = "vpn-dns",
                Type   = "udp",
                // AdGuard "Default" plain-DNS (ad + tracker + malware blocking) when
                // BlockAds is on; else the user's VPN DNS reduced to a literal IP.
                Server = settings.App.BlockAds ? "94.140.14.14" : ToPlainDnsIp(settings.Dns.VpnDns),
                Detour = "proxy"
            };
        }

        // Remote DoH server routed through VPN proxy.
        // When BlockAds is on, use AdGuard DNS (blocks ads + trackers + malware).
        // Otherwise use user-configured VPN DNS.
        return new DnsServer
        {
            Tag        = "vpn-dns",
            Type       = "https",
            Server     = settings.App.BlockAds ? "dns.adguard-dns.com" : ParseDohHost(settings.Dns.VpnDns),
            ServerPort = settings.App.BlockAds ? 443 : ParseDohPort(settings.Dns.VpnDns),
            Path       = settings.App.BlockAds ? "/dns-query" : ParseDohPath(settings.Dns.VpnDns),
            Detour     = "proxy",
            // Bootstrap the DoH hostname without asking vpn-dns to resolve
            // itself. The DoH exchange still rides the proxy via Detour.
            DomainResolver = "local-dns"
        };
    }

    /// <summary>
    /// Reduce a DoH URL to a literal IP for plain-UDP DNS (which cannot bootstrap
    /// a hostname over the tunnel without a loop). Falls back to Cloudflare 1.1.1.1
    /// when the configured VPN DNS is a hostname rather than an IP literal.
    /// </summary>
    private static string ToPlainDnsIp(string dohUrl)
    {
        var host = ParseDohHost(dohUrl);
        return System.Net.IPAddress.TryParse(host, out _) ? host : "1.1.1.1";
    }

    // ─── DoH URL parsing helpers ──────────────────────────────────────────────────

    /// <summary>Extract hostname from a DoH URL like https://1.1.1.1/dns-query</summary>
    private static string ParseDohHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return url; // fallback: return as-is
    }

    /// <summary>Extract port from a DoH URL (default 443 for https)</summary>
    private static int? ParseDohPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Port > 0 && !uri.IsDefaultPort)
                return uri.Port;
            return null; // let sing-box use default
        }
        return null;
    }

    /// <summary>Extract path from a DoH URL (e.g. /dns-query)</summary>
    private static string ParseDohPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.IsNullOrEmpty(uri.AbsolutePath) ? "/dns-query" : uri.AbsolutePath;
        return "/dns-query";
    }

}
