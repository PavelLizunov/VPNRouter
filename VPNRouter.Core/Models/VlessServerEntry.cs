using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// A single proxy server entry with all connection parameters.
///
/// <para>Despite the legacy name <c>VlessServerEntry</c>, this type now
/// holds entries for any supported protocol (VLESS+Reality, Hysteria2,
/// TUIC v5, Shadowsocks 2022, optionally with ShadowTLS v3 plugin or
/// Hysteria2 Salamander obfuscation). The <see cref="Protocol"/>
/// discriminator controls which subset of fields is meaningful at
/// outbound-generation time. The class name is preserved to avoid
/// breaking the existing YAML alias mapping.</para>
/// </summary>
public class VlessServerEntry
{
    /// <summary>Optional display name for logs (e.g. "main", "backup")</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Protocol discriminator. One of:
    /// <list type="bullet">
    /// <item><c>vless</c> (default) — VLESS+Reality, optionally over TCP/WS/gRPC</item>
    /// <item><c>hysteria2</c> — Hysteria2 (with optional Salamander obfs)</item>
    /// <item><c>tuic</c> — TUIC v5</item>
    /// <item><c>shadowsocks</c> — Shadowsocks 2022 (with optional ShadowTLS v3 plugin)</item>
    /// <item><c>naive</c> — NaiveProxy (HTTP/2 or HTTP/3 via Cronet; Windows + Linux only)</item>
    /// <item><c>dns-tunnel</c> — VLESS tunnelled over DNS via a local slipstream-client
    /// sidecar (last-resort transport; Windows + Linux only). Carries
    /// <see cref="DnsDomain"/> / <see cref="DnsResolvers"/> / <see cref="DnsLeafFingerprint"/>
    /// + reuses <see cref="Uuid"/>; the VLESS outbound is generated against
    /// 127.0.0.1:&lt;localPort&gt; with no TLS (the tunnel does its own QUIC-TLS).</item>
    /// </list>
    /// New entries default to "vless" so legacy settings.yaml without
    /// this field stays valid.
    /// </summary>
    [YamlMember(Alias = "protocol")]
    public string Protocol { get; set; } = "vless";

    /// <summary>
    /// True when this entry is a DNS-tunnel (slipstream) server. Field-based
    /// (not just the <see cref="Protocol"/> string) so it survives a
    /// serialization round-trip that drops <c>Protocol</c> back to its "vless"
    /// default. v2.42.0-r2 symptom: a dns-tunnel server ran fine on Android but
    /// its list subtitle showed "tcp + reality" because the JSON cache lost
    /// <c>Protocol</c> while the dns-tunnel payload (<see cref="DnsDomain"/> /
    /// <see cref="DnsResolvers"/> / <see cref="DnsLeafCertPem"/>) survived — and
    /// those fields ONLY exist on a dns-tunnel entry, so their presence
    /// unambiguously identifies one.
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public bool IsDnsTunnel =>
        string.Equals(Protocol, "dns-tunnel", System.StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(DnsDomain)
        || (DnsResolvers != null && DnsResolvers.Count > 0)
        || !string.IsNullOrWhiteSpace(DnsLeafCertPem);

    [YamlMember(Alias = "server")]
    public string Server { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 443;

    [YamlMember(Alias = "uuid")]
    public string Uuid { get; set; } = string.Empty;

    [YamlMember(Alias = "flow")]
    public string Flow { get; set; } = string.Empty;

    [YamlMember(Alias = "security")]
    public string Security { get; set; } = "reality";

    [YamlMember(Alias = "reality")]
    public VlessRealityConfig Reality { get; set; } = new();

    [YamlMember(Alias = "tls")]
    public VlessTlsConfig Tls { get; set; } = new();

    [YamlMember(Alias = "transport")]
    public VlessTransportConfig Transport { get; set; } = new();

    // ── Non-VLESS fields ────────────────────────────────────────────────────
    // All optional. Populated only when Protocol != "vless".

    /// <summary>
    /// NaiveProxy basic-auth username (paired with <see cref="Password"/>).
    /// Empty for non-naive protocols.
    /// </summary>
    [YamlMember(Alias = "username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Authentication password.
    /// <list type="bullet">
    /// <item>Hysteria2 — auth password (URL userinfo)</item>
    /// <item>TUIC — auth password (URL userinfo, paired with Uuid)</item>
    /// <item>Shadowsocks — encryption key / password (paired with Method)</item>
    /// <item>NaiveProxy — auth password (paired with Username)</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks cipher method (e.g. <c>2022-blake3-aes-256-gcm</c>,
    /// <c>aes-256-gcm</c>, <c>chacha20-ietf-poly1305</c>). Ignored for
    /// non-Shadowsocks protocols.
    /// </summary>
    [YamlMember(Alias = "method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// TUIC congestion-control algorithm. <c>bbr</c> | <c>cubic</c> |
    /// <c>new_reno</c>. Default <c>bbr</c>.
    /// </summary>
    [YamlMember(Alias = "congestion_control")]
    public string CongestionControl { get; set; } = "bbr";

    /// <summary>
    /// TUIC UDP relay mode. <c>native</c> | <c>quic</c>.
    /// </summary>
    [YamlMember(Alias = "udp_relay_mode")]
    public string UdpRelayMode { get; set; } = "native";

    /// <summary>
    /// Hysteria2 Salamander obfuscation. Empty = obfs disabled.
    /// </summary>
    [YamlMember(Alias = "obfs_type")]
    public string ObfsType { get; set; } = string.Empty;

    /// <summary>Salamander password (paired with <see cref="ObfsType"/> = "salamander").</summary>
    [YamlMember(Alias = "obfs_password")]
    public string ObfsPassword { get; set; } = string.Empty;

    /// <summary>
    /// Hysteria2 Brutal congestion-control bandwidth, Mbit/s. 0 = unset -> sing-box
    /// falls back to BBR. When &gt;0, Brutal is engaged: it ignores loss and paces to
    /// this ceiling, masking access-leg loss/jitter — the lever that stabilises
    /// realtime game UDP (Roblox 277) on a TSPU-throttled RU path. MUST be calibrated
    /// to ~70-80% of the MEASURED client-&gt;server goodput: over-declaring self-induces
    /// the very loss it's meant to mask. Parsed from the hysteria2 URI (?up=&amp;down=).
    /// See plans/roblox-tester-vps-spec-2026-06-27.md.
    /// </summary>
    [YamlMember(Alias = "hysteria_up_mbps")]
    public int HysteriaUpMbps { get; set; }

    /// <summary>Hysteria2 Brutal down bandwidth, Mbit/s. 0 = unset (BBR). See <see cref="HysteriaUpMbps"/>.</summary>
    [YamlMember(Alias = "hysteria_down_mbps")]
    public int HysteriaDownMbps { get; set; }

    /// <summary>
    /// AmneziaWG (AWG2) parameters when <see cref="Protocol"/> = "amneziawg" (or "awg").
    /// Null for non-AWG servers. Requires a client bundling sing-box-lx (build tag
    /// with_awg); official sing-box rejects the AWG fields. <see cref="Server"/>/<see
    /// cref="Port"/> are the peer endpoint; <see cref="AwgConfig.PeerPublicKey"/> the
    /// server key. See plans/amneziawg-fork-implementation-plan-2026-06-27.md.
    /// </summary>
    [YamlMember(Alias = "awg")]
    public AwgConfig? Awg { get; set; }

    /// <summary>
    /// Shadowsocks plugin name (e.g. <c>shadow-tls</c> for ShadowTLS v3).
    /// Empty = no plugin.
    /// </summary>
    [YamlMember(Alias = "plugin")]
    public string Plugin { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks plugin opts (semicolon-delimited <c>key=value</c> pairs:
    /// <c>version=3;password=xxx;host=cdn.example.com</c>).
    /// </summary>
    [YamlMember(Alias = "plugin_opts")]
    public string PluginOpts { get; set; } = string.Empty;

    /// <summary>
    /// r5: co-located server pairing tag from the subscription (<c>pair=</c>
    /// query param). NaiveProxy can't carry UDP, so a naive server and its
    /// same-node UDP-capable sibling (e.g. Hysteria2) share an identical
    /// PairGroup; VPNRouter routes UDP through the sibling (same physical node →
    /// same exit IP). Empty = no pairing.
    /// </summary>
    [YamlMember(Alias = "pair")]
    public string PairGroup { get; set; } = string.Empty;

    /// <summary>
    /// r7 #1: NaiveProxy HTTP/3-over-QUIC transport. Set from a
    /// <c>naive+quic://</c> share-link; emitted as the naive outbound's
    /// <c>quic</c> boolean. False (default) = HTTP/2. Naive-only.
    /// </summary>
    [YamlMember(Alias = "naive_quic")]
    public bool NaiveQuic { get; set; }

    // ── DNS-tunnel (slipstream) fields ──────────────────────────────────────
    // Populated only when Protocol == "dns-tunnel". Parsed from a
    // dns-tunnel:// share-link (base64url-JSON payload). See
    // plans/dns-tunnel-slipstream-integration-2026-06-10.md.

    /// <summary>
    /// dns-tunnel: the tunnel domain (slipstream-client <c>-d</c>). Also mirrored
    /// into <see cref="Server"/> for dedup/display identity. Empty for other
    /// protocols.
    /// </summary>
    [YamlMember(Alias = "dns_domain")]
    public string DnsDomain { get; set; } = string.Empty;

    /// <summary>
    /// dns-tunnel: НСДИ resolver endpoints (<c>ip:port</c>, e.g.
    /// <c>195.208.4.1:53</c>) — slipstream-client repeats <c>-r</c> for each
    /// (multipath). Empty for other protocols.
    /// </summary>
    [YamlMember(Alias = "dns_resolvers")]
    public List<string> DnsResolvers { get; set; } = new();

    /// <summary>
    /// dns-tunnel: when true, prefer the OS/operator default resolver(s) discovered
    /// at connect time over the hardcoded <see cref="DnsResolvers"/> (desktop: the
    /// active NIC's DNS servers; Android: the active network's
    /// <c>ConnectivityManager.getLinkProperties().getDnsServers()</c>). Set by a
    /// <c>"system"</c>/<c>"auto"</c>/<c>"os"</c> sentinel token in the link's <c>r</c>
    /// array. This is the operator-agnostic WL-BYPASS path: on a strict RU mobile
    /// whitelist the only reachable DNS is the operator's own resolver, so a link
    /// cannot hardcode НСДИ IPs and work for every operator. Any concrete IPs that
    /// ALSO appear in <c>r</c> stay in <see cref="DnsResolvers"/> as a fallback for
    /// when the OS resolver can't be discovered.
    /// </summary>
    [YamlMember(Alias = "dns_use_system_resolver")]
    public bool DnsUseSystemResolver { get; set; }

    /// <summary>
    /// dns-tunnel: OPTIONAL authoritative DNS endpoint(s) (<c>ip:port</c>, e.g.
    /// <c>213.155.15.93:53</c>) — slipstream-client repeats <c>--authoritative</c>
    /// for each. Queries the tunnel server's authoritative NS DIRECTLY, bypassing
    /// the recursive resolver. The recursive НСДИ resolvers rate-limit the covert
    /// query stream after ~1.5-3 min (→ QUIC idle-timeout 0x433); the authoritative
    /// path has no such limit, so where the network allows direct UDP to it the
    /// tunnel is stable. Passed ALONGSIDE <see cref="DnsResolvers"/> (multipath:
    /// authoritative when reachable, recursive as the censorship-resilient
    /// fallback). Empty for other protocols / when the server publishes none.
    /// </summary>
    [YamlMember(Alias = "dns_authoritative")]
    public List<string> DnsAuthoritative { get; set; } = new();

    /// <summary>
    /// dns-tunnel: the full server leaf certificate (PEM, with BEGIN/END
    /// markers). Load-bearing — slipstream-client verifies the server-presented
    /// leaf against this via <c>--cert</c>. Carried in the profile (self-contained,
    /// supports multi-server / rotation without rebuilding the client) rather than
    /// bundled with the binary. SlipstreamManager writes it to
    /// <see cref="AppPaths.SlipstreamActiveCertPath"/> at launch. A leaf cert is
    /// public material (the server presents it in every TLS handshake), so storing
    /// it in the profile / on disk is not a secret leak. Empty for other protocols.
    /// </summary>
    [YamlMember(Alias = "dns_leaf_cert")]
    public string DnsLeafCertPem { get; set; } = string.Empty;

    /// <summary>
    /// dns-tunnel: OPTIONAL sha256 of the leaf cert (hex), for display + an
    /// integrity cross-check against <see cref="DnsLeafCertPem"/>. Not a pin
    /// (slipstream-client has no <c>--pin</c>); when present, SlipstreamManager
    /// verifies <c>sha256(PEM) == fingerprint</c> and refuses on mismatch. Empty
    /// for other protocols / when not supplied.
    /// </summary>
    [YamlMember(Alias = "dns_leaf_fingerprint")]
    public string DnsLeafFingerprint { get; set; } = string.Empty;
}

/// <summary>
/// AmneziaWG (AWG2) client parameters. Maps to a sing-box-lx <c>wireguard</c> endpoint
/// (build tag with_awg). Both ends must share IDENTICAL obfuscation params (Jc..H4, I1-I5).
/// </summary>
public class AwgConfig
{
    /// <summary>Client interface private key (base64).</summary>
    [YamlMember(Alias = "private_key")]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Client tunnel address(es), e.g. ["10.13.13.2/32"].</summary>
    [YamlMember(Alias = "address")]
    public List<string> Address { get; set; } = new();

    /// <summary>Server (peer) public key (base64).</summary>
    [YamlMember(Alias = "peer_public_key")]
    public string PeerPublicKey { get; set; } = string.Empty;

    /// <summary>Optional pre-shared key (base64). Empty = none.</summary>
    [YamlMember(Alias = "preshared_key")]
    public string PresharedKey { get; set; } = string.Empty;

    /// <summary>Persistent keepalive seconds (0 = default 25 applied at build).</summary>
    [YamlMember(Alias = "keepalive")]
    public int Keepalive { get; set; }

    // AWG obfuscation — must match the server. 0/empty = unset.
    [YamlMember(Alias = "jc")]   public int Jc { get; set; }
    [YamlMember(Alias = "jmin")] public int Jmin { get; set; }
    [YamlMember(Alias = "jmax")] public int Jmax { get; set; }
    [YamlMember(Alias = "s1")]   public int S1 { get; set; }
    [YamlMember(Alias = "s2")]   public int S2 { get; set; }
    [YamlMember(Alias = "s3")]   public int S3 { get; set; }
    [YamlMember(Alias = "s4")]   public int S4 { get; set; }
    /// <summary>Magic headers h1-h4: a uint32 or "min-max" range string (AWG2). Empty = unset.</summary>
    [YamlMember(Alias = "h1")] public string H1 { get; set; } = string.Empty;
    [YamlMember(Alias = "h2")] public string H2 { get; set; } = string.Empty;
    [YamlMember(Alias = "h3")] public string H3 { get; set; } = string.Empty;
    [YamlMember(Alias = "h4")] public string H4 { get; set; } = string.Empty;
    /// <summary>AWG2 CPS decoy strings i1-i5. Empty = unset.</summary>
    [YamlMember(Alias = "i1")] public string I1 { get; set; } = string.Empty;
    [YamlMember(Alias = "i2")] public string I2 { get; set; } = string.Empty;
    [YamlMember(Alias = "i3")] public string I3 { get; set; } = string.Empty;
    [YamlMember(Alias = "i4")] public string I4 { get; set; } = string.Empty;
    [YamlMember(Alias = "i5")] public string I5 { get; set; } = string.Empty;
}
