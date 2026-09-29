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

    private static SingBoxDns BuildDns(Profile profile, List<string> processes, AppSettings settings, bool isExcludeMode = false, bool? strictDnsOverride = null, bool proxyIsUdpNative = false)
    {
        var routingMode = settings.App.RoutingMode ?? "split";
        var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);

        var strictDns = strictDnsOverride ?? settings.App.StrictDns;

        var defaultVpnDns = isFullTunnel || isExcludeMode || strictDns;

        var dns = new SingBoxDns
        {
            Strategy = (settings.App.ForceIpv4Only || !settings.Tun.Ipv6Enabled) ? "ipv4_only" : null,
            Final = defaultVpnDns ? "vpn-dns" : "local-dns",
            Servers = new List<DnsServer>
            {
                BuildVpnDnsServer(settings, proxyIsUdpNative),
                new()
                {
                    Tag        = "local-dns",
                    Type       = "https",
                    Server     = "8.8.8.8",
                    Path       = "/dns-query",
                    Detour     = "dns-direct"
                }
            },
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

        if (isFullTunnel)
        {
        }
        else if (isExcludeMode)
        {
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

}
