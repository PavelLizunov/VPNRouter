using VPNRouter.Core.Models;
using Newtonsoft.Json;

namespace VPNRouter.Core.Services;

/// <summary>
/// Generates sing-box 1.12+ compatible JSON config.
///
/// Migration from legacy API:
/// - DNS: uses new type-based server format (type: remote/local)
/// - DNS rules: uses action-based format (action: route/reject)
/// - Route: no more "dns" or "block" outbound types — uses action: "hijack-dns" and action: "reject"
/// </summary>
public static class ConfigGenerator
{
    public static SingBoxConfig Generate(
        Profile profile,
        IEnumerable<string> resolvedProcessNames,
        AppSettings settings)
    {
        // Filter out wildcard patterns — sing-box process_name doesn't support globs
        // Only pass exact .exe names (no * or ?)
        // Preserve original case — sing-box process_name matching is case-sensitive
        // (Go map lookup against filepath.Base from QueryFullProcessImageName)
        var processes = resolvedProcessNames
            .Where(p => !p.Contains('*') && !p.Contains('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var logPath = Environment.ExpandEnvironmentVariables(
            @"%ProgramData%\VPNRouter\logs\singbox.log");

        var outbounds = BuildOutbounds(settings, out bool hasUdpProxy);

        var config = new SingBoxConfig
        {
            Log = new SingBoxLog
            {
                Level = settings.App.LogLevel,
                Timestamp = true,
                Output = logPath
            },
            Dns = BuildDns(profile, processes, settings),
            Inbounds = BuildInbounds(settings),
            Outbounds = outbounds,
            Route = BuildRoute(profile, processes, settings.App.RoutingMode, hasUdpProxy),
            Experimental = new SingBoxExperimental()
        };

        return config;
    }

    public static string Serialize(SingBoxConfig config)
    {
        return JsonConvert.SerializeObject(config, new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        });
    }

    // ─── DNS (sing-box 1.12+ format) ──────────────────────────────────────────

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        var dns = new SingBoxDns
        {
            Strategy = "ipv4_only",
            // Full tunnel: all DNS through VPN by default
            // Split tunnel: only targeted processes use VPN DNS, rest use local
            Final = isFullTunnel ? "vpn-dns" : "local-dns",
            Servers = new List<DnsServer>
            {
                // Remote DoH server routed through VPN proxy
                // sing-box 1.12+: type=https uses server/server_port/path instead of address URL
                new()
                {
                    Tag        = "vpn-dns",
                    Type       = "https",
                    Server     = ParseDohHost(settings.Dns.VpnDns),
                    ServerPort = ParseDohPort(settings.Dns.VpnDns),
                    Path       = ParseDohPath(settings.Dns.VpnDns),
                    Detour     = "proxy"
                },
                // Local system DNS — direct
                new()
                {
                    Tag  = "local-dns",
                    Type = "local"
                    // No address needed for type=local
                }
            },
            Rules = new List<DnsRule>()
        };

        if (isFullTunnel)
        {
            // Full tunnel: all DNS goes through vpn-dns (via Final above).
            // No per-process rules needed.
        }
        else
        {
            // Split tunnel: targeted processes → VPN DNS (leak protection)
            if (processes.Count > 0 && profile.DnsMode != "direct")
            {
                var dnsServer = profile.DnsMode == "smart" ? "local-dns" : "vpn-dns";
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

    // ─── Inbounds ─────────────────────────────────────────────────────────────

    private static List<SingBoxInbound> BuildInbounds(AppSettings settings)
    {
        return new List<SingBoxInbound>
        {
            new()
            {
                Type                    = "tun",
                Tag                     = "tun-in",
                InterfaceName           = settings.Tun.InterfaceName,
                Address                 = new List<string> { settings.Tun.Ipv4Address },
                Mtu                     = settings.Tun.Mtu,
                AutoRoute               = settings.Tun.AutoRoute,
                StrictRoute             = false, // Always false — avoid dual stack errors
                RouteExcludeAddress     = settings.Tun.RouteExcludeAddress.Count > 0
                                            ? settings.Tun.RouteExcludeAddress
                                            : null,
                EndpointIndependentNat  = false,
                Stack                   = "system"
                // sniff + sniff_override_destination removed — deprecated since 1.11
                // Sniffing now handled by route rule: action="sniff"
            }
        };
    }

    // ─── Outbounds ────────────────────────────────────────────────────────────
    // sing-box 1.12+: removed "dns" and "block" outbound types
    // DNS hijacking is done via route rule action: "hijack-dns"
    // Blocking is done via route rule action: "reject"

    /// <summary>
    /// Build outbound list. When any server has a flow setting (e.g. xtls-rprx-vision),
    /// also creates a "proxy-udp" outbound without flow for UDP traffic (voice/video).
    /// xtls-rprx-vision is TCP-only; UDP through it adds latency and may trigger DPI.
    /// </summary>
    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings, out bool hasUdpProxy)
    {
        var servers = settings.Vless.GetEffectiveServers();
        var outbounds = new List<SingBoxOutbound>();

        // Detect if any server uses flow — if so, we need a UDP proxy without flow
        bool anyServerHasFlow = servers.Any(s => !string.IsNullOrEmpty(s.Flow));
        hasUdpProxy = anyServerHasFlow;

        if (servers.Count == 1)
        {
            // Single server — direct VLESS outbound with tag="proxy"
            outbounds.Add(BuildVlessOutbound(servers[0], "proxy"));

            // UDP variant: same server but without flow
            if (anyServerHasFlow)
            {
                outbounds.Add(BuildVlessOutbound(servers[0], "proxy-udp", overrideFlowEmpty: true));
            }
        }
        else if (servers.Count > 1)
        {
            // Multi-server — individual VLESS outbounds + urltest wrapper
            var childTags = new List<string>();
            var childTagsUdp = new List<string>();
            var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < servers.Count; i++)
            {
                var baseTag = !string.IsNullOrEmpty(servers[i].Name)
                    ? $"vless-{servers[i].Name}"
                    : $"vless-{i}";

                var tag = baseTag;
                var suffix = 2;
                while (!usedTags.Add(tag))
                {
                    tag = $"{baseTag}-{suffix++}";
                }

                childTags.Add(tag);
                outbounds.Add(BuildVlessOutbound(servers[i], tag));

                // UDP variant for each server
                if (anyServerHasFlow)
                {
                    var udpTag = $"{tag}-udp";
                    childTagsUdp.Add(udpTag);
                    outbounds.Add(BuildVlessOutbound(servers[i], udpTag, overrideFlowEmpty: true));
                }
            }

            // urltest selector — tag="proxy" so route/DNS rules work unchanged
            outbounds.Add(new SingBoxOutbound
            {
                Type      = "urltest",
                Tag       = "proxy",
                Outbounds = childTags,
                Url       = "http://www.gstatic.com/generate_204",
                Interval  = "3m",
                Tolerance = 150,
                InterruptExistConnections = false
            });

            // urltest for UDP outbounds
            if (anyServerHasFlow)
            {
                outbounds.Add(new SingBoxOutbound
                {
                    Type      = "urltest",
                    Tag       = "proxy-udp",
                    Outbounds = childTagsUdp,
                    Url       = "http://www.gstatic.com/generate_204",
                    Interval  = "3m",
                    Tolerance = 150,
                    InterruptExistConnections = false
                });
            }
        }

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        return outbounds;
    }

    /// <summary>
    /// Build a single VLESS outbound from a server entry.
    /// When overrideFlowEmpty=true, the flow field is omitted (for UDP-optimized outbound).
    /// </summary>
    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag, bool overrideFlowEmpty = false)
    {
        // Null-safe: YamlDotNet may leave nested objects null if YAML has empty keys
        var transport = entry.Transport ?? new VlessTransportConfig();
        var transportType = transport.Type ?? "tcp";

        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = entry.Server,
            ServerPort = entry.Port,
            Uuid       = entry.Uuid,
            Flow       = overrideFlowEmpty ? null
                       : (string.IsNullOrEmpty(entry.Flow) ? null : entry.Flow),
            Tls        = BuildTlsConfig(entry),
            Transport  = transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? null
                : new TransportConfig
                {
                    Type    = transportType,
                    Path    = transport.Path,
                    Headers = transport.Headers?.Count > 0 ? transport.Headers : null
                },
            DomainResolver = "local-dns"
        };
    }

    // ─── TLS / Reality ────────────────────────────────────────────────────────

    private static TlsConfig BuildTlsConfig(VlessServerEntry entry)
    {
        var security = entry.Security ?? "reality";
        var isReality = security.Equals("reality", StringComparison.OrdinalIgnoreCase);

        if (isReality)
        {
            var reality = entry.Reality ?? new VlessRealityConfig();
            return new TlsConfig
            {
                Enabled    = true,
                ServerName = reality.ServerName,
                Insecure   = false,
                Utls = new UtlsConfig
                {
                    Enabled     = true,
                    Fingerprint = reality.Fingerprint
                },
                Reality = new RealityConfig
                {
                    Enabled   = true,
                    PublicKey = reality.PublicKey,
                    ShortId   = reality.ShortId
                }
            };
        }

        // Plain TLS fallback
        var tls = entry.Tls ?? new VlessTlsConfig();
        return new TlsConfig
        {
            Enabled    = tls.Enabled,
            ServerName = tls.ServerName,
            Insecure   = tls.Insecure
        };
    }

    // ─── Route (sing-box 1.12+ action-based format) ──────────────────────────

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes,
        string routingMode = "split", bool hasUdpProxy = false)
    {
        var isFullTunnel = (routingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);

        var rules = new List<RouteRule>
        {
            // Protocol sniffing: detect HTTP/TLS/QUIC and override destination with sniffed domain.
            // Replaces deprecated inbound-level sniff + sniff_override_destination (removed in 1.13).
            new() { Action = "sniff", Timeout = "300ms" },

            // DNS traffic: hijack and resolve through DNS module (replaces "dns" outbound)
            new() { Protocol = "dns", Action = "hijack-dns" }
        };

        // Private IPs always direct — MUST be before process/default rules so that
        // traffic to local/VPN subnets (WireGuard, AmneziaWG, LAN) is never
        // sent through the remote proxy, in both split and full tunnel modes.
        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        if (!isFullTunnel && processes.Count > 0)
        {
            if (hasUdpProxy)
            {
                // Dual outbound: UDP traffic → proxy-udp (no flow, better for voice/video)
                // TCP traffic → proxy (with xtls-rprx-vision, optimized for TCP)
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
                // Single outbound: all traffic → proxy
                rules.Add(new RouteRule
                {
                    ProcessName = processes.ToList(),
                    Action      = "route",
                    Outbound    = "proxy"
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

        return new SingBoxRoute
        {
            Rules                   = rules,
            // Full tunnel: all non-private traffic → proxy
            // Split tunnel: unmatched traffic → direct
            Final                   = isFullTunnel ? "proxy" : "direct",
            AutoDetectInterface     = true,
            // Required since sing-box 1.12, mandatory in 1.14
            DefaultDomainResolver   = "local-dns"
        };
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