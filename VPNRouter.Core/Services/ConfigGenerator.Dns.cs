using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private static readonly HashSet<string> PublicTldDenyList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "com", "net", "org", "io", "co", "dev", "app", "ru", "info", "biz",
            "online", "xyz", "me", "tv", "cc", "us", "uk", "de", "edu", "gov"
        };

    internal const string DefaultDirectDohServer = "8.8.8.8";
    internal const string DefaultDirectDohPath = "/dns-query";
    internal const string LocalBootstrapDnsTag = "local-bootstrap";

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings, bool isExcludeMode = false, bool? strictDnsOverride = null, bool proxyIsUdpNative = false)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        var strictDns = strictDnsOverride ?? settings.App.StrictDns;

        var defaultVpnDns = isFullTunnel || isExcludeMode || strictDns;

        var localDns = BuildLocalDnsServer(settings.Dns.LocalDns, out var localBootstrap);
        var servers = new List<DnsServer>
        {
            BuildVpnDnsServer(settings, proxyIsUdpNative),
            localDns
        };
        if (localBootstrap != null)
            servers.Add(localBootstrap);

        var dns = new SingBoxDns
        {
            Strategy = (settings.App.ForceIpv4Only || !settings.Tun.Ipv6Enabled) ? "ipv4_only" : null,
            Final = defaultVpnDns ? "vpn-dns" : "local-dns",
            Servers = servers,
            Rules = new List<DnsRule>
            {
                new()
                {
                    QueryType = new List<string> { "HTTPS", "SVCB" },
                    Action    = "reject"
                }
            }
        };

        if (settings.App.ResolveLanViaSystemDns && !strictDns)
            AddLanSystemDns(dns, settings);

        if (!isFullTunnel && processes.Count > 0)
        {
            var dnsServer = isExcludeMode
                ? (strictDns ? "vpn-dns" : "local-dns")
                : (strictDns || profile.DnsMode != "smart" ? "vpn-dns" : "local-dns");
            dns.Rules.Add(new DnsRule
            {
                ProcessName = processes.ToList(),
                Action      = "route",
                Server      = dnsServer
            });
        }

        return dns;
    }

    private static void AddLanSystemDns(SingBoxDns dns, AppSettings settings)
    {
        dns.Servers.Add(new DnsServer
        {
            Tag  = "dns-system",
            Type = "local"
        });

        var lanSuffixes = new List<string> { "local", "lan", "home.arpa", "internal" };
        if (settings.App.LanDnsSuffixes != null)
        {
            foreach (var s in settings.App.LanDnsSuffixes)
            {
                var t = s?.Trim().TrimStart('.');
                if (string.IsNullOrEmpty(t)) continue;
                if (!t!.Contains('.') && PublicTldDenyList.Contains(t))
                    continue;
                if (!lanSuffixes.Contains(t, StringComparer.OrdinalIgnoreCase))
                    lanSuffixes.Add(t);
            }
        }

        dns.Rules.Add(new DnsRule
        {
            DomainSuffix = lanSuffixes,
            Action       = "route",
            Server       = "dns-system"
        });
    }

    private static DnsServer BuildVpnDnsServer(AppSettings settings, bool proxyIsUdpNative)
    {
        if (proxyIsUdpNative)
        {
            return new DnsServer
            {
                Tag    = "vpn-dns",
                Type   = "udp",
                Server = settings.App.BlockAds ? "94.140.14.14" : ToPlainDnsIp(settings.Dns.VpnDns),
                Detour = "proxy"
            };
        }

        return new DnsServer
        {
            Tag        = "vpn-dns",
            Type       = "https",
            Server     = settings.App.BlockAds ? "dns.adguard-dns.com" : ParseDohHost(settings.Dns.VpnDns),
            ServerPort = settings.App.BlockAds ? 443 : ParseDohPort(settings.Dns.VpnDns),
            Path       = settings.App.BlockAds ? "/dns-query" : ParseDohPath(settings.Dns.VpnDns),
            Detour     = "proxy",
            DomainResolver = "local-dns"
        };
    }

    private static string ToPlainDnsIp(string dohUrl)
    {
        var host = ParseDohHost(dohUrl);
        return System.Net.IPAddress.TryParse(host, out _) ? host : "8.8.8.8";
    }

    private static string ParseDohHost(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Host;
        return url;
    }

    private static int? ParseDohPort(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Port > 0 && !uri.IsDefaultPort)
                return uri.Port;
            return null;
        }
        return null;
    }

    private static string ParseDohPath(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.IsNullOrEmpty(uri.AbsolutePath) ? "/dns-query" : uri.AbsolutePath;
        return "/dns-query";
    }

    internal static bool IsExplicitLocalDnsOverride(string? configuredLocalDns)
    {
        var raw = configuredLocalDns?.Trim();
        return !string.IsNullOrEmpty(raw)
            && !raw.Equals("local", StringComparison.OrdinalIgnoreCase);
    }

    internal static DnsServer BuildLocalDnsServer(string? configuredLocalDns, out DnsServer? bootstrapServer, string tag = "local-dns")
    {
        bootstrapServer = null;
        var raw = configuredLocalDns?.Trim();

        // Legacy default ("local") and blank values preserve the encrypted direct DoH
        // resolver so split-tunnel direct apps avoid ISP DNS poisoning and TUN loops.
        if (string.IsNullOrEmpty(raw) || raw.Equals("local", StringComparison.OrdinalIgnoreCase))
            return DefaultDirectLocalDns(tag);

        if (raw.Equals("system", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("os", StringComparison.OrdinalIgnoreCase))
        {
            return new DnsServer
            {
                Tag  = tag,
                Type = "local"
            };
        }

        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                return DefaultDirectLocalDns(tag);

            if (!TryExtractRawUriHost(raw, out var rawAuthorityHost, out var wasBracketed))
                return DefaultDirectLocalDns(tag);

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.Port == 0)
                return DefaultDirectLocalDns(tag);

            string host;
            if (wasBracketed)
            {
                if (!TryParseStrictIp(rawAuthorityHost, out var parsedIpv6, ipv6Only: true))
                    return DefaultDirectLocalDns(tag);
                host = parsedIpv6.ToString();
            }
            else if (TryParseStrictIp(rawAuthorityHost, out var parsedUriIp))
            {
                host = parsedUriIp.ToString();
            }
            else if (IsValidDnsHostname(rawAuthorityHost))
            {
                host = rawAuthorityHost.ToLowerInvariant();
            }
            else
            {
                return DefaultDirectLocalDns(tag);
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            DnsServer server;
            switch (scheme)
            {
                case "https":
                    server = new DnsServer
                    {
                        Tag        = tag,
                        Type       = "https",
                        Server     = host,
                        ServerPort = uri.Port > 0 && !uri.IsDefaultPort ? uri.Port : null,
                        Path       = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
                            ? DefaultDirectDohPath
                            : uri.AbsolutePath,
                        Detour     = "dns-direct"
                    };
                    break;
                case "tls":
                    server = new DnsServer
                    {
                        Tag        = tag,
                        Type       = "tls",
                        Server     = host,
                        ServerPort = uri.Port > 0 && uri.Port != 853 ? uri.Port : null,
                        Detour     = "dns-direct"
                    };
                    break;
                case "udp":
                    server = new DnsServer
                    {
                        Tag        = tag,
                        Type       = "udp",
                        Server     = host,
                        ServerPort = uri.Port > 0 && uri.Port != 53 ? uri.Port : null,
                        Detour     = "dns-direct"
                    };
                    break;
                default:
                    return DefaultDirectLocalDns(tag);
            }

            AttachBootstrapIfHostname(server, host, out bootstrapServer);
            return server;
        }

        if (TryParseHostAndOptionalPort(raw, out var udpHost, out var udpPort))
        {
            var server = new DnsServer
            {
                Tag        = tag,
                Type       = "udp",
                Server     = udpHost,
                ServerPort = udpPort is > 0 and <= 65535 && udpPort != 53 ? udpPort : null,
                Detour     = "dns-direct"
            };
            AttachBootstrapIfHostname(server, udpHost, out bootstrapServer);
            return server;
        }

        return DefaultDirectLocalDns(tag);
    }

    private static DnsServer DefaultDirectLocalDns(string tag) => new()
    {
        Tag    = tag,
        Type   = "https",
        Server = DefaultDirectDohServer,
        Path   = DefaultDirectDohPath,
        Detour = "dns-direct"
    };

    private static void AttachBootstrapIfHostname(DnsServer server, string host, out DnsServer? bootstrapServer)
    {
        if (TryParseStrictIp(host, out _))
        {
            bootstrapServer = null;
            return;
        }

        bootstrapServer = new DnsServer
        {
            Tag  = LocalBootstrapDnsTag,
            Type = "local"
        };
        server.DomainResolver = LocalBootstrapDnsTag;
    }

    private static bool TryExtractRawUriHost(string raw, out string host, out bool wasBracketed)
    {
        host = string.Empty;
        wasBracketed = false;
        var sep = raw.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0)
            return false;

        var rest = raw[(sep + 3)..];
        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd >= 0 ? rest[..authorityEnd] : rest;
        if (string.IsNullOrEmpty(authority) || authority.Contains('@'))
            return false;

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close <= 1)
                return false;
            var tail = authority[(close + 1)..];
            if (tail.Length > 0)
            {
                if (tail[0] != ':'
                    || !int.TryParse(tail[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)
                    || parsedPort is <= 0 or > 65535)
                    return false;
            }
            host = authority[1..close];
            wasBracketed = true;
            return true;
        }

        var colon = authority.IndexOf(':');
        if (colon >= 0)
        {
            if (authority.IndexOf(':', colon + 1) >= 0)
                return false;
            var portPart = authority[(colon + 1)..];
            if (!int.TryParse(portPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)
                || parsedPort is <= 0 or > 65535)
                return false;
            host = authority[..colon];
            return !string.IsNullOrEmpty(host);
        }

        host = authority;
        return true;
    }

    private static bool TryParseStrictIp(string raw, out System.Net.IPAddress ip, bool ipv6Only = false)
    {
        ip = System.Net.IPAddress.None;
        if (string.IsNullOrEmpty(raw)
            || raw.Contains('[')
            || raw.Contains(']')
            || raw.Contains('%'))
            return false;

        if (!System.Net.IPAddress.TryParse(raw, out var parsed))
            return false;

        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (ipv6Only)
                return false;
            // Reject shorthand IPv4 ("192.168.1", "53") and leading-zero/non-canonical forms.
            if (raw.Split('.').Length != 4 || !string.Equals(parsed.ToString(), raw, StringComparison.Ordinal))
                return false;
            ip = parsed;
            return true;
        }

        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            ip = parsed;
            return true;
        }

        return false;
    }

    private static bool TryParseHostAndOptionalPort(string raw, out string host, out int? port)
    {
        host = string.Empty;
        port = null;
        if (string.IsNullOrEmpty(raw) || raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '?' or '#'))
            return false;

        if (raw[0] == '[')
        {
            var close = raw.IndexOf(']');
            if (close <= 1)
                return false;

            var ipPart = raw[1..close];
            if (!TryParseStrictIp(ipPart, out var bracketIp, ipv6Only: true))
                return false;

            var tail = raw[(close + 1)..];
            if (tail.Length == 0)
            {
                host = bracketIp.ToString();
                return true;
            }
            if (tail[0] != ':' || !int.TryParse(tail[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)
                || parsedPort is <= 0 or > 65535)
                return false;

            host = bracketIp.ToString();
            port = parsedPort;
            return true;
        }

        if (TryParseStrictIp(raw, out var bareIp))
        {
            host = bareIp.ToString();
            return true;
        }

        var colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            if (raw.IndexOf(':', colon + 1) >= 0)
                return false;
            var hostPart = raw[..colon];
            var portPart = raw[(colon + 1)..];
            if (!int.TryParse(portPart, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort)
                || parsedPort is <= 0 or > 65535)
                return false;
            if (TryParseStrictIp(hostPart, out var parsedHostIp))
            {
                host = parsedHostIp.ToString();
                port = parsedPort;
                return true;
            }
            if (!IsValidDnsHostname(hostPart))
                return false;

            host = hostPart;
            port = parsedPort;
            return true;
        }

        if (!IsValidDnsHostname(raw))
            return false;

        host = raw;
        return true;
    }

    private static bool IsValidDnsHostname(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
            return false;

        var labels = host.Split('.');
        // Require at least two labels so single-word typos (e.g. "systm", "dhcp")
        // fall back safely instead of becoming UDP servers with OS bootstrap DNS.
        if (labels.Length < 2)
            return false;

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                return false;
            if (!char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
                return false;
            foreach (var c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return false;
            }
        }

        // RFC 3696: TLD must not be all-numeric (also rejects malformed IPs like 192.168.1.999).
        if (labels[^1].All(char.IsAsciiDigit))
            return false;

        return true;
    }

}
