using Newtonsoft.Json;

namespace VPNRouter.Core.Models;

public class Profile
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("processes")]
    public List<ProcessRule> Processes { get; set; } = new();

    [JsonProperty("dns_mode")]
    public string DnsMode { get; set; } = "vpn_only"; // vpn_only | smart | direct

    [JsonProperty("block_on_vpn_fail")]
    public bool BlockOnVpnFail { get; set; } = false;
}

public class ProfileCollection
{
    [JsonProperty("profiles")]
    public List<Profile> Profiles { get; set; } = new();
}
