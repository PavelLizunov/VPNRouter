using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class AppSettings
{
    /// <summary>
    /// Schema version for forward-compatibility migrations. Bumped only
    /// when the yaml layout changes in a breaking way (renamed field,
    /// dropped section, etc.). Older configs get picked up by
    /// <see cref="VPNRouter.Core.Services.SettingsMigrator"/> and
    /// rewritten to the current schema on load.
    ///
    /// <para>v3 bump (2026-05-11, AM-1 + F-B): adds
    /// <see cref="AppConfig.RoutingAppsMode"/> + the include/exclude
    /// split lists, and seeds the F-B legacy <c>vless.servers</c>
    /// cleanup pass for users with shadow-override entries from a
    /// pre-subscription manual paste. See
    /// <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §1 Fix-B / §2.</para>
    ///
    /// <para>v4 bump (2026-05-12, W-2): wgturn-cli binary moved from
    /// shared <c>bin/</c> into dedicated <c>wgturn/bin/</c> subtree
    /// (parallel to <c>zapret/</c>, <c>tg-proxy/</c>) ahead of the W-1
    /// on-demand download flow. Migration moves any pre-existing
    /// binary + version stamp out of the legacy location. See
    /// <c>plans/wgturn-on-demand-download.md</c> §3 + §5.</para>
    ///
    /// <para>v5 bump (2026-05-19, Wave 39): adds
    /// <see cref="AppConfig.DnsLeakLockdown"/> firewall-level DNS-port
    /// block toggle. <strong>BR-10 (2026-05-20, post-v2.35.0)</strong>:
    /// default is <c>false</c> for ALL installs (fresh + upgrade). User
    /// must explicitly enable via Settings → Leak Protection. Original
    /// BR-5 default-on was too disruptive for LAN-DNS-proxy users
    /// (dnscrypt-proxy, AdGuard Home on a sibling NIC); sing-box already
    /// routes app DNS via VLESS:443 (DoH), so the firewall block is
    /// belt-and-suspenders defense-in-depth, not a baseline requirement.
    /// See <c>plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md</c>.</para>
    ///
    /// <para>v7 bump (2026-06-27, Roblox realtime UDP): clamps only the
    /// known legacy <c>tun.mtu: 1500</c> value to 1280 for existing configs.
    /// Other explicit MTU values are preserved.</para>
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    [YamlMember(Alias = "schema_version")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [YamlMember(Alias = "app")]
    public AppConfig App { get; set; } = new();

    [YamlMember(Alias = "profile_sources")]
    public List<ProfileSource> ProfileSources { get; set; } = new();

    [YamlMember(Alias = "active_profile")]
    public string ActiveProfile { get; set; } = string.Empty;

    [YamlMember(Alias = "vless")]
    public VlessConfig Vless { get; set; } = new();

    [YamlMember(Alias = "tun")]
    public TunSettings Tun { get; set; } = new();

    [YamlMember(Alias = "dns")]
    public DnsSettings Dns { get; set; } = new();

    [YamlMember(Alias = "singbox")]
    public SingBoxSettings SingBox { get; set; } = new();

    [YamlMember(Alias = "monitoring")]
    public MonitoringSettings Monitoring { get; set; } = new();

    /// <summary>
    /// Custom app exe names added by user via GUI (e.g. ["spotify.exe", "slack.exe"]).
    /// These are added as a "_custom" profile with each exe as a process rule.
    /// </summary>
    [YamlMember(Alias = "custom_apps")]
    public List<string> CustomApps { get; set; } = new();

    /// <summary>
    /// User-added apps per group (Discord, Browsers, etc.). Merged with
    /// defaults from profiles/default.json on load. Allows adding/removing
    /// custom apps in any group without touching the bundled defaults.
    /// </summary>
    [YamlMember(Alias = "custom_group_apps")]
    public Dictionary<string, List<string>> CustomGroupApps { get; set; } = new();

    /// <summary>User-created categories (beyond the bundled default groups).</summary>
    [YamlMember(Alias = "custom_categories")]
    public List<CustomCategory> CustomCategories { get; set; } = new();

    /// <summary>
    /// v2.32.x (Bug-r9-I): process names the user has individually unchecked
    /// inside an active default profile group (e.g. unchecked Firefox inside
    /// the Browsers group so RU sites stay on the direct route). Pre-r9-I the
    /// per-app checkbox was a transient view state — only the group's
    /// IsChecked was persisted via <see cref="ActiveProfile"/>. The
    /// unchecked specific app reverted on every restart. Persisting the
    /// exclusion list closes that gap.
    ///
    /// <para>Entries are stored in the same form as
    /// <c>AppItemViewModel.ProcessName</c> (with <c>.exe</c> suffix on
    /// Windows, stripped on macOS/Linux). <see cref="VPNRouter.Core.Services.VpnEngine"/>
    /// normalises both sides at filter time so the suffix variance does
    /// not matter at the engine layer.</para>
    /// </summary>
    [YamlMember(Alias = "excluded_apps")]
    public List<string> ExcludedApps { get; set; } = new();

    [YamlMember(Alias = "update")]
    public UpdateSettings Update { get; set; } = new();

    /// <summary>
    /// r9 Phase 2 — emergency fallback channel settings (wgturn-core
    /// integration). Persists the share URL + VK link the user pasted
    /// in the future Phase-3 UI. The actual lifecycle service is
    /// <see cref="VPNRouter.Core.Services.EmergencyChannel.EmergencyChannelEngine"/>.
    /// </summary>
    [YamlMember(Alias = "emergency_channel")]
    public EmergencyChannelSettings EmergencyChannel { get; set; } = new();
}
