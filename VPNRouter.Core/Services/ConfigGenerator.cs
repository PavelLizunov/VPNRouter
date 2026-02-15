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
            Outbounds = BuildOutbounds(settings),
            Route = BuildRoute(profile, processes),
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
        var dns = new SingBoxDns
        {
            Strategy = "ipv4_only",
            // Default DNS for processes NOT in the list → local system DNS (direct)
            // Without this, sing-box uses the first server (vpn-dns via proxy),
            // so ALL apps lose DNS when the proxy goes down
            Final = "local-dns",
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

        // Targeted processes → VPN DNS (leak protection)
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
                EndpointIndependentNat  = false,
                Stack                   = "system",
                Sniff                   = true,
                SniffOverrideDestination = true
            }
        };
    }

    // ─── Outbounds ────────────────────────────────────────────────────────────
    // sing-box 1.12+: removed "dns" and "block" outbound types
    // DNS hijacking is done via route rule action: "hijack-dns"
    // Blocking is done via route rule action: "reject"

    private static List<SingBoxOutbound> BuildOutbounds(AppSettings settings)
    {
        var servers = settings.Vless.GetEffectiveServers();
        var outbounds = new List<SingBoxOutbound>();

        if (servers.Count == 1)
        {
            // Single server — direct VLESS outbound with tag="proxy"
            outbounds.Add(BuildVlessOutbound(servers[0], "proxy"));
        }
        else if (servers.Count > 1)
        {
            // Multi-server — individual VLESS outbounds + urltest wrapper
            var childTags = new List<string>();
            for (int i = 0; i < servers.Count; i++)
            {
                var tag = !string.IsNullOrEmpty(servers[i].Name)
                    ? $"vless-{servers[i].Name}"
                    : $"vless-{i}";
                childTags.Add(tag);
                outbounds.Add(BuildVlessOutbound(servers[i], tag));
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
        }

        outbounds.Add(new SingBoxOutbound { Type = "direct", Tag = "direct" });
        return outbounds;
    }

    /// <summary>Build a single VLESS outbound from a server entry.</summary>
    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = entry.Server,
            ServerPort = entry.Port,
            Uuid       = entry.Uuid,
            Flow       = string.IsNullOrEmpty(entry.Flow) ? null : entry.Flow,
            Tls        = BuildTlsConfig(entry),
            Transport  = entry.Transport.Type.ToLowerInvariant() == "tcp"
                ? null
                : new TransportConfig
                {
                    Type    = entry.Transport.Type,
                    Path    = entry.Transport.Path,
                    Headers = entry.Transport.Headers.Count > 0 ? entry.Transport.Headers : null
                },
            DomainResolver = "local-dns"
        };
    }

    // ─── TLS / Reality ────────────────────────────────────────────────────────

    private static TlsConfig BuildTlsConfig(VlessServerEntry entry)
    {
        var isReality = entry.Security.Equals("reality", StringComparison.OrdinalIgnoreCase);

        if (isReality)
        {
            return new TlsConfig
            {
                Enabled    = true,
                ServerName = entry.Reality.ServerName,
                Insecure   = false,
                Utls = new UtlsConfig
                {
                    Enabled     = true,
                    Fingerprint = entry.Reality.Fingerprint
                },
                Reality = new RealityConfig
                {
                    Enabled   = true,
                    PublicKey = entry.Reality.PublicKey,
                    ShortId   = entry.Reality.ShortId
                }
            };
        }

        // Plain TLS fallback
        return new TlsConfig
        {
            Enabled    = entry.Tls.Enabled,
            ServerName = entry.Tls.ServerName,
            Insecure   = entry.Tls.Insecure
        };
    }

    // ─── Route (sing-box 1.12+ action-based format) ──────────────────────────

    private static SingBoxRoute BuildRoute(Profile profile, List<string> processes)
    {
        var rules = new List<RouteRule>
        {
            // DNS traffic: hijack and resolve through DNS module (replaces "dns" outbound)
            new() { Protocol = "dns", Action = "hijack-dns" }
        };

        if (processes.Count > 0)
        {
            // Route targeted processes through VPN proxy
            rules.Add(new RouteRule
            {
                ProcessName = processes.ToList(),
                Action      = "route",
                Outbound    = "proxy"
            });
        }

        // Private IPs always direct
        rules.Add(new RouteRule
        {
            IpIsPrivate = true,
            Action      = "route",
            Outbound    = "direct"
        });

        return new SingBoxRoute
        {
            Rules                   = rules,
            Final                   = "direct",
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