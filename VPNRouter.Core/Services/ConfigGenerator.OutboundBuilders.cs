using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static partial class ConfigGenerator
{
    /// <summary>
    /// r5: when the active server is NaiveProxy (UDP-incapable), find a
    /// co-located UDP-capable sibling so UDP (Discord voice, games) can route
    /// through it. Pairing key, in priority order:
    /// <list type="number">
    /// <item><see cref="VlessServerEntry.PairGroup"/> — the subscription's
    /// <c>pair=</c> tag (bulletproof; the backend marks naive + its same-node
    /// HY2 with the same value).</item>
    /// <item>Base-name match — strip the protocol token and compare the
    /// remainder (transition fallback before a refresh ships the tag).</item>
    /// </list>
    /// Returns the sibling (preferring Hysteria2/TUIC for best UDP), or null
    /// when the active server isn't naive or no UDP sibling exists (caller then
    /// falls back to the standard flow/no-flow logic).
    /// </summary>
    private static VlessServerEntry? FindNaiveUdpSibling(
        List<VlessServerEntry> activeServers, List<VlessServerEntry> pool,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        // r8 #6: pairing logic lives in NaivePairing so config-gen and the UI
        // ("naive + hy2" label) share ONE source of truth — the label can never
        // claim a pairing the generator wouldn't make.
        // RB1: pass the liveness probe so a dead UDP sibling is never selected.
        var naive = activeServers.FirstOrDefault(NaivePairing.IsNaive);
        return naive == null ? null : NaivePairing.FindUdpSibling(naive, pool, isServerAlive);
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
    /// Build a single proxy outbound from a server entry. v2.30.1-r3
    /// dispatches on <see cref="VlessServerEntry.Protocol"/> to support
    /// VLESS+Reality / Hysteria2 / TUIC v5 / Shadowsocks 2022 (with
    /// optional ShadowTLS plugin) from a single entry-point. Existing
    /// callers keep working — VLESS remains the default protocol when
    /// the discriminator is empty or unset.
    /// </summary>
    private static SingBoxOutbound BuildVlessOutbound(VlessServerEntry entry, string tag)
    {
        var protocol = (entry.Protocol ?? "vless").ToLowerInvariant();
        return protocol switch
        {
            "hysteria2"   => BuildHysteria2Outbound(entry, tag),
            "hy2"         => BuildHysteria2Outbound(entry, tag),   // r10 (Codex #2): hy2 alias parity with VlessDeepVerifier
            "tuic"        => BuildTuicOutbound(entry, tag),
            "shadowsocks" => BuildShadowsocksOutbound(entry, tag),
            "ss"          => BuildShadowsocksOutbound(entry, tag),
            "naive"       => BuildNaiveOutbound(entry, tag),
            "dns-tunnel"  => BuildDnsTunnelOutbound(entry, tag),
            _             => BuildVlessOutboundCore(entry, tag),
        };
    }

    /// <summary>
    /// DNS-tunnel (slipstream) outbound. The VLESS traffic rides over the local
    /// slipstream-client front (started separately by SlipstreamManager /
    /// VpnEngine), so the outbound targets <c>127.0.0.1:&lt;localPort&gt;</c> with
    /// the uuid set and <b>no TLS / Reality / flow / transport</b> — the tunnel
    /// provides its own QUIC-TLS. The real server domain + resolvers + leaf cert
    /// live in the dns-tunnel profile and are consumed by SlipstreamManager, not
    /// here. No domain_resolver: the server is a literal loopback IP.
    /// </summary>
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

    /// <summary>
    /// Extract literal IP addresses from dns-tunnel resolver strings
    /// (<c>"1.2.3.4:53"</c>, <c>"[2001:db8::1]:53"</c>, <c>"9.9.9.9"</c>),
    /// skipping hostnames (those are covered by the process_name exclusion).
    /// Returns bare IPs suitable for a sing-box <c>ip_cidr</c> rule (a bare IP
    /// is treated as /32 or /128). Order-preserving, de-duplicated.
    /// </summary>
    private static List<string> ExtractResolverIps(IEnumerable<string>? resolvers)
    {
        var ips = new List<string>();
        if (resolvers == null) return ips;
        foreach (var raw in resolvers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            string host;
            if (s.StartsWith("[", StringComparison.Ordinal))          // [ipv6]:port
            {
                var end = s.IndexOf(']');
                if (end <= 1) continue;
                host = s.Substring(1, end - 1);
            }
            else
            {
                var firstColon = s.IndexOf(':');
                var lastColon  = s.LastIndexOf(':');
                // Strip a trailing :port only for the unambiguous ipv4:port shape
                // (exactly one colon). A bare IPv6 literal has multiple colons and
                // no brackets — keep it whole.
                host = (firstColon >= 0 && firstColon == lastColon)
                    ? s.Substring(0, lastColon)
                    : s;
            }
            if (System.Net.IPAddress.TryParse(host, out _) && !ips.Contains(host))
                ips.Add(host);
        }
        return ips;
    }

    /// <summary>VLESS+Reality outbound (the original implementation).</summary>
    private static SingBoxOutbound BuildVlessOutboundCore(VlessServerEntry entry, string tag)
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
            // XHTTP is incompatible with XTLS-Vision (protocol limitation) — drop the flow
            // even if a stray one is present, so a VLESS+XHTTP+Reality config is valid.
            Flow       = (string.IsNullOrEmpty(entry.Flow)
                          || transportType.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
                ? null : entry.Flow,
            Tls        = BuildTlsConfig(entry),
            Transport  = transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                ? null
                : BuildTransportConfig(transportType, transport),
            DomainResolver = "local-dns",
            // v2.36 F4 fix (EOStārāTheia 2026-05-23 — Android ~5 min
            // auto-disconnect). sing-box 1.13's default tcp_keep_alive
            // initial period is 5m, which doesn't beat ISP/NAT idle
            // timeouts on mobile (typically 30-180s). Forces the
            // connection to drop silently right at the 5-min mark.
            // Setting both fields to 30s makes OS-level keepalive
            // probes fire BEFORE NAT mappings expire. Cross-platform
            // (also helps desktop on flaky home routers / corporate
            // NATs). See plans/android-disconnect-investigation-v2.36.md.
            TcpKeepAlive         = "30s",
            TcpKeepAliveInterval = "30s",
        };
    }

    /// <summary>
    /// Hysteria2 outbound. ALPN defaults to <c>["h3"]</c> per Hysteria2
    /// spec (it's QUIC-only). When <see cref="VlessServerEntry.ObfsType"/>
    /// is "salamander", emits the obfs block.
    /// </summary>
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
            // 2026-06-08 (scout #2 #6): Hysteria2 dials its server over QUIC/UDP.
            // In the naive+HY2 pairing it carries ALL the UDP, so on an IPv6-less
            // host it hits the SAME "address not valid in its context" failure the
            // naive fix targets. prefer_ipv4 = IPv4-first server resolution.
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

        // T2 (2026-06-27): Brutal CC calibration. When both up/down are set (>0), engage
        // Brutal — it ignores loss and paces to the declared ceiling, masking the access-leg
        // loss/jitter that times RakNet out (Roblox 277) on a TSPU-throttled RU path. Both
        // required (sing-box wants the pair); 0/unset -> omit -> BBR (prior behaviour). The
        // value MUST be ~70-80% of measured goodput — over-declaring self-induces loss.
        if (entry.HysteriaUpMbps > 0 && entry.HysteriaDownMbps > 0)
        {
            ob.UpMbps   = entry.HysteriaUpMbps;
            ob.DownMbps = entry.HysteriaDownMbps;
        }

        return ob;
    }

    /// <summary>
    /// AmneziaWG (AWG2) endpoint for a sing-box-lx (with_awg) client. The schema —
    /// a <c>wireguard</c> endpoint with promoted obfuscation fields + peer with
    /// <c>persistent_keepalive_interval</c> — was verified against <c>sing-box-lx check</c>
    /// (2026-06-27). Server/Port are the peer endpoint; obfuscation params must match the
    /// server. See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
    /// </summary>
    internal static SingBoxEndpoint BuildAmneziaWgEndpoint(VlessServerEntry entry, string tag)
    {
        var awg = entry.Awg ?? new AwgConfig();
        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
        return new SingBoxEndpoint
        {
            Type       = "wireguard",
            Tag        = tag,
            System     = false,
            Mtu        = AwgEndpointMtu,
            Address    = awg.Address.Count > 0 ? new List<string>(awg.Address) : new List<string> { "10.13.13.2/32" },
            PrivateKey = awg.PrivateKey,
            Jc = awg.Jc, Jmin = awg.Jmin, Jmax = awg.Jmax,
            S1 = awg.S1, S2 = awg.S2, S3 = awg.S3, S4 = awg.S4,
            H1 = NullIfEmpty(awg.H1), H2 = NullIfEmpty(awg.H2), H3 = NullIfEmpty(awg.H3), H4 = NullIfEmpty(awg.H4),
            I1 = NullIfEmpty(awg.I1), I2 = NullIfEmpty(awg.I2), I3 = NullIfEmpty(awg.I3),
            I4 = NullIfEmpty(awg.I4), I5 = NullIfEmpty(awg.I5),
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

    /// <summary>
    /// TUIC v5 outbound. ALPN defaults to <c>["h3"]</c> per TUIC spec.
    /// </summary>
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
            // 2026-06-08 (scout #2 #6): TUIC dials its server over QUIC/UDP — same
            // IPv6-less-host hazard as Hysteria2/naive. prefer_ipv4 server resolution.
            DomainResolver    = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    /// <summary>
    /// Shadowsocks outbound. Supports SS 2022 ciphers natively via
    /// <see cref="VlessServerEntry.Method"/>. When
    /// <see cref="VlessServerEntry.Plugin"/> is "shadow-tls" (or any
    /// other plugin name sing-box recognises), emits the plugin /
    /// plugin_opts pair and lets sing-box wire it up.
    /// </summary>
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

    /// <summary>
    /// NaiveProxy outbound. sing-box 1.13's naive outbound is deliberately
    /// minimal — username/password basic auth + a plain TLS block. It does
    /// NOT accept <c>tls.insecure=true</c>, uTLS, or <c>alpn</c> (sing-box
    /// rejects them at outbound init), so the TLS here is just
    /// <c>{enabled, server_name}</c> (insecure defaults to false, which IS
    /// accepted). Requires <c>libcronet.{dll,so}</c> next to the sing-box
    /// binary → Windows + Linux only (SagerNet ships no macOS Cronet, on any
    /// version). macOS naive servers are filtered out before generation so we
    /// never emit a config that FATALs at sing-box start.
    /// </summary>
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
            Quic           = entry.NaiveQuic ? true : (bool?)null, // r7 #1: HTTP/3 over QUIC
            Tls            = new TlsConfig
            {
                Enabled    = true,
                ServerName = string.IsNullOrEmpty(entry.Tls?.ServerName) ? entry.Server : entry.Tls.ServerName,
            },
            // 2026-06-08 (Pavel "Latvia NAIVE" run): force IPv4-first server
            // resolution via the 1.13 domain_resolver object form. naive_quic
            // dials the server over UDP/QUIC; on an IPv6-less host sing-box was
            // picking the server's AAAA and failing with "open UDP connection to
            // [2001:...]: address not valid in its context" (17x). prefer_ipv4
            // tries the A record first, falling back to IPv6 only if there's no
            // A — safe for IPv6-only servers too. (The legacy top-level
            // domain_strategy outbound option is FATAL in sing-box 1.13.)
            DomainResolver = new DomainResolverValue("local-dns", "prefer_ipv4"),
        };
    }

    private static List<string>? ParseAlpnList(string? alpn)
    {
        if (string.IsNullOrWhiteSpace(alpn)) return null;
        return alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    // ─── Transport ────────────────────────────────────────────────────────────

    private static TransportConfig BuildTransportConfig(string type, VlessTransportConfig source)
    {
        var isGrpc = type.Equals("grpc", StringComparison.OrdinalIgnoreCase);

        // XHTTP (sing-box-lx with_xhttp): VLESS over plain HTTP/2, composes with Reality,
        // incompatible with XTLS-Vision. host is a TOP-LEVEL field (not in headers). Schema
        // verified vs `sing-box-lx check`. See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
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
            // gRPC: service_name (no path, no headers)
            // WS: path + headers
            Path        = isGrpc ? null : source.Path,
            ServiceName = isGrpc ? source.Path : null,
            Headers     = isGrpc ? null : (source.Headers?.Count > 0 ? source.Headers : null)
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
                    // v2.40.0-r9 (#2 core-audit): drop a structurally-invalid short_id.
                    // sing-box's hex.Decode PANICS (index out of range) on a Reality
                    // short_id > 8 bytes (16 hex chars) — a 10/20-hex sid from a
                    // copy-paste/generator bug would crash sing-box at config load AND
                    // crash-loop the HealthMonitor Advisory reload (→ routed traffic
                    // falls direct). An empty short_id is valid, so degrade to "" → a
                    // clean handshake attempt instead of a panic.
                    ShortId   = VlessUriParser.IsValidRealityShortId(reality.ShortId)
                                    ? reality.ShortId : string.Empty
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

}
