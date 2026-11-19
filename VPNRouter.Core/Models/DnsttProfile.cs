namespace VPNRouter.Core.Models;

public sealed class DnsttProfile
{
    public string Domain { get; set; } = string.Empty;
    public string NoiseKey { get; set; } = string.Empty;
    public string SshHostKey { get; set; } = string.Empty;
    public string SshPrivateKey { get; set; } = string.Empty;
}
