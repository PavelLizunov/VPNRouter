#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>
/// A sing-box <c>domain_resolver</c> dial-field value. Serializes as a bare
/// string (<c>"local-dns"</c>) when <see cref="Strategy"/> is null, or as the
/// 1.13 object form (<c>{ "server": "local-dns", "strategy": "prefer_ipv4" }</c>)
/// when a strategy is set. Implicitly constructible from a string so existing
/// <c>DomainResolver = "local-dns"</c> call sites stay byte-identical.
/// </summary>
public sealed class DomainResolverValue
{
    public DomainResolverValue() { }
    public DomainResolverValue(string server, string? strategy = null)
    {
        Server = server;
        Strategy = strategy;
    }

    public string Server { get; set; } = "";
    public string? Strategy { get; set; }

    public static implicit operator DomainResolverValue(string server) => new(server);
}

/// <summary>
/// Writes <see cref="DomainResolverValue"/> as a bare string (server only) or
/// the object form (server + strategy). Reads either form back.
/// </summary>
public sealed class DomainResolverValueConverter : JsonConverter<DomainResolverValue>
{
    public override DomainResolverValue? Read(
        ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
            return new DomainResolverValue(reader.GetString() ?? "");
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var v = new DomainResolverValue();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "server", System.StringComparison.OrdinalIgnoreCase))
                    v.Server = reader.GetString() ?? "";
                else if (string.Equals(name, "strategy", System.StringComparison.OrdinalIgnoreCase))
                    v.Strategy = reader.GetString();
                else
                    reader.Skip();
            }
            return v;
        }
        reader.Skip();
        return null;
    }

    public override void Write(
        Utf8JsonWriter writer, DomainResolverValue value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value.Strategy))
        {
            writer.WriteStringValue(value.Server);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("server", value.Server);
        writer.WriteString("strategy", value.Strategy);
        writer.WriteEndObject();
    }
}

// ─── sing-box config root ───────────────────────────────────────────────────

// Phase 4 (2026-05-18): migrated from Newtonsoft.Json [JsonProperty] to
// System.Text.Json [JsonPropertyName]. Wire format is byte-identical because:
//   • Every property pins its on-disk key via [JsonPropertyName(...)] so the
//     sing-box JSON we emit has the exact snake_case keys that the binary
//     parses (process_name, server_port, route_exclude_address, ...).
//   • Nullable optional fields get [JsonIgnore(Condition=WhenWritingNull)]
//     so STJ elides them on serialize — matches Newtonsoft's
//     NullValueHandling.Ignore behaviour we relied on for "absent =/=
//     default" semantics in sing-box.
// The sing-box check integration tests run against the generated JSON and
// will fail loudly if any key drifts.

public class SingBoxConfig
{
    [JsonPropertyName("log")]
    public SingBoxLog Log { get; set; } = new();

    [JsonPropertyName("dns")]
    public SingBoxDns Dns { get; set; } = new();

    [JsonPropertyName("inbounds")]
    public List<SingBoxInbound> Inbounds { get; set; } = new();

    [JsonPropertyName("outbounds")]
    public List<SingBoxOutbound> Outbounds { get; set; } = new();

    [JsonPropertyName("route")]
    public SingBoxRoute Route { get; set; } = new();

    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SingBoxExperimental? Experimental { get; set; }
}

// ─── Log ─────────────────────────────────────────────────────────────────────

public class SingBoxLog
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("timestamp")]
    public bool Timestamp { get; set; } = true;

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;
}

// ─── DNS ─────────────────────────────────────────────────────────────────────

public class SingBoxDns
{
    [JsonPropertyName("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<DnsRule> Rules { get; set; } = new();

    /// <summary>
    /// Default DNS server for queries that don't match any rule.
    /// Without this, sing-box uses the first server (vpn-dns through proxy),
    /// which means ALL apps lose DNS when the proxy goes down.
    /// </summary>
    [JsonPropertyName("final")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Final { get; set; }

    [JsonPropertyName("strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; set; } = "ipv4_only";
}

/// <summary>sing-box 1.12+ new DNS server format</summary>
public class DnsServer
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// sing-box 1.12+ typed DNS server format.
    /// Valid types: https, tls, udp, tcp, local, fakeip, dhcp, quic, h3, tailscale
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "https";

    /// <summary>Used for type=https: DNS-over-HTTPS server hostname</summary>
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    /// <summary>Used for type=https: port (default 443)</summary>
    [JsonPropertyName("server_port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServerPort { get; set; }

    /// <summary>Used for type=https: URL path (e.g. /dns-query)</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("detour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detour { get; set; }
}

/// <summary>sing-box 1.12+ new DNS rule format with action</summary>
public class DnsRule
{
    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcessName { get; set; }

    /// <summary>route | reject | pre-resolve — new action-based format in 1.12</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "route";

    /// <summary>Tag of DNS server to use (for action=route)</summary>
    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    /// <summary>Match by rule set tags (geosite-ru, etc).</summary>
    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RuleSet { get; set; }

    // v2.30.0 — fields for custom block rules with domain-type match.
    // Block rules with these match types produce a DNS-level reject so
    // the lookup itself fails (no-traffic UX for "blocked = invisible").

    /// <summary>Match by exact FQDN(s) — used by custom block rules.</summary>
    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Domain { get; set; }

    /// <summary>Match by domain suffix(es) — used by custom block rules.</summary>
    [JsonPropertyName("domain_suffix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainSuffix { get; set; }

    /// <summary>Match by substring — used by custom block rules.</summary>
    [JsonPropertyName("domain_keyword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainKeyword { get; set; }
}

// ─── Inbounds ─────────────────────────────────────────────────────────────────

public class SingBoxInbound
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "tun";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "tun-in";

    [JsonPropertyName("interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    /// <summary>sing-box 1.12+: address is an array (replaces inet4_address/inet6_address)</summary>
    [JsonPropertyName("address")]
    public List<string> Address { get; set; } = new() { "172.19.0.1/30" };

    // v2.42.0-r3: 9000 jumbo broke HTTP/2 over TCP-only proxies (browser
    // ERR_CONNECTION_CLOSED via fragmentation). 1280 = IPv6 min, traverses any path.
    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 1280;

    [JsonPropertyName("auto_route")]
    public bool AutoRoute { get; set; } = true;

    [JsonPropertyName("strict_route")]
    public bool StrictRoute { get; set; } = false;

    /// <summary>
    /// Exclude specific address ranges from TUN routing.
    /// Traffic to these addresses bypasses TUN and uses the system routing table.
    /// </summary>
    [JsonPropertyName("route_exclude_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RouteExcludeAddress { get; set; }

    [JsonPropertyName("endpoint_independent_nat")]
    public bool EndpointIndependentNat { get; set; } = false;

    [JsonPropertyName("stack")]
    public string Stack { get; set; } = "system";
}
// Note: sniff + sniff_override_destination removed from inbound (deprecated since 1.11, removed in 1.13).
// Sniffing is now handled by route rule with action: "sniff".

// ─── Outbounds ────────────────────────────────────────────────────────────────

public class SingBoxOutbound
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    [JsonPropertyName("server_port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServerPort { get; set; }

    [JsonPropertyName("uuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uuid { get; set; }

    [JsonPropertyName("flow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Flow { get; set; }

    [JsonPropertyName("tls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TlsConfig? Tls { get; set; }

    [JsonPropertyName("transport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransportConfig? Transport { get; set; }

    /// <summary>
    /// sing-box 1.12+: DNS resolver tag for resolving the server hostname.
    /// Suppresses "missing domain_resolver in dial fields" deprecation warning.
    /// Must reference a tag from dns.servers.
    /// </summary>
    /// <summary>
    /// sing-box dial field. Serializes as a bare string tag
    /// (<c>"domain_resolver": "local-dns"</c>) when no IPv4/IPv6 strategy is
    /// needed, or as the 1.13 object form
    /// (<c>{ "server": "local-dns", "strategy": "prefer_ipv4" }</c>) when a
    /// strategy is set. The legacy top-level <c>domain_strategy</c> outbound
    /// option was REMOVED in sing-box 1.13 (FATAL unless
    /// ENABLE_DEPRECATED_LEGACY_DOMAIN_STRATEGY_OPTIONS=true), so per-outbound
    /// IPv4-preference must ride inside <c>domain_resolver</c>. An implicit
    /// <c>string</c> conversion keeps the 5 existing <c>= "local-dns"</c> call
    /// sites byte-identical (string form, strategy null).
    /// </summary>
    [JsonPropertyName("domain_resolver")]
    [JsonConverter(typeof(DomainResolverValueConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DomainResolverValue? DomainResolver { get; set; }

    // ── TCP dial keepalive (v2.36 F4 fix — EOStārāTheia 2026-05-23) ──
    // sing-box 1.13 changed the default tcp_keep_alive INITIAL period
    // from 10m → 5m, meaning OS-level keepalive probes don't start until
    // the connection has been idle for 5 minutes. On mobile with NAT /
    // ISP middlebox idle timeouts (typically 30-180s) this guarantees
    // the connection drops silently before keepalive ever kicks in.
    // EOStārāTheia's report ("~5 min auto-disconnect") matches the
    // sing-box default exactly. Setting tcp_keep_alive=30s forces the
    // first keepalive probe much earlier so NAT mappings stay alive.
    // tcp_keep_alive_interval=30s keeps subsequent probes frequent.
    // See plans/android-disconnect-investigation-v2.36.md.

    /// <summary>
    /// TCP keep-alive INITIAL period — duration string ("30s", "2m").
    /// sing-box 1.13 default = 5m which is too long for mobile NAT
    /// timeouts. Set short on outbounds that should survive idle phone.
    /// </summary>
    [JsonPropertyName("tcp_keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TcpKeepAlive { get; set; }

    /// <summary>
    /// TCP keep-alive probe INTERVAL — duration string ("30s", "75s").
    /// sing-box 1.13 default = 75s. Set shorter for aggressive mobile
    /// keepalive ("30s" recommended).
    /// </summary>
    [JsonPropertyName("tcp_keep_alive_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TcpKeepAliveInterval { get; set; }

    // ── URLTest outbound fields (for multi-server failover) ────────────────

    /// <summary>Tags of child outbounds (e.g. ["vless-0", "vless-1"]). Used when type=urltest.</summary>
    [JsonPropertyName("outbounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Outbounds { get; set; }

    /// <summary>Health check URL. Default: http://www.gstatic.com/generate_204</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    /// <summary>Health check interval (e.g. "3m", "30s")</summary>
    [JsonPropertyName("interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Interval { get; set; }

    /// <summary>Latency tolerance in ms before switching servers</summary>
    [JsonPropertyName("tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Tolerance { get; set; }

    /// <summary>Whether to interrupt existing connections on server switch</summary>
    [JsonPropertyName("interrupt_exist_connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InterruptExistConnections { get; set; }

    /// <summary>
    /// For type=direct: enables UDP fragmentation. Setting this to true makes the
    /// outbound "non-empty" so sing-box 1.13 accepts detour:"direct" pointing to it
    /// (otherwise FATAL: "detour to empty direct outbound makes no sense").
    /// </summary>
    [JsonPropertyName("udp_fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UdpFragment { get; set; }

    // ── Non-VLESS protocol fields (v2.30.1-r3 multi-protocol support) ──────

    /// <summary>NaiveProxy basic-auth username (naive outbound only).</summary>
    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    /// <summary>r7 #1: NaiveProxy HTTP/3-over-QUIC transport (naive outbound only).</summary>
    [JsonPropertyName("quic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Quic { get; set; }

    /// <summary>Auth password — used by Hysteria2, TUIC, Shadowsocks, NaiveProxy outbounds.</summary>
    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    /// <summary>Shadowsocks cipher method (e.g. <c>2022-blake3-aes-256-gcm</c>).</summary>
    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    /// <summary>TUIC congestion-control (<c>bbr</c> | <c>cubic</c> | <c>new_reno</c>).</summary>
    [JsonPropertyName("congestion_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CongestionControl { get; set; }

    /// <summary>TUIC UDP relay mode (<c>native</c> | <c>quic</c>).</summary>
    [JsonPropertyName("udp_relay_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UdpRelayMode { get; set; }

    /// <summary>Hysteria2 obfuscation block (Salamander).</summary>
    [JsonPropertyName("obfs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hysteria2Obfs? Obfs { get; set; }

    /// <summary>Shadowsocks plugin name (e.g. <c>shadow-tls</c>).</summary>
    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plugin { get; set; }

    /// <summary>Shadowsocks plugin options (semicolon-delimited <c>key=value</c> pairs).</summary>
    [JsonPropertyName("plugin_opts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginOpts { get; set; }
}

/// <summary>Hysteria2 Salamander obfuscation block.</summary>
public class Hysteria2Obfs
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "salamander";

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class TlsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("server_name")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("insecure")]
    public bool Insecure { get; set; } = false;

    /// <summary>Reality config — set when using VLESS Reality (replaces standard TLS)</summary>
    [JsonPropertyName("reality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealityConfig? Reality { get; set; }

    /// <summary>TLS fingerprint for uTLS/Reality: firefox | chrome | safari | etc.</summary>
    [JsonPropertyName("utls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UtlsConfig? Utls { get; set; }

    /// <summary>ALPN negotiation protocols (e.g. ["http/1.1"] or ["h2", "http/1.1"])</summary>
    [JsonPropertyName("alpn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Alpn { get; set; }

    /// <summary>
    /// TLS record fragmentation — splits TLS handshake records into multiple
    /// smaller TLS records. Bypasses DPI that inspects the first TLS record
    /// for SNI. Available since sing-box 1.12.0.
    /// </summary>
    [JsonPropertyName("record_fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RecordFragment { get; set; }

    /// <summary>
    /// TCP-level ClientHello fragmentation — more aggressive than record_fragment.
    /// Splits the TCP segments carrying the TLS ClientHello.
    /// </summary>
    [JsonPropertyName("fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fragment { get; set; }

    /// <summary>
    /// Fallback delay for fragmentation. If the fragmented handshake doesn't
    /// complete within this duration, sing-box retries without fragmentation.
    /// Default: 500ms.
    /// </summary>
    [JsonPropertyName("fragment_fallback_delay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FragmentFallbackDelay { get; set; }
}

public class RealityConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("short_id")]
    public string ShortId { get; set; } = string.Empty;
}

public class UtlsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>chrome | firefox | safari | ios | android | edge | 360 | qq | random | randomized</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "firefox";
}

public class TransportConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ws";

    /// <summary>WebSocket path (e.g. /vless-ws). Only for type=ws.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>gRPC service name. Only for type=grpc.</summary>
    [JsonPropertyName("service_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; set; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }
}

// ─── Route ────────────────────────────────────────────────────────────────────

public class SingBoxRoute
{
    [JsonPropertyName("rules")]
    public List<RouteRule> Rules { get; set; } = new();

    [JsonPropertyName("final")]
    public string Final { get; set; } = "direct";

    [JsonPropertyName("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;

    /// <summary>
    /// sing-box 1.12+ requirement — suppresses deprecation warning.
    /// Must reference a tag from dns.servers.
    /// Will be required in sing-box 1.14.
    /// </summary>
    [JsonPropertyName("default_domain_resolver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultDomainResolver { get; set; }

    /// <summary>
    /// Rule sets — references to local .srs files (sing-box binary rule sets).
    /// Used for geo-based routing (geoip-ru, geosite-ru).
    /// </summary>
    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleSetEntry>? RuleSet { get; set; }
}

/// <summary>
/// Local rule set definition (sing-box format: type=local, path to .srs file).
/// </summary>
public class RuleSetEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "local";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "binary";

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("download_detour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadDetour { get; set; }

    /// <summary>
    /// v2.31.6-r18: explicit refresh interval for remote rule sets (e.g.
    /// "168h" = weekly). sing-box default is "24h" if absent — explicit
    /// value documents intent + lets us bump cadence per rule_set without
    /// bumping every one. Format per sing-box: Go duration string ("24h",
    /// "168h", "1h30m"). Ignored for local rule sets.
    /// </summary>
    [JsonPropertyName("update_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdateInterval { get; set; }
}

/// <summary>sing-box 1.12+ route rule — uses action-based format</summary>
public class RouteRule
{
    /// <summary>Match by inbound tag (e.g. "tun-in"). Used for sniff rule.</summary>
    [JsonPropertyName("inbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inbound { get; set; }

    [JsonPropertyName("protocol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Protocol { get; set; }

    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcessName { get; set; }

    /// <summary>Match by network type: "tcp" | "udp". Null = match both.</summary>
    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Network { get; set; }

    [JsonPropertyName("ip_is_private")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IpIsPrivate { get; set; }

    /// <summary>
    /// New 1.12+ action-based format: "route" | "reject" | "hijack-dns" | "sniff" | "resolve"
    /// Use Action + Outbound together for route action.
    /// </summary>
    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }

    /// <summary>Outbound tag — used when Action = "route"</summary>
    [JsonPropertyName("outbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outbound { get; set; }

    /// <summary>Sniff timeout — used when Action = "sniff". Default "300ms".</summary>
    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timeout { get; set; }

    /// <summary>Match by rule set tags (geoip-ru, geosite-ru, etc).</summary>
    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RuleSet { get; set; }

    // v2.29.0 — fields for custom direct rules (CustomDirectRule).

    /// <summary>Match by exact FQDN(s) (e.g. ["example.com", "api.example.com"]).</summary>
    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Domain { get; set; }

    /// <summary>Match by domain suffix(es) (e.g. [".lan.local"] matches "printer.lan.local").</summary>
    [JsonPropertyName("domain_suffix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainSuffix { get; set; }

    /// <summary>Match by substring(s) anywhere in the FQDN.</summary>
    [JsonPropertyName("domain_keyword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainKeyword { get; set; }

    /// <summary>Match by IP CIDR(s) (e.g. ["10.0.0.0/8", "192.168.0.0/16"]).</summary>
    [JsonPropertyName("ip_cidr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IpCidr { get; set; }

    /// <summary>Match by destination port(s).</summary>
    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Port { get; set; }
}

// ─── Experimental ─────────────────────────────────────────────────────────────

public class SingBoxExperimental
{
    [JsonPropertyName("clash_api")]
    public ClashApi ClashApi { get; set; } = new();
}

public class ClashApi
{
    [JsonPropertyName("external_controller")]
    public string ExternalController { get; set; } = "127.0.0.1:9090";
}
