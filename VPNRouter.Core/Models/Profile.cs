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

    /// <summary>
    /// v3.0 Android: package IDs (com.discord, org.telegram.messenger, etc.)
    /// for VpnService.Builder.addAllowedApplication() calls. On Android,
    /// per-app routing is done at the OS layer by passing package names to
    /// VpnService.Builder, not via sing-box process_name rules (which don't
    /// translate to Android's app sandboxing model).
    ///
    /// <para>Profile catalogs are per-platform: <c>default.json</c> uses
    /// <see cref="Processes"/> with .exe names, <c>default-macos.json</c>
    /// uses Mach-O binary names in <see cref="Processes"/>,
    /// <c>default-linux.json</c> uses Unix-style binary names in
    /// <see cref="Processes"/>, and <c>default-android.json</c> uses
    /// <see cref="AndroidPackages"/> with reverse-DNS package IDs.</para>
    ///
    /// <para>Empty on non-Android profile catalogs. The Android catalog
    /// keeps <see cref="Processes"/> empty too — Android profiles only
    /// drive package-level routing.</para>
    /// </summary>
    [JsonProperty("android_packages", NullValueHandling = NullValueHandling.Ignore)]
    public List<string> AndroidPackages { get; set; } = new();
}

public class ProfileCollection
{
    [JsonProperty("profiles")]
    public List<Profile> Profiles { get; set; } = new();
}
