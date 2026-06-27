using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

/// <summary>VLESS Reality settings (replaces TLS)</summary>
public class VlessRealityConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>SNI to mimic — must match a real TLS 1.3 site</summary>
    [YamlMember(Alias = "server_name")]
    public string ServerName { get; set; } = "yahoo.com";

    /// <summary>TLS fingerprint: chrome | firefox | safari | ios | android | edge | 360 | qq | random | randomized</summary>
    [YamlMember(Alias = "fingerprint")]
    public string Fingerprint { get; set; } = "firefox";

    /// <summary>Server public key (x25519)</summary>
    [YamlMember(Alias = "public_key")]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Short ID (hex, 0–16 chars)</summary>
    [YamlMember(Alias = "short_id")]
    public string ShortId { get; set; } = string.Empty;
}

public class VlessTlsConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = false;

    [YamlMember(Alias = "server_name")]
    public string ServerName { get; set; } = string.Empty;

    [YamlMember(Alias = "insecure")]
    public bool Insecure { get; set; } = false;

    /// <summary>uTLS fingerprint (chrome, firefox, safari, etc.)</summary>
    [YamlMember(Alias = "fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>ALPN negotiation (e.g. "http/1.1", "h2", "h2,http/1.1")</summary>
    [YamlMember(Alias = "alpn")]
    public string Alpn { get; set; } = string.Empty;
}

public class VlessTransportConfig
{
    /// <summary>tcp | ws | grpc | xhttp</summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "tcp";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "/";

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    // ── XHTTP (sing-box-lx with_xhttp) — only for type=xhttp ─────────────────
    /// <summary>XHTTP mode: auto | packet-up | stream-up | stream-one. Empty = auto.</summary>
    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>XHTTP Host-header override (top-level field, not in headers). Empty = none.</summary>
    [YamlMember(Alias = "host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>XHTTP X-Padding byte range, e.g. "100-1000". Empty = sing-box default.</summary>
    [YamlMember(Alias = "x_padding_bytes")]
    public string XPaddingBytes { get; set; } = string.Empty;

    /// <summary>XHTTP: omit Xray gRPC-style headers (forward-compat). Default false.</summary>
    [YamlMember(Alias = "no_grpc_header")]
    public bool NoGrpcHeader { get; set; }
}
