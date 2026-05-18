using System.Text.Json.Serialization;

namespace VPNRouter.Core.Models;

// Phase 3 — 3B (2026-05-18): migrated Profile/ProcessRule/ProfileCollection
// attributes from Newtonsoft.Json [JsonProperty(...)] to System.Text.Json
// [JsonPropertyName(...)]. All call sites (ProfileManager, App/ViewModels,
// Tests/ProfileManagerJsonDosGuardTests) migrated in the same pass. STJ
// is AOT-friendly + 2-5x faster + ships with the runtime — see brief
// plans/phase3-3B-newtonsoft-to-stj-2026-05-18.md.
//
// Wire-compat: existing on-disk profiles.json (snake_case keys: "name",
// "description", "processes", "dns_mode", "block_on_vpn_fail",
// "android_packages") round-trip byte-identical when written by STJ
// because we kept the exact same property names + use WriteIndented for
// the GitHubProfileSource cache file.

public class Profile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("processes")]
    public List<ProcessRule> Processes { get; set; } = new();

    [JsonPropertyName("dns_mode")]
    public string DnsMode { get; set; } = "vpn_only"; // vpn_only | smart | direct

    [JsonPropertyName("block_on_vpn_fail")]
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
    ///
    /// <para>Phase 3B (2026-05-18) STJ migration: switched from
    /// <c>[JsonProperty("android_packages", NullValueHandling.Ignore)]</c>
    /// to <c>[JsonPropertyName("android_packages")]</c> +
    /// <c>[JsonIgnore(Condition=WhenWritingNull)]</c>. Behaviour-preserving:
    /// null lists are still elided on write; default empty-list assignment
    /// in the auto-property means they never serialize as null anyway, but
    /// the attribute stays for forward-compat with hand-crafted profiles.</para>
    /// </summary>
    [JsonPropertyName("android_packages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> AndroidPackages { get; set; } = new();
}

public class ProfileCollection
{
    [JsonPropertyName("profiles")]
    public List<Profile> Profiles { get; set; } = new();
}
