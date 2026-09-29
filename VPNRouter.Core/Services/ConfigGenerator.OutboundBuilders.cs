using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    private static VlessServerEntry? FindNaiveUdpSibling(
        List<VlessServerEntry> activeServers, List<VlessServerEntry> pool,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        var naive = activeServers.FirstOrDefault(NaivePairing.IsNaive);
        return naive == null ? null : NaivePairing.FindUdpSibling(naive, pool, isServerAlive);
    }

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

    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag)
    {
        var protocol = (entry.Protocol ?? "vless").ToLowerInvariant();
        return protocol switch
        {
            "hysteria2"   => BuildHysteria2Outbound(entry, tag),
            "hy2"         => BuildHysteria2Outbound(entry, tag),
            "tuic"        => BuildTuicOutbound(entry, tag),
            "shadowsocks" => BuildShadowsocksOutbound(entry, tag),
            "ss"          => BuildShadowsocksOutbound(entry, tag),
            "naive"       => BuildNaiveOutbound(entry, tag),
            "dns-tunnel"  => BuildDnsTunnelOutbound(entry, tag),
            _             => BuildVlessOutboundCore(entry, tag),
        };
    }

    private static SingBoxOutbound BuildDnsTunnelOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = "127.0.0.1",
            ServerPort = SlipstreamManager.DefaultLocalPort,
            Uuid       = entry.Uuid,
        };
    }

    private static List<string> ExtractResolverIps(IEnumerable<string>? resolvers)
    {
        var ips = new List<string>();
        if (resolvers == null) return ips;
        foreach (var raw in resolvers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            string host;
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                var end = s.IndexOf(']');
                if (end <= 1) continue;
                host = s.Substring(1, end - 1);
            }
            else
            {
                var firstColon = s.IndexOf(':');
                var lastColon  = s.LastIndexOf(':');
                host = (firstColon >= 0 && firstColon == lastColon)
                    ? s.Substring(0, lastColon)
                    : s;
            }
            if (System.Net.IPAddress.TryParse(host, out _) && !ips.Contains(host))
                ips.Add(host);
        }
        return ips;
    }

    private static SingBoxOutbound BuildVlessOutboundCore(VlessServerEntry entry, string tag)
    {
        var transport = entry.Transport ?? new VlessTransportConfig();
        var transportType = transport.Type ?? "tcp";

        return new SingBoxOutbound
        {
            Type       = "vless",
            Tag        = tag,
            Server     = entry.Server,
            ServerPort = entry.Port,
            Uuid       = entry.Uuid,
            Flow       = (string.IsNullOrEmpty(entry.Flow)
                          || transportType.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
                ? null : entry.Flow,
            Tls        = BuildTlsConfig(entry),
            Transport  = transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? null
                : BuildTransportConfig(transportType, transport),
            DomainResolver = "local-dns",
            TcpKeepAlive         = "30s",
            TcpKeepAliveInterval = "30s",
        };
    }

    private static SingBoxOutbound BuildHysteria2Outbound(VlessServerEntry entry, string tag)
    {
        var tls = new TlsConfig
        {
            Enabled    = true,
            ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            Insecure   = entry.Tls?.Insecure ?? false,
            Alpn       = new List<string> { "h3" },
        };

        var ob = new SingBoxOutbound
        {
            Type           = "hysteria2",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Password       = entry.Password,
            Tls            = tls,
            DomainResolver = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };

        if (!string.IsNullOrEmpty(entry.ObfsType))
        {
            ob.Obfs = new Hysteria2Obfs
            {
                Type     = entry.ObfsType,
                Password = entry.ObfsPassword,
            };
        }

        if (entry.HysteriaUpMbps > 0 && entry.HysteriaDownMbps > 0)
        {
            ob.UpMbps   = entry.HysteriaUpMbps;
            ob.DownMbps = entry.HysteriaDownMbps;
        }

        return ob;
    }

    internal static string? NormalizeHeaderProtectionKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 64 && trimmed.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            return trimmed.ToLowerInvariant();

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length == 32)
                return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
        }
        return trimmed;
    }

    internal static SingBoxEndpoint BuildAmneziaWgEndpoint(VlessServerEntry entry, string tag)
    {
        var awg = entry.Awg ?? new AwgConfig();
        static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

        var hpk = NormalizeHeaderProtectionKey(awg.HeaderProtectionKey);
        var s1 = awg.S1;
        var s2 = awg.S2;
        var s3 = awg.S3;
        var s4 = awg.S4;

        if (!string.IsNullOrEmpty(hpk))
        {
            if (s1 < 12) s1 = 12;
            if (s2 < 12) s2 = 12;
            if (s3 < 12) s3 = 12;
            if (s4 < 12) s4 = 12;
        }

        return new SingBoxEndpoint
        {
            Type       = "wireguard",
            Tag        = tag,
            System     = false,
            Mtu        = AwgEndpointMtu,
            Address    = awg.Address.Count > 0 ? new List<string>(awg.Address) : new List<string> { "10.13.13.2/32" },
            PrivateKey = awg.PrivateKey,
            Jc = awg.Jc, Jmin = awg.Jmin, Jmax = awg.Jmax,
            S1 = s1, S2 = s2, S3 = s3, S4 = s4,
            H1 = NullIfEmpty(awg.H1), H2 = NullIfEmpty(awg.H2), H3 = NullIfEmpty(awg.H3), H4 = NullIfEmpty(awg.H4),
            I1 = NullIfEmpty(awg.I1), I2 = NullIfEmpty(awg.I2), I3 = NullIfEmpty(awg.I3),
            I4 = NullIfEmpty(awg.I4), I5 = NullIfEmpty(awg.I5),
            HeaderProtectionKey    = hpk,
            ContentPaddingAddition = NullIfEmpty(awg.ContentPaddingAddition),
            RandomTrailers         = awg.RandomTrailers ? true : null,
            DisableCookies         = awg.DisableCookies ? true : null,
            Peers = new List<WireGuardPeer>
            {
                new()
                {
                    Address                     = entry.Server,
                    Port                        = entry.Port,
                    PublicKey                   = awg.PeerPublicKey,
                    PreSharedKey                = NullIfEmpty(awg.PresharedKey),
                    AllowedIps                  = new List<string> { "0.0.0.0/0" },
                    PersistentKeepaliveInterval = awg.Keepalive > 0 ? awg.Keepalive : 25,
                }
            }
        };
    }

    private static SingBoxOutbound BuildTuicOutbound(VlessServerEntry entry, string tag)
    {
        var tls = new TlsConfig
        {
            Enabled    = true,
            ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            Insecure   = entry.Tls?.Insecure ?? false,
            Alpn       = ParseAlpnList(entry.Tls?.Alpn) ?? new List<string> { "h3" },
        };

        return new SingBoxOutbound
        {
            Type              = "tuic",
            Tag               = tag,
            Server            = entry.Server,
            ServerPort        = entry.Port,
            Uuid              = entry.Uuid,
            Password          = entry.Password,
            CongestionControl = string.IsNullOrEmpty(entry.CongestionControl) ? "bbr" : entry.CongestionControl,
            UdpRelayMode      = string.IsNullOrEmpty(entry.UdpRelayMode) ? "native" : entry.UdpRelayMode,
            Tls               = tls,
            DomainResolver    = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    private static SingBoxOutbound BuildShadowsocksOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type           = "shadowsocks",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Method         = entry.Method,
            Password       = entry.Password,
            Plugin         = string.IsNullOrEmpty(entry.Plugin) ? null : entry.Plugin,
            PluginOpts     = string.IsNullOrEmpty(entry.PluginOpts) ? null : entry.PluginOpts,
            DomainResolver = "local-dns",
        };
    }

    private static SingBoxOutbound BuildNaiveOutbound(VlessServerEntry entry, string tag)
    {
        return new SingBoxOutbound
        {
            Type           = "naive",
            Tag            = tag,
            Server         = entry.Server,
            ServerPort     = entry.Port,
            Username       = entry.Username,
            Password       = entry.Password,
            Quic           = entry.NaiveQuic ? true : (bool?)null,
            Tls            = new TlsConfig
            {
                Enabled    = true,
                ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            },
            DomainResolver = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    private static List<string>? ParseAlpnList(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn)) return null;
        return alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static TransportConfig BuildTransportConfig(string type, VlessTransportConfig source)
    {
        var isGrpc = type.Equals("grpc", StringComparison.OrdinalIgnoreCase);

        if (type.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
        {
            return new TransportConfig
            {
                Type          = "xhttp",
                Mode          = string.IsNullOrEmpty(source.Mode) ? "auto" : source.Mode,
                Path          = string.IsNullOrEmpty(source.Path) ? "/" : source.Path,
                Host          = string.IsNullOrEmpty(source.Host) ? null : source.Host,
                XPaddingBytes = string.IsNullOrEmpty(source.XPaddingBytes) ? null : source.XPaddingBytes,
                NoGrpcHeader  = source.NoGrpcHeader,
                Headers       = source.Headers?.Count > 0 ? source.Headers : null,
            };
        }

        return new TransportConfig
        {
            Type        = type,
            Path        = isGrpc ? null : source.Path,
            ServiceName = isGrpc ? source.Path : null,
            Headers     = isGrpc ? null : (source.Headers?.Count > 0 ? source.Headers : null)
        };
    }

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
                    ShortId   = VlessUriParser.IsValidRealityShortId(reality.ShortId)
                                    ? reality.ShortId : string.Empty
                },
                RecordFragment = true,
                FragmentFallbackDelay = "500ms"
            };
        }

        var tls = entry.Tls ?? new VlessTlsConfig();
        var tlsConfig = new TlsConfig
        {
            Enabled    = tls.Enabled,
            ServerName = tls.ServerName,
            Insecure   = tls.Insecure
        };

        if (!string.IsNullOrEmpty(tls.Fingerprint))
        {
            tlsConfig.Utls = new UtlsConfig
            {
                Enabled = true,
                Fingerprint = tls.Fingerprint
            };
        }

        if (!string.IsNullOrEmpty(tls.Alpn))
        {
            tlsConfig.Alpn = tls.Alpn
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return tlsConfig;
    }

}
