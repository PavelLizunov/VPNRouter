using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class VlessRealityConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "server_name")]
    public string ServerName { get; set; } = "yahoo.com";

    [YamlMember(Alias = "fingerprint")]
    public string Fingerprint { get; set; } = "firefox";

    [YamlMember(Alias = "public_key")]
    public string PublicKey { get; set; } = string.Empty;

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

    [YamlMember(Alias = "fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [YamlMember(Alias = "alpn")]
    public string Alpn { get; set; } = string.Empty;
}

public class VlessTransportConfig
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "tcp";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "/";

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string> Headers { get; set; } = new();

    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = string.Empty;

    [YamlMember(Alias = "host")]
    public string Host { get; set; } = string.Empty;

    [YamlMember(Alias = "x_padding_bytes")]
    public string XPaddingBytes { get; set; } = string.Empty;

    [YamlMember(Alias = "no_grpc_header")]
    public bool NoGrpcHeader { get; set; }
}
