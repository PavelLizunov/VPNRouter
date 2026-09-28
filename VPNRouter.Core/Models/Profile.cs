using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

public class Profile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("processes")]
    public List<ProcessRule> Processes { get; set; } = new();

    [JsonPropertyName("dns_mode")]
    public string DnsMode { get; set; } = "vpn_only";

    [JsonPropertyName("block_on_vpn_fail")]
    public bool BlockOnVpnFail { get; set; } = false;

    [JsonPropertyName("android_packages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> AndroidPackages { get; set; } = new();
}

public class ProfileCollection
{
    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();
}
