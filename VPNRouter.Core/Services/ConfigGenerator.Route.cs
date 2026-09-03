using System.IO;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    /// <summary>The slipstream-client executable basename that sing-box matches
    /// in process_name rules (platform-correct: "slipstream-client.exe" on
    /// Windows, "slipstream-client" elsewhere — dns-tunnel is Windows/Linux only).
    /// Used by <see cref="BuildRoute"/> to keep the slipstream front's own
    /// upstream traffic OUT of the tunnel.</summary>
    private static string SlipstreamProcessName => Path.GetFileName(AppPaths.SlipstreamExePath);

    // ─── Route (sing-box 1.12+ action-based format) ──────────────────────────

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes,
        string routingMode = "split", bool hasUdpProxy = false, bool isExcludeMode = false,
        bool blockQuicOnTcpProxy = true, bool isDnsTunnel = false,
        List<string>? dnsTunnelResolverIps = null, bool proxyIsUdpNative = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            // Protocol sniffing: detect HTTP/TLS/QUIC and override destination with sniffed domain.
            // Replaces deprecated inbound-level sniff + sniff_override_destination (removed in 1.13).
            new() { Action = "sniff", Timeout = "300ms" },
        };

        // DNS-tunnel (slipstream) self-exclusion — MUST precede hijack-dns AND the
        // proxy final. The slipstream-client front (127.0.0.1:7001) carries the
        // VLESS stream, but its OWN upstream packets to the DNS resolvers would
        // otherwise be (a) hijacked by the DNS module (they're DNS on :53) or
        // (b) routed to final=proxy=127.0.0.1:7001 = itself → deadlock
        // ("dial tcp 127.0.0.1:7001 i/o timeout", all DNS hangs, no internet).
        // Android excludes the whole app via VpnService; on Windows/Linux
        // slipstream is a SEPARATE process, so exclude it here by resolver-IP
        // (destination-based, reliable even before sniff) AND process_name
        // (covers DoH/DoT resolvers on non-:53 ports). IsInfrastructure keeps
        // FindCustomRulesInsertionPoint treating these as the leading block.
        if (isDnsTunnel)
        {
            if (dnsTunnelResolverIps is { Count: > 0 })
                rules.Add(new RouteRule
                {
                    IpCidr           = dnsTunnelResolverIps,
                    Action           = "route",
                    Outbound         = "direct",
                    IsInfrastructure = true,
                });
            rules.Add(new RouteRule
            {
                ProcessName      = new List<string> { SlipstreamProcessName },
                Action           = "route",
                Outbound         = "direct",
                IsInfrastructure = true,
            });
        }

        // DNS traffic: hijack and resolve through DNS module (replaces "dns" outbound)
        rules.Add(new RouteRule { Protocol = "dns", Action = "hijack-dns" });

        // Private IPs always direct — MUST be before process/default rules so that
        // traffic to local/VPN subnets (WireGuard, AmneziaWG, LAN) is never
        // sent through the remote proxy, in both split and full tunnel modes.
        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        // YouTube / QUIC fix: when the proxy is TCP-only (VLESS+Reality+Vision
        // with no UDP-capable TUIC/Hysteria2 sibling), QUIC (HTTP/3 over UDP/443)
        // tunneled over the reliable VLESS-over-TCP stream suffers head-of-line
        // blocking ("TCP-over-TCP meltdown") → YouTube/google-video stalls and
        // buffering. Because QUIC is slow-not-rejected, the browser keeps
        // retrying it instead of falling back. A clean reject forces the
        // fallback to HTTP/2-over-TCP, which rides VLESS cleanly. The sniff rule
        // above identifies QUIC; private-IP traffic is already routed direct, so
        // LAN QUIC is untouched. Skipped when a UDP-capable outbound exists
        // (proxy-udp) — there we honour the user's deliberate UDP routing. Also
        // skipped for a UDP-native tunnel (AmneziaWG / WireGuard endpoint): it
        // carries QUIC over real UDP, so there is no TCP-over-TCP meltdown to
        // pre-empt — rejecting QUIC would needlessly force HTTP/3 apps to TCP.
        if (blockQuicOnTcpProxy && !hasUdpProxy && !proxyIsUdpNative)
        {
            if (isFullTunnel || isExcludeMode)
            {
                // final = "proxy": (almost) all traffic rides the TCP-only proxy.
                rules.Add(new RouteRule { Protocol = "quic", Action = "reject" });
            }
            else if (processes.Count > 0)
            {
                // Split include: only the listed apps ride the TCP-only proxy,
                // so scope the QUIC reject to them — other apps keep QUIC direct.
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Protocol    = "quic",
                    Action      = "reject"
                });
            }
        }

        // AM-1: when the user is in exclude mode under split tunnel, the
        // listed processes get routed to "direct" (kept OUT of the
        // tunnel) and route.final flips to "proxy" so everything else
        // goes through the VPN. Otherwise we keep the legacy semantics:
        // include mode in split tunnel routes the listed processes
        // through proxy + final=direct; full tunnel routes everything
        // through proxy and ignores the per-app list.
        if (!isFullTunnel && processes.Count > 0)
        {
            var perAppOutbound = isExcludeMode ? "direct" : "proxy";
            if (hasUdpProxy && !isExcludeMode)
            {
                // Dual outbound only matters when sending through proxy;
                // for the exclude path the destination is always
                // "direct" so the TCP/UDP split is meaningless.
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "udp",
                    Action      = "route",
                    Outbound    = "proxy-udp"
                });
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Network     = "tcp",
                    Action      = "route",
                    Outbound    = "proxy"
                });
            }
            else
            {
                // Single outbound: listed traffic → proxy (include) or → direct (exclude)
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Outbound    = perAppOutbound
                });
            }
        }
        else if (isFullTunnel && hasUdpProxy)
        {
            // Full tunnel with UDP split: UDP → proxy-udp, TCP handled by Final
            rules.Add(new RouteRule
            {
                Network  = "udp",
                Action   = "route",
                Outbound = "proxy-udp"
            });
        }
        // Full tunnel without UDP split: no process-specific rules — Final = "proxy" handles everything

        // route.final defaults to "direct" in include mode (split), to
        // "proxy" in full tunnel OR exclude mode. In exclude split mode
        // the per-app rules above pin the user's exclude list to
        // direct, and the final rule sends everything else through the
        // VPN. Full tunnel always lands on proxy regardless of
        // isExcludeMode (no per-app filtering when everything is
        // tunnelled).
        string finalOutbound;
        if (isFullTunnel)
            finalOutbound = "proxy";
        else if (isExcludeMode)
            finalOutbound = "proxy";
        else
            finalOutbound = "direct";

        return new SingBoxRoute
        {
            Rules                   = rules,
            Final                   = finalOutbound,
            AutoDetectInterface     = true,
            // Required since sing-box 1.12, mandatory in 1.14
            DefaultDomainResolver   = "local-dns"
        };
    }
    // ─── vpn-dns resolver (tunnelled; Detour="proxy") ────────────────────────────
}
