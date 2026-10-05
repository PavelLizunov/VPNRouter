using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class VlessServerEntry
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "protocol")]
    public string Protocol { get; set; } = "vless";

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

    [YamlMember(Alias = "username")]
    public string Username { get; set; } = string.Empty;

    [YamlMember(Alias = "password")]
    public string Password { get; set; } = string.Empty;

    [YamlMember(Alias = "method")]
    public string Method { get; set; } = string.Empty;

    [YamlMember(Alias = "congestion_control")]
    public string CongestionControl { get; set; } = "bbr";

    [YamlMember(Alias = "udp_relay_mode")]
    public string UdpRelayMode { get; set; } = "native";

    [YamlMember(Alias = "obfs_type")]
    public string ObfsType { get; set; } = string.Empty;

    [YamlMember(Alias = "obfs_password")]
    public string ObfsPassword { get; set; } = string.Empty;

    [YamlMember(Alias = "hysteria_up_mbps")]
    public int HysteriaUpMbps { get; set; }

    [YamlMember(Alias = "hysteria_down_mbps")]
    public int HysteriaDownMbps { get; set; }

    [YamlMember(Alias = "awg")]
    public AwgConfig? Awg { get; set; }

    [YamlMember(Alias = "plugin")]
    public string Plugin { get; set; } = string.Empty;

    [YamlMember(Alias = "plugin_opts")]
    public string PluginOpts { get; set; } = string.Empty;

    [YamlMember(Alias = "pair")]
    public string PairGroup { get; set; } = string.Empty;

    [YamlMember(Alias = "outbound")]
    public string OutboundId { get; set; } = string.Empty;

    [YamlMember(Alias = "detour")]
    public string DetourVia { get; set; } = string.Empty;

    [YamlMember(Alias = "naive_quic")]
    public bool NaiveQuic { get; set; }

    [YamlMember(Alias = "dns_domain")]
    public string DnsDomain { get; set; } = string.Empty;

    [YamlMember(Alias = "dns_resolvers")]
    public List<string> DnsResolvers { get; set; } = new();

    [YamlMember(Alias = "dns_use_system_resolver")]
    public bool DnsUseSystemResolver { get; set; }

    [YamlMember(Alias = "dns_authoritative")]
    public List<string> DnsAuthoritative { get; set; } = new();

    [YamlMember(Alias = "dns_leaf_cert")]
    public string DnsLeafCertPem { get; set; } = string.Empty;

    [YamlMember(Alias = "dns_leaf_fingerprint")]
    public string DnsLeafFingerprint { get; set; } = string.Empty;
}

public class AwgConfig
{
    [YamlMember(Alias = "private_key")]
    public string PrivateKey { get; set; } = string.Empty;

    [YamlMember(Alias = "address")]
    public List<string> Address { get; set; } = new();

    [YamlMember(Alias = "peer_public_key")]
    public string PeerPublicKey { get; set; } = string.Empty;

    [YamlMember(Alias = "preshared_key")]
    public string PresharedKey { get; set; } = string.Empty;

    [YamlMember(Alias = "keepalive")]
    public int Keepalive { get; set; }

    [YamlMember(Alias = "jc")]   public int Jc { get; set; }
    [YamlMember(Alias = "jmin")] public int Jmin { get; set; }
    [YamlMember(Alias = "jmax")] public int Jmax { get; set; }
    [YamlMember(Alias = "s1")]   public int S1 { get; set; }
    [YamlMember(Alias = "s2")]   public int S2 { get; set; }
    [YamlMember(Alias = "s3")]   public int S3 { get; set; }
    [YamlMember(Alias = "s4")]   public int S4 { get; set; }
    [YamlMember(Alias = "h1")] public string H1 { get; set; } = string.Empty;
    [YamlMember(Alias = "h2")] public string H2 { get; set; } = string.Empty;
    [YamlMember(Alias = "h3")] public string H3 { get; set; } = string.Empty;
    [YamlMember(Alias = "h4")] public string H4 { get; set; } = string.Empty;
    [YamlMember(Alias = "i1")] public string I1 { get; set; } = string.Empty;
    [YamlMember(Alias = "i2")] public string I2 { get; set; } = string.Empty;
    [YamlMember(Alias = "i3")] public string I3 { get; set; } = string.Empty;
    [YamlMember(Alias = "i4")] public string I4 { get; set; } = string.Empty;
    [YamlMember(Alias = "i5")] public string I5 { get; set; } = string.Empty;

    [YamlMember(Alias = "header_protection_key")] public string HeaderProtectionKey { get; set; } = string.Empty;
    [YamlMember(Alias = "content_padding_addition")] public string ContentPaddingAddition { get; set; } = string.Empty;
    [YamlMember(Alias = "random_trailers")] public bool RandomTrailers { get; set; }
    [YamlMember(Alias = "disable_cookies")] public bool DisableCookies { get; set; }

    internal const int MaxJunkPacketCount = 128;
    internal const int MaxJunkPacketSize = 65507;

    internal string? FindLimitViolation()
    {
        if (Jc < 0 || Jc > MaxJunkPacketCount)
            return $"jc must be between 0 and {MaxJunkPacketCount} (got {Jc})";
        if (Jmin < 0 || Jmax < 0)
            return "jmin and jmax must not be negative";
        if (Jmax > MaxJunkPacketSize)
            return $"jmax must be at most {MaxJunkPacketSize} (got {Jmax})";
        if (Jmin > Jmax)
            return $"jmin must not exceed jmax (got jmin {Jmin}, jmax {Jmax})";
        return null;
    }
}
