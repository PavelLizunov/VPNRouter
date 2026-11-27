using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public sealed record TrustTunnelProfile
{
    [YamlMember(Alias = "quic")]
    public bool Quic { get; set; }

    [YamlMember(Alias = "udp")]
    public bool Udp { get; set; } = true;

    [YamlMember(Alias = "certificate_public_key_sha256", DefaultValuesHandling = DefaultValuesHandling.OmitNull)]
    public string? CertificatePublicKeySha256 { get; set; }
}

public sealed record MieruProfile
{
    [YamlMember(Alias = "transport")]
    public string Transport { get; set; } = "tcp";

    [YamlMember(Alias = "multiplexing")]
    public string Multiplexing { get; set; } = "default";

    [YamlMember(Alias = "handshake_mode")]
    public string HandshakeMode { get; set; } = "default";

    internal bool IsValid() =>
        Transport is "tcp" or "udp" &&
        Multiplexing is "default" or "off" or "low" or "middle" or "high" &&
        HandshakeMode is "default" or "standard" or "no_wait";
}
