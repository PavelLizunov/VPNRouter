using Newtonsoft.Json;

namespace VPNRouter.Core.Models;

// ─── sing-box config root ───────────────────────────────────────────────────

public class SingBoxConfig
{
    [JsonProperty("log")]
    public SingBoxLog Log { get; set; } = new();

    [JsonProperty("dns")]
    public SingBoxDns Dns { get; set; } = new();

    [JsonProperty("inbounds")]
    public List<SingBoxInbound> Inbounds { get; set; } = new();

    [JsonProperty("outbounds")]
    public List<SingBoxOutbound> Outbounds { get; set; } = new();

    [JsonProperty("route")]
    public SingBoxRoute Route { get; set; } = new();

    [JsonProperty("experimental")]
    public SingBoxExperimental? Experimental { get; set; }
}

// ─── Log ─────────────────────────────────────────────────────────────────────

public class SingBoxLog
{
    [JsonProperty("level")]
    public string Level { get; set; } = "info";

    [JsonProperty("timestamp")]
    public bool Timestamp { get; set; } = true;

    [JsonProperty("output")]
    public string Output { get; set; } = string.Empty;
}

// ─── DNS ─────────────────────────────────────────────────────────────────────

public class SingBoxDns
{
    [JsonProperty("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonProperty("rules")]
    public List<DnsRule> Rules { get; set; } = new();

    /// <summary>
    /// Default DNS server for queries that don't match any rule.
    /// Without this, sing-box uses the first server (vpn-dns through proxy),
    /// which means ALL apps lose DNS when the proxy goes down.
    /// </summary>
    [JsonProperty("final", NullValueHandling = NullValueHandling.Ignore)]
    public string? Final { get; set; }

    [JsonProperty("strategy", NullValueHandling = NullValueHandling.Ignore)]
    public string? Strategy { get; set; } = "ipv4_only";
}

/// <summary>sing-box 1.12+ new DNS server format</summary>
public class DnsServer
{
    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// sing-box 1.12+ typed DNS server format.
    /// Valid types: https, tls, udp, tcp, local, fakeip, dhcp, quic, h3, tailscale
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; } = "https";

    /// <summary>Used for type=https: DNS-over-HTTPS server hostname</summary>
    [JsonProperty("server", NullValueHandling = NullValueHandling.Ignore)]
    public string? Server { get; set; }

    /// <summary>Used for type=https: port (default 443)</summary>
    [JsonProperty("server_port", NullValueHandling = NullValueHandling.Ignore)]
    public int? ServerPort { get; set; }

    /// <summary>Used for type=https: URL path (e.g. /dns-query)</summary>
    [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
    public string? Path { get; set; }

    [JsonProperty("detour", NullValueHandling = NullValueHandling.Ignore)]
    public string? Detour { get; set; }
}

/// <summary>sing-box 1.12+ new DNS rule format with action</summary>
public class DnsRule
{
    [JsonProperty("process_name", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? ProcessName { get; set; }

    /// <summary>route | reject | pre-resolve — new action-based format in 1.12</summary>
    [JsonProperty("action")]
    public string Action { get; set; } = "route";

    /// <summary>Tag of DNS server to use (for action=route)</summary>
    [JsonProperty("server", NullValueHandling = NullValueHandling.Ignore)]
    public string? Server { get; set; }

    /// <summary>Match by rule set tags (geosite-ru, etc).</summary>
    [JsonProperty("rule_set", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? RuleSet { get; set; }
}

// ─── Inbounds ─────────────────────────────────────────────────────────────────

public class SingBoxInbound
{
    [JsonProperty("type")]
    public string Type { get; set; } = "tun";

    [JsonProperty("tag")]
    public string Tag { get; set; } = "tun-in";

    [JsonProperty("interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    /// <summary>sing-box 1.12+: address is an array (replaces inet4_address/inet6_address)</summary>
    [JsonProperty("address")]
    public List<string> Address { get; set; } = new() { "172.19.0.1/30" };

    [JsonProperty("mtu")]
    public int Mtu { get; set; } = 9000;

    [JsonProperty("auto_route")]
    public bool AutoRoute { get; set; } = true;

    [JsonProperty("strict_route")]
    public bool StrictRoute { get; set; } = false;

    /// <summary>
    /// Exclude specific address ranges from TUN routing.
    /// Traffic to these addresses bypasses TUN and uses the system routing table.
    /// </summary>
    [JsonProperty("route_exclude_address", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? RouteExcludeAddress { get; set; }

    [JsonProperty("endpoint_independent_nat")]
    public bool EndpointIndependentNat { get; set; } = false;

    [JsonProperty("stack")]
    public string Stack { get; set; } = "system";
}
// Note: sniff + sniff_override_destination removed from inbound (deprecated since 1.11, removed in 1.13).
// Sniffing is now handled by route rule with action: "sniff".

// ─── Outbounds ────────────────────────────────────────────────────────────────

public class SingBoxOutbound
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("server", NullValueHandling = NullValueHandling.Ignore)]
    public string? Server { get; set; }

    [JsonProperty("server_port", NullValueHandling = NullValueHandling.Ignore)]
    public int? ServerPort { get; set; }

    [JsonProperty("uuid", NullValueHandling = NullValueHandling.Ignore)]
    public string? Uuid { get; set; }

    [JsonProperty("flow", NullValueHandling = NullValueHandling.Ignore)]
    public string? Flow { get; set; }

    [JsonProperty("tls", NullValueHandling = NullValueHandling.Ignore)]
    public TlsConfig? Tls { get; set; }

    [JsonProperty("transport", NullValueHandling = NullValueHandling.Ignore)]
    public TransportConfig? Transport { get; set; }

    /// <summary>
    /// sing-box 1.12+: DNS resolver tag for resolving the server hostname.
    /// Suppresses "missing domain_resolver in dial fields" deprecation warning.
    /// Must reference a tag from dns.servers.
    /// </summary>
    [JsonProperty("domain_resolver", NullValueHandling = NullValueHandling.Ignore)]
    public string? DomainResolver { get; set; }

    // ── URLTest outbound fields (for multi-server failover) ────────────────

    /// <summary>Tags of child outbounds (e.g. ["vless-0", "vless-1"]). Used when type=urltest.</summary>
    [JsonProperty("outbounds", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Outbounds { get; set; }

    /// <summary>Health check URL. Default: http://www.gstatic.com/generate_204</summary>
    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
    public string? Url { get; set; }

    /// <summary>Health check interval (e.g. "3m", "30s")</summary>
    [JsonProperty("interval", NullValueHandling = NullValueHandling.Ignore)]
    public string? Interval { get; set; }

    /// <summary>Latency tolerance in ms before switching servers</summary>
    [JsonProperty("tolerance", NullValueHandling = NullValueHandling.Ignore)]
    public int? Tolerance { get; set; }

    /// <summary>Whether to interrupt existing connections on server switch</summary>
    [JsonProperty("interrupt_exist_connections", NullValueHandling = NullValueHandling.Ignore)]
    public bool? InterruptExistConnections { get; set; }

    /// <summary>
    /// For type=direct: enables UDP fragmentation. Setting this to true makes the
    /// outbound "non-empty" so sing-box 1.13 accepts detour:"direct" pointing to it
    /// (otherwise FATAL: "detour to empty direct outbound makes no sense").
    /// </summary>
    [JsonProperty("udp_fragment", NullValueHandling = NullValueHandling.Ignore)]
    public bool? UdpFragment { get; set; }
}

public class TlsConfig
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("server_name")]
    public string ServerName { get; set; } = string.Empty;

    [JsonProperty("insecure")]
    public bool Insecure { get; set; } = false;

    /// <summary>Reality config — set when using VLESS Reality (replaces standard TLS)</summary>
    [JsonProperty("reality", NullValueHandling = NullValueHandling.Ignore)]
    public RealityConfig? Reality { get; set; }

    /// <summary>TLS fingerprint for uTLS/Reality: firefox | chrome | safari | etc.</summary>
    [JsonProperty("utls", NullValueHandling = NullValueHandling.Ignore)]
    public UtlsConfig? Utls { get; set; }
}

public class RealityConfig
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("public_key")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonProperty("short_id")]
    public string ShortId { get; set; } = string.Empty;
}

public class UtlsConfig
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>chrome | firefox | safari | ios | android | edge | 360 | qq | random | randomized</summary>
    [JsonProperty("fingerprint")]
    public string Fingerprint { get; set; } = "firefox";
}

public class TransportConfig
{
    [JsonProperty("type")]
    public string Type { get; set; } = "ws";

    [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
    public string? Path { get; set; }

    [JsonProperty("headers", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string>? Headers { get; set; }
}

// ─── Route ────────────────────────────────────────────────────────────────────

public class SingBoxRoute
{
    [JsonProperty("rules")]
    public List<RouteRule> Rules { get; set; } = new();

    [JsonProperty("final")]
    public string Final { get; set; } = "direct";

    [JsonProperty("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;

    /// <summary>
    /// sing-box 1.12+ requirement — suppresses deprecation warning.
    /// Must reference a tag from dns.servers.
    /// Will be required in sing-box 1.14.
    /// </summary>
    [JsonProperty("default_domain_resolver", NullValueHandling = NullValueHandling.Ignore)]
    public string? DefaultDomainResolver { get; set; }

    /// <summary>
    /// Rule sets — references to local .srs files (sing-box binary rule sets).
    /// Used for geo-based routing (geoip-ru, geosite-ru).
    /// </summary>
    [JsonProperty("rule_set", NullValueHandling = NullValueHandling.Ignore)]
    public List<RuleSetEntry>? RuleSet { get; set; }
}

/// <summary>
/// Local rule set definition (sing-box format: type=local, path to .srs file).
/// </summary>
public class RuleSetEntry
{
    [JsonProperty("type")]
    public string Type { get; set; } = "local";

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("format")]
    public string Format { get; set; } = "binary";

    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;
}

/// <summary>sing-box 1.12+ route rule — uses action-based format</summary>
public class RouteRule
{
    /// <summary>Match by inbound tag (e.g. "tun-in"). Used for sniff rule.</summary>
    [JsonProperty("inbound", NullValueHandling = NullValueHandling.Ignore)]
    public string? Inbound { get; set; }

    [JsonProperty("protocol", NullValueHandling = NullValueHandling.Ignore)]
    public string? Protocol { get; set; }

    [JsonProperty("process_name", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? ProcessName { get; set; }

    /// <summary>Match by network type: "tcp" | "udp". Null = match both.</summary>
    [JsonProperty("network", NullValueHandling = NullValueHandling.Ignore)]
    public string? Network { get; set; }

    [JsonProperty("ip_is_private", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IpIsPrivate { get; set; }

    /// <summary>
    /// New 1.12+ action-based format: "route" | "reject" | "hijack-dns" | "sniff" | "resolve"
    /// Use Action + Outbound together for route action.
    /// </summary>
    [JsonProperty("action", NullValueHandling = NullValueHandling.Ignore)]
    public string? Action { get; set; }

    /// <summary>Outbound tag — used when Action = "route"</summary>
    [JsonProperty("outbound", NullValueHandling = NullValueHandling.Ignore)]
    public string? Outbound { get; set; }

    /// <summary>Sniff timeout — used when Action = "sniff". Default "300ms".</summary>
    [JsonProperty("timeout", NullValueHandling = NullValueHandling.Ignore)]
    public string? Timeout { get; set; }

    /// <summary>Match by rule set tags (geoip-ru, geosite-ru, etc).</summary>
    [JsonProperty("rule_set", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? RuleSet { get; set; }
}

// ─── Experimental ─────────────────────────────────────────────────────────────

public class SingBoxExperimental
{
    [JsonProperty("clash_api")]
    public ClashApi ClashApi { get; set; } = new();
}

public class ClashApi
{
    [JsonProperty("external_controller")]
    public string ExternalController { get; set; } = "127.0.0.1:9090";
}
