#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

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

    [JsonPropertyName("endpoints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SingBoxEndpoint>? Endpoints { get; set; }

    [JsonPropertyName("route")]
    public SingBoxRoute Route { get; set; } = new();

    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SingBoxExperimental? Experimental { get; set; }
}

public class SingBoxEndpoint
{
    [JsonPropertyName("type")] public string Type { get; set; } = "wireguard";
    [JsonPropertyName("tag")] public string Tag { get; set; } = string.Empty;
    [JsonPropertyName("system")] public bool System { get; set; }

    [JsonPropertyName("mtu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Mtu { get; set; }

    [JsonPropertyName("address")] public List<string> Address { get; set; } = new();
    [JsonPropertyName("private_key")] public string PrivateKey { get; set; } = string.Empty;

    [JsonPropertyName("jc")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Jc { get; set; }
    [JsonPropertyName("jmin")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Jmin { get; set; }
    [JsonPropertyName("jmax")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Jmax { get; set; }
    [JsonPropertyName("s1")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int S1 { get; set; }
    [JsonPropertyName("s2")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int S2 { get; set; }
    [JsonPropertyName("s3")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int S3 { get; set; }
    [JsonPropertyName("s4")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int S4 { get; set; }
    [JsonPropertyName("h1")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? H1 { get; set; }
    [JsonPropertyName("h2")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? H2 { get; set; }
    [JsonPropertyName("h3")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? H3 { get; set; }
    [JsonPropertyName("h4")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? H4 { get; set; }
    [JsonPropertyName("i1")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? I1 { get; set; }
    [JsonPropertyName("i2")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? I2 { get; set; }
    [JsonPropertyName("i3")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? I3 { get; set; }
    [JsonPropertyName("i4")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? I4 { get; set; }
    [JsonPropertyName("i5")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? I5 { get; set; }

    [JsonPropertyName("header_protection_key")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HeaderProtectionKey { get; set; }
    [JsonPropertyName("content_padding_addition")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContentPaddingAddition { get; set; }
    [JsonPropertyName("random_trailers")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RandomTrailers { get; set; }
    [JsonPropertyName("disable_cookies")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DisableCookies { get; set; }

    [JsonPropertyName("peers")] public List<WireGuardPeer> Peers { get; set; } = new();
}

public class WireGuardPeer
{
    [JsonPropertyName("address")] public string Address { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("pre_shared_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreSharedKey { get; set; }

    [JsonPropertyName("allowed_ips")] public List<string> AllowedIps { get; set; } = new() { "0.0.0.0/0" };

    [JsonPropertyName("persistent_keepalive_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int PersistentKeepaliveInterval { get; set; }

    [JsonPropertyName("reserved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Reserved { get; set; }
}

public class SingBoxLog
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("timestamp")]
    public bool Timestamp { get; set; } = true;

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;
}

public class SingBoxDns
{
    [JsonPropertyName("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<DnsRule> Rules { get; set; } = new();

    [JsonPropertyName("final")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Final { get; set; }

    [JsonPropertyName("strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; set; } = "ipv4_only";
}

public class DnsServer
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "https";

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    [JsonPropertyName("server_port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServerPort { get; set; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("detour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detour { get; set; }

    [JsonPropertyName("domain_resolver")]
    [JsonConverter(typeof(DomainResolverValueConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DomainResolverValue? DomainResolver { get; set; }
}

public class DnsRule
{
    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcessName { get; set; }

    [JsonPropertyName("query_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? QueryType { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "route";

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; set; }

    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RuleSet { get; set; }

    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Domain { get; set; }

    [JsonPropertyName("domain_suffix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainSuffix { get; set; }

    [JsonPropertyName("domain_keyword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainKeyword { get; set; }
}

public class SingBoxInbound
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "tun";

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = "tun-in";

    [JsonPropertyName("interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    [JsonPropertyName("address")]
    public List<string> Address { get; set; } = new() { "172.19.0.1/30" };

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = TunSettings.DefaultMtu;

    [JsonPropertyName("auto_route")]
    public bool AutoRoute { get; set; } = true;

    [JsonPropertyName("strict_route")]
    public bool StrictRoute { get; set; } = false;

    [JsonPropertyName("route_exclude_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RouteExcludeAddress { get; set; }

    [JsonPropertyName("endpoint_independent_nat")]
    public bool EndpointIndependentNat { get; set; } = false;

    [JsonPropertyName("stack")]
    public string Stack { get; set; } = "system";
}

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

    [JsonPropertyName("detour")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detour { get; set; }

    [JsonPropertyName("tls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TlsConfig? Tls { get; set; }

    [JsonPropertyName("transport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransportConfig? Transport { get; set; }

    [JsonPropertyName("domain_resolver")]
    [JsonConverter(typeof(DomainResolverValueConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DomainResolverValue? DomainResolver { get; set; }

    [JsonPropertyName("tcp_keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TcpKeepAlive { get; set; }

    [JsonPropertyName("tcp_keep_alive_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TcpKeepAliveInterval { get; set; }

    [JsonPropertyName("outbounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Outbounds { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Interval { get; set; }

    [JsonPropertyName("tolerance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Tolerance { get; set; }

    [JsonPropertyName("interrupt_exist_connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InterruptExistConnections { get; set; }

    [JsonPropertyName("udp_fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? UdpFragment { get; set; }

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    [JsonPropertyName("quic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Quic { get; set; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("congestion_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CongestionControl { get; set; }

    [JsonPropertyName("udp_relay_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UdpRelayMode { get; set; }

    [JsonPropertyName("obfs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Hysteria2Obfs? Obfs { get; set; }

    [JsonPropertyName("up_mbps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UpMbps { get; set; }

    [JsonPropertyName("down_mbps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DownMbps { get; set; }

    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plugin { get; set; }

    [JsonPropertyName("plugin_opts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginOpts { get; set; }
}

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

    [JsonPropertyName("reality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealityConfig? Reality { get; set; }

    [JsonPropertyName("utls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UtlsConfig? Utls { get; set; }

    [JsonPropertyName("alpn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Alpn { get; set; }

    [JsonPropertyName("record_fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RecordFragment { get; set; }

    [JsonPropertyName("fragment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Fragment { get; set; }

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

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "firefox";
}

public class TransportConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ws";

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("service_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; set; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    [JsonPropertyName("x_padding_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? XPaddingBytes { get; set; }

    [JsonPropertyName("no_grpc_header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NoGrpcHeader { get; set; }
}

public class SingBoxRoute
{
    [JsonPropertyName("rules")]
    public List<RouteRule> Rules { get; set; } = new();

    [JsonPropertyName("final")]
    public string Final { get; set; } = "direct";

    [JsonPropertyName("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;

    [JsonPropertyName("default_domain_resolver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultDomainResolver { get; set; }

    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleSetEntry>? RuleSet { get; set; }
}

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

    [JsonPropertyName("update_interval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdateInterval { get; set; }
}

public class RouteRule
{
    [JsonPropertyName("inbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inbound { get; set; }

    [JsonPropertyName("protocol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Protocol { get; set; }

    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ProcessName { get; set; }

    [JsonPropertyName("network")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Network { get; set; }

    [JsonPropertyName("ip_is_private")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IpIsPrivate { get; set; }

    [JsonPropertyName("action")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }

    [JsonPropertyName("outbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outbound { get; set; }

    [JsonPropertyName("timeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Timeout { get; set; }

    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? RuleSet { get; set; }

    [JsonPropertyName("domain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Domain { get; set; }

    [JsonPropertyName("domain_suffix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainSuffix { get; set; }

    [JsonPropertyName("domain_keyword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? DomainKeyword { get; set; }

    [JsonPropertyName("ip_cidr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IpCidr { get; set; }

    [JsonIgnore]
    public bool IsInfrastructure { get; set; }

    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Port { get; set; }
}

public class SingBoxExperimental
{
    [JsonPropertyName("clash_api")]
    public ClashApi ClashApi { get; set; } = new();
}

public class ClashApi
{
    [JsonPropertyName("external_controller")]
    public string ExternalController { get; set; } = "127.0.0.1:9090";

    [JsonPropertyName("secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Secret { get; set; }
}
