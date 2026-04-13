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

        var logPath = AppPaths.SingBoxLogPath;

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

        // Russian geo bypass — inject rule sets + DNS/route rules so RU traffic
        // goes direct (real IP), protecting VPN server from blacklists.
        if (settings.App.BypassRussianTraffic && GeoDataDownloader.AreGeoFilesAvailable())
        {
            ApplyGeoBypass(config);
        }

        return config;
    }

    // ─── Russian geo bypass ───────────────────────────────────────────────────

    private const string GeoIpRuleSetTag = "vpnrouter-geoip-ru";
    private const string GeoSiteRuleSetTag = "vpnrouter-geosite-ru";
    private const string DirectDnsRuTag = "vpnrouter-dns-ru";

    private static void ApplyGeoBypass(SingBoxConfig config)
    {
        // 1. Add rule_set entries pointing to local .srs files
        config.Route.RuleSet ??= new List<RuleSetEntry>();
        var geoIpPath = AppPaths.GeoIpRuPath.Replace('\\', '/');
        var geoSitePath = AppPaths.GeoSiteRuPath.Replace('\\', '/');

        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = GeoIpRuleSetTag,
            Format = "binary",
            Path = geoIpPath
        });
        config.Route.RuleSet.Add(new RuleSetEntry
        {
            Type = "local",
            Tag = GeoSiteRuleSetTag,
            Format = "binary",
            Path = geoSitePath
        });

        // 2. Add Russian DNS server (Yandex 77.88.8.8) routed via dns-direct
        // outbound (real NIC, no proxy, no routing loop)
        config.Dns.Servers.Add(new DnsServer
        {
            Tag = DirectDnsRuTag,
            Type = "udp",
            Server = "77.88.8.8",
            Detour = "dns-direct"
        });

        // 3. Add DNS rule: RU domains use Russian DNS resolver
        config.Dns.Rules.Insert(0, new DnsRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag },
            Action = "route",
            Server = DirectDnsRuTag
        });

        // 4. Add route rule: RU sites/IPs go direct (BEFORE process_name rules)
        // Find insertion point: after sniff/hijack-dns/private-ip rules
        int insertAt = 0;
        for (int i = 0; i < config.Route.Rules.Count; i++)
        {
            var r = config.Route.Rules[i];
            if (r.Action == "sniff" || r.Action == "hijack-dns" || r.IpIsPrivate == true)
            {
                insertAt = i + 1;
                continue;
            }
            break;
        }

        config.Route.Rules.Insert(insertAt, new RouteRule
        {
            RuleSet = new List<string> { GeoSiteRuleSetTag, GeoIpRuleSetTag },
            Action = "route",
            Outbound = "direct"
        });
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
            // ipv4_only protects from IPv6 leaks (when VPN tunnels only IPv4) AND
            // skips slow AAAA queries (+100-300ms each). Disable only if user
            // explicitly wants IPv6 via dns.strategy in config.yaml.
            Strategy = settings.App.ForceIpv4Only ? "ipv4_only" : null,
            // Strict DNS: all queries via VPN (no leaks possible).
            // Full tunnel: all DNS through VPN by default.
            // Split tunnel (default): only targeted processes use VPN DNS, rest use local.
            Final = (isFullTunnel || settings.App.StrictDns) ? "vpn-dns" : "local-dns",
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
                InterfaceName           = OperatingSystem.IsMacOS() ? "utun99" : settings.Tun.InterfaceName,
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
    /// Build outbound list. Auto-detects UDP split:
    /// - If servers have BOTH flow and no-flow entries → dual outbound (TCP/UDP split)
    /// - Servers WITH flow → "proxy" (TCP, xtls-rprx-vision optimized)
    /// - Servers WITHOUT flow → "proxy-udp" (UDP, better for voice/video)
    /// - If all servers have same flow config → single "proxy" outbound
    /// </summary>
    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings, out bool hasUdpProxy)
    {
        var servers = settings.Vless.GetActiveServers();
        var outbounds = new List<SingBoxOutbound>();

        // Auto-detect: split servers by flow presence
        var flowServers = servers.Where(s => !string.IsNullOrEmpty(s.Flow)).ToList();
        var noFlowServers = servers.Where(s => string.IsNullOrEmpty(s.Flow)).ToList();
        hasUdpProxy = flowServers.Count > 0 && noFlowServers.Count > 0;

        if (hasUdpProxy)
        {
            // Dual outbound: TCP → proxy (with flow), UDP → proxy-udp (no flow)
            AddOutboundGroup(outbounds, flowServers, "proxy", "vless");
            AddOutboundGroup(outbounds, noFlowServers, "proxy-udp", "vless-udp");
        }
        else
        {
            // Single outbound: all traffic → proxy
            AddOutboundGroup(outbounds, servers, "proxy", "vless");
        }

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        // dns-direct: separate non-empty direct outbound for DNS servers.
        // sing-box 1.13 FATAL: "detour to empty direct outbound makes no sense"
        // when using detour:"direct" on a bare direct outbound. udp_fragment:true
        // makes it non-empty so we can route DNS through it.
        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "dns-direct", UdpFragment = true });
        return outbounds;
    }

    /// <summary>
    /// Add a group of VLESS outbounds. Single server → direct outbound.
    /// Multiple servers → individual outbounds + urltest wrapper.
    /// </summary>
    private static void AddOutboundGroup(List<SingBoxOutbound> outbounds,
        List<VlessServerEntry> servers, string groupTag, string childPrefix)
    {
        if (servers.Count == 1)
        {
            outbounds.Add(BuildVlessOutbound(servers[0], groupTag));
        }
        else if (servers.Count > 1)
        {
            var childTags = new List<string>();
            var usedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < servers.Count; i++)
            {
                var baseTag = !string.IsNullOrEmpty(servers[i].Name)
                    ? $"{childPrefix}-{servers[i].Name}"
                    : $"{childPrefix}-{i}";

                var tag = baseTag;
                var suffix = 2;
                while (!usedTags.Add(tag))
                    tag = $"{baseTag}-{suffix++}";

                childTags.Add(tag);
                outbounds.Add(BuildVlessOutbound(servers[i], tag));
            }

            outbounds.Add(new SingBoxOutbound
            {
                Type      = "urltest",
                Tag       = groupTag,
                Outbounds = childTags,
                Url       = "http://www.gstatic.com/generate_204",
                Interval  = "3m",
                Tolerance = 150,
                InterruptExistConnections = false
            });
        }
    }

    /// <summary>
    /// Build a single VLESS outbound from a server entry.
    /// Flow is included only when entry.Flow is non-empty (auto-detect: no-flow servers → no flow in output).
    /// </summary>
    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag)
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
            Flow       = string.IsNullOrEmpty(entry.Flow) ? null : entry.Flow,
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
                },
                // TLS record fragmentation: splits ClientHello across multiple TLS
                // records to bypass DPI that inspects the first record for SNI.
                // Available since sing-box 1.12.0. Falls back to normal handshake
                // if fragmented attempt doesn't complete within 500ms.
                RecordFragment = true,
                FragmentFallbackDelay = "500ms"
            };
        }

        // Plain TLS (e.g. VLESS+WS+TLS via CDN)
        var tls = entry.Tls ?? new VlessTlsConfig();
        var tlsConfig = new TlsConfig
        {
            Enabled    = tls.Enabled,
            ServerName = tls.ServerName,
            Insecure   = tls.Insecure
        };

        // uTLS fingerprint (critical for Cloudflare CDN — without it, handshake fails)
        if (!string.IsNullOrEmpty(tls.Fingerprint))
        {
            tlsConfig.Utls = new UtlsConfig
            {
                Enabled = true,
                Fingerprint = tls.Fingerprint
            };
        }

        // ALPN (e.g. "http/1.1" for WebSocket, "h2" for gRPC)
        if (!string.IsNullOrEmpty(tls.Alpn))
        {
            tlsConfig.Alpn = tls.Alpn
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return tlsConfig;
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