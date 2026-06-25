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
    /// </summary>
    public const int CurrentSchemaVersion = 6;

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

public class TunSettings
{
    [YamlMember(Alias = "interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    [YamlMember(Alias = "ipv4_address")]
    public string Ipv4Address { get; set; } = "172.19.0.1/30";

    [YamlMember(Alias = "ipv6_enabled")]
    public bool Ipv6Enabled { get; set; } = false;

    // v2.42.0-r3: was 9000 (sing-box jumbo default). With stack=system that
    // 9000-byte TUN MTU put oversized HTTP/2 segments on the wire that the real
    // 1500-MTU path can't carry; with PMTUD broken they were RST -> browsers got
    // ERR_CONNECTION_CLOSED on YouTube/Google over TCP-only (VLESS) proxies while
    // small clients (curl --http1.1, PowerShell) squeaked through and UDP/QUIC
    // proxies bypassed it. 1280 (IPv6 minimum) traverses ANY path. Confirmed via
    // diagnose.ps1 on a real user (h2 FAIL + tun mtu 9000). SettingsMigrator
    // v5->v6 lowers existing 9000 configs.
    [YamlMember(Alias = "mtu")]
    public int Mtu { get; set; } = 1280;

    [YamlMember(Alias = "auto_route")]
    public bool AutoRoute { get; set; } = true;

    [YamlMember(Alias = "strict_route")]
    public bool StrictRoute { get; set; } = false;

    /// <summary>
    /// Exclude specific address ranges from TUN routing.
    /// Traffic to these addresses bypasses TUN and uses the system routing table.
    /// Useful for coexistence with other VPNs (e.g. WireGuard/AmneziaWG subnets).
    /// Example: ["10.9.1.0/24", "192.168.100.0/24"]
    ///
    /// <para>This is the USER-AUTHORED list and the ONLY one persisted to
    /// config.yaml. Auto-detected WG/AWG subnets live in the runtime-only
    /// <see cref="AutoDetectedExcludeAddress"/> and are folded in at
    /// config-generation time via <see cref="GetEffectiveRouteExcludeAddress"/>
    /// — they are deliberately never merged here, so a vanished adapter or a
    /// network change can never leave a stale exclude persisted forever.</para>
    /// </summary>
    [YamlMember(Alias = "route_exclude_address")]
    public List<string> RouteExcludeAddress { get; set; } = new();

    /// <summary>
    /// WireGuard/AmneziaWG subnets auto-detected from the live network
    /// interfaces (see <see cref="VPNRouter.Core.Services.NetworkInterfaceDetector"/>),
    /// recomputed fresh on every connect by the startup pipeline.
    ///
    /// <para><b>RUNTIME-ONLY — never serialized.</b> Marked <see cref="YamlIgnoreAttribute"/>
    /// (+ <see cref="JsonIgnoreAttribute"/> for defence-in-depth) precisely
    /// because the previous design merged these into the persisted
    /// <see cref="RouteExcludeAddress"/> additively-and-never-pruned: once an
    /// auto-detected subnet (e.g. 10.9.1.0/24 widened from a /32 point-to-point)
    /// was written to config.yaml it survived forever, even after the WG/AWG
    /// adapter was gone or the user moved networks — sending that subnet DIRECT
    /// (bypassing the VPN) where it shouldn't, or excluding a now-unrelated LAN
    /// range. Keeping the auto set out-of-band means it is present only while
    /// the adapter is, and is dropped the instant it isn't.</para>
    /// </summary>
    [YamlIgnore]
    [JsonIgnore]
    public List<string> AutoDetectedExcludeAddress { get; set; } = new();

    /// <summary>
    /// The EFFECTIVE TUN route-exclude set used to build the sing-box config
    /// and the TUN-change fingerprint: the persisted user list
    /// (<see cref="RouteExcludeAddress"/>) followed by the freshly
    /// auto-detected WG/AWG subnets (<see cref="AutoDetectedExcludeAddress"/>),
    /// de-duplicated case-insensitively. User entries are preserved verbatim
    /// and win on collision; the persisted list itself is never mutated.
    /// </summary>
    public List<string> GetEffectiveRouteExcludeAddress()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // User-authored entries first, preserved verbatim so the generated
        // config wire-format for an unchanged user list is byte-identical.
        if (RouteExcludeAddress != null)
        {
            foreach (var s in RouteExcludeAddress)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (seen.Add(s.Trim())) result.Add(s);
            }
        }

        // Freshly auto-detected WG/AWG subnets, only those not already covered
        // by a user entry (case/whitespace-insensitive).
        if (AutoDetectedExcludeAddress != null)
        {
            foreach (var s in AutoDetectedExcludeAddress)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (seen.Add(s.Trim())) result.Add(s);
            }
        }

        return result;
    }
}
