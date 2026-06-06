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
    public const int CurrentSchemaVersion = 5;

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

/// <summary>
/// r9 Phase 2 — persisted state for the wgturn-core emergency channel.
/// Stored in <c>config.yaml</c> alongside the rest of AppSettings so
/// the user doesn't have to re-paste the share URL every launch. The
/// VK link is also persisted but typically gets re-pasted per session
/// since each VK call uses a fresh invite.
/// </summary>
public class EmergencyChannelSettings
{
    /// <summary>True ⇒ user has opted into the emergency channel
    /// feature. Default false — Phase 3 UI flips this when the user
    /// connects for the first time.</summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Share URL from the wgturn server (<c>wgturn://...</c>).
    /// Nullable so an empty config doesn't serialise a placeholder.</summary>
    [YamlMember(Alias = "wgturn_url")]
    public string? WgturnUrl { get; set; }

    /// <summary>VK Calls invite (<c>https://vk.com/call/join/...</c>).
    /// Optional — typically supplied at runtime per session.</summary>
    [YamlMember(Alias = "vk_link")]
    public string? VkLink { get; set; }

    /// <summary>
    /// W-4 — list of named wgturn share URLs the user has saved (e.g.
    /// <c>Operator-A</c>, <c>Operator-B</c>, <c>Personal</c>). Surfaced
    /// in the Tools tab Emergency Channel card as a ComboBox so the
    /// user can pick one per session without re-pasting the
    /// <c>wgturn://</c> URL each time. Empty until the user adds their
    /// first entry via <c>+ Add</c>.
    /// </summary>
    [YamlMember(Alias = "configs")]
    public List<WgturnEntry> Configs { get; set; } = new();

    /// <summary>
    /// W-4 — name of the entry from <see cref="Configs"/> that should
    /// be pre-selected when the user opens the Tools tab. Empty when
    /// no entry is selected (e.g. first-run, after deleting the active
    /// one).
    /// </summary>
    [YamlMember(Alias = "active_config")]
    public string ActiveConfig { get; set; } = string.Empty;

    /// <summary>
    /// W-4 — last VK Calls invite link the user pasted into the
    /// Emergency Channel card. Persisted so reopening the app
    /// pre-fills the input. Each call typically needs a fresh link, but
    /// keeping the last one saves a paste during quick reconnect.
    /// </summary>
    [YamlMember(Alias = "last_vk_link")]
    public string LastVkLink { get; set; } = string.Empty;
}

public class AppConfig
{
    [YamlMember(Alias = "log_level")]
    public string LogLevel { get; set; } = "info";

    [YamlMember(Alias = "log_file")]
    public string LogFile { get; set; } = @"%ProgramData%\VPNRouter\logs\vpnrouter.log";

    /// <summary>
    /// Routing mode: "split" routes only selected apps through VPN,
    /// "full" routes ALL traffic through VPN (except private IPs).
    /// </summary>
    [YamlMember(Alias = "routing_mode")]
    public string RoutingMode { get; set; } = "split";

    /// <summary>
    /// v2.32.x (AM-1, 2026-05-11): per-app routing mode within split tunnel.
    /// <list type="bullet">
    /// <item><c>"include"</c> (default, legacy behaviour) — selected apps
    /// listed in <see cref="RoutingAppsInclude"/> are routed through the
    /// VPN, everything else goes direct. sing-box gets
    /// <c>{process_name: [..], action: route, outbound: proxy}</c> +
    /// <c>route.final = "direct"</c>.</item>
    /// <item><c>"exclude"</c> — selected apps listed in
    /// <see cref="RoutingAppsExclude"/> are kept on the direct route,
    /// everything else goes through the VPN. sing-box gets
    /// <c>{process_name: [..], action: route, outbound: direct}</c> +
    /// <c>route.final = "proxy"</c>. Useful when most apps want VPN but
    /// a few (RU bank, Steam, vendor-specific client) must stay on the
    /// direct route.</item>
    /// </list>
    ///
    /// <para>Storage uses separate include/exclude lists (option B in
    /// the plan) so toggling the mode preserves the user's selection
    /// from the other mode — useful when comparing routing layouts
    /// during testing.</para>
    ///
    /// <para>Only meaningful when <see cref="RoutingMode"/> is "split";
    /// "full" tunnel routes everything through the VPN regardless and
    /// the per-app list is ignored.</para>
    ///
    /// <para>See <c>plans/r10-stas-confirmed-and-apps-2mode.md</c> §2
    /// for the rationale, schema decision, and acceptance criteria.</para>
    /// </summary>
    [YamlMember(Alias = "routing_apps_mode")]
    public string RoutingAppsMode { get; set; } = "include";

    /// <summary>
    /// v2.32.x (AM-1): apps routed through the VPN when
    /// <see cref="RoutingAppsMode"/> is "include". Each entry is a
    /// process executable name (e.g. <c>chrome.exe</c> on Windows,
    /// <c>firefox</c> on Linux). Empty when the user has not yet
    /// selected anything OR when the per-app selection is currently
    /// driven by the legacy profile system (<see cref="VPNRouter.Core.Models.Profile.Processes"/>).
    ///
    /// <para>Migrator copies legacy <see cref="CustomApps"/> into this
    /// list on v2→v3 upgrade so first-time-after-upgrade users see
    /// their previous selection.</para>
    /// </summary>
    [YamlMember(Alias = "routing_apps_include")]
    public List<string> RoutingAppsInclude { get; set; } = new();

    /// <summary>
    /// v2.32.x (AM-1): apps kept on the direct route (NOT routed via
    /// VPN) when <see cref="RoutingAppsMode"/> is "exclude". Same
    /// process-name format as <see cref="RoutingAppsInclude"/>. Empty
    /// when the user has not yet selected anything for exclusion.
    /// </summary>
    [YamlMember(Alias = "routing_apps_exclude")]
    public List<string> RoutingAppsExclude { get; set; } = new();

    /// <summary>UI theme preference: "light", "dark", or "system" (follow the
    /// OS appearance). v2.40.x (Fix #7): default is "system" so fresh installs
    /// match the OS theme on first launch — fixing the macOS "app starts light
    /// while macOS is in Dark" desync. Existing users keep whatever explicit
    /// "light"/"dark" they already have persisted (their choice wins).</summary>
    [YamlMember(Alias = "theme")]
    public string Theme { get; set; } = "system";

    /// <summary>UI language: "en" or "ru". Empty string means "never chose
    /// one yet" → UI auto-detects from OS locale on first launch (v2.24.4)
    /// and persists the result. User can still change via menu.</summary>
    [YamlMember(Alias = "language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// UI complexity mode. "simple" = one-page onboarding for non-technical
    /// users (v2.17+); "advanced" = the full tabbed layout we shipped in
    /// v2.15/v2.16.
    ///
    /// Default is "advanced" until v2.17.5 when SimplePage is fully wired.
    /// v2.17.5 flips the default to "simple" AND promotes existing users
    /// with non-empty Subscriptions / Vless.Servers / CustomConfigs to
    /// "advanced" automatically (so nobody's workflow regresses).
    /// Until then, only users who explicitly click the Advanced↔Simple
    /// toggle see the SimplePage scaffolding.
    /// </summary>
    [YamlMember(Alias = "ui_mode")]
    public string UiMode { get; set; } = "advanced";

    /// <summary>
    /// Config generation mode:
    /// "generated" — build sing-box config from VLESS settings + profiles (default).
    /// "custom" — use a user-provided sing-box JSON config, inject process routing only.
    /// </summary>
    [YamlMember(Alias = "config_mode")]
    public string ConfigMode { get; set; } = "generated";

    /// <summary>
    /// Path to custom sing-box JSON config (used when config_mode = "custom").
    /// Supports environment variables (e.g. %ProgramData%\VPNRouter\custom.json).
    /// </summary>
    [YamlMember(Alias = "custom_config")]
    public string CustomConfig { get; set; } = string.Empty;

    /// <summary>
    /// List of saved custom sing-box configs. Each entry has a name and path
    /// to a ProgramData copy. Configs are copied on import so originals can be deleted.
    /// </summary>
    [YamlMember(Alias = "custom_configs")]
    public List<CustomConfigEntry> CustomConfigs { get; set; } = new();

    /// <summary>Name of the currently active custom config (from CustomConfigs list).</summary>
    [YamlMember(Alias = "active_custom_config")]
    public string ActiveCustomConfig { get; set; } = string.Empty;

    /// <summary>
    /// Subscription URL (e.g. https://ninitux.com/api/v1/app/config/{device_id}).
    /// Returns base64-encoded VLESS URIs. Used when config_mode = "subscribe".
    /// </summary>
    [YamlMember(Alias = "subscription_url")]
    public string SubscriptionUrl { get; set; } = string.Empty;

    /// <summary>
    /// Servers fetched from subscription (cached for offline startup).
    /// Separate from Vless.Servers which holds manually-added servers.
    /// </summary>
    [YamlMember(Alias = "subscription_servers")]
    public List<VlessServerEntry> SubscriptionServers { get; set; } = new();

    /// <summary>Active subscription server name (like Vless.ActiveServer for manual).</summary>
    [YamlMember(Alias = "active_subscription_server")]
    public string ActiveSubscriptionServer { get; set; } = string.Empty;

    /// <summary>
    /// Multiple subscription URLs support. Each has its own server list, refresh state,
    /// and enabled flag. Legacy SubscriptionUrl migrates to Subscriptions[0] on first load.
    /// </summary>
    [YamlMember(Alias = "subscriptions")]
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    /// <summary>
    /// When true, traffic to Russian sites/IPs is routed directly (real IP),
    /// not through VPN. Protects VPN server from being blacklisted by RU services
    /// and unblocks RU sites that geo-restrict non-RU IPs.
    /// </summary>
    [YamlMember(Alias = "bypass_russian_traffic")]
    public bool BypassRussianTraffic { get; set; } = true;

    /// <summary>
    /// v2.29.0: user-defined direct-routing rules. Each rule matches a
    /// destination (domain / IP / CIDR / port) and routes it OUT of the
    /// VPN tunnel (action: direct).
    ///
    /// <para>v2.30.0 SUPERSEDED by <see cref="CustomRules"/> which
    /// supports direct + proxy + block actions. <see cref="CustomDirectRules"/>
    /// remains in schema for back-compat with v2.29.0-r4..r8 configs;
    /// <see cref="SettingsMigrator"/> auto-migrates to <see cref="CustomRules"/>
    /// on first run after upgrade. After migration the field is left empty
    /// but kept (drop in v2.32+ once enough users have upgraded past).</para>
    /// </summary>
    [YamlMember(Alias = "custom_direct_rules")]
    public List<CustomDirectRule> CustomDirectRules { get; set; } = new();

    /// <summary>
    /// v2.30.0: full custom-rules engine. Each rule has an Action
    /// (direct / proxy / block), a match type (domain / domain_suffix /
    /// domain_keyword / ip_cidr / port / port_range / network /
    /// process_name / geosite / geoip), a value (comma-separated for
    /// multi-value), an optional comment, and an enabled flag.
    ///
    /// <para>Rule order matters — sing-box uses first-match-wins. The
    /// generator inserts these rules AFTER built-in always-on rules
    /// (sniff, hijack-dns, ip_is_private) and AFTER toggle-driven rules
    /// (BypassRussianTraffic, BlockAds — both higher priority per user
    /// direction 2026-04-29 «toggles остаются и всегда приоритетнее»),
    /// but BEFORE auto-generated process_name → proxy and the final
    /// route. So a user direct rule wins over app-selection proxy
    /// routing, but loses to BypassRussianTraffic if both match.</para>
    /// </summary>
    [YamlMember(Alias = "custom_rules")]
    public List<CustomRule> CustomRules { get; set; } = new();

    /// <summary>
    /// v2.30.0-r17 — priority order for custom rules vs global toggles.
    /// User direction 2026-04-29: «block не работает на ru-домены
    /// которые в правиле выше [BypassRu]; хочу чтоб кастомные правила
    /// были выше или переключатель что брать в приоритет».
    ///
    /// <list type="bullet">
    /// <item><c>"toggles_first"</c> (default) — global BypassRussianTraffic
    /// + BlockAds toggles win over custom rules. Same as v2.30.0 r1-r16
    /// behavior. Custom direct/proxy/block can be shadowed by toggles
    /// if both match the same domain.</item>
    /// <item><c>"custom_first"</c> — custom rules win over global toggles.
    /// E.g. a custom <c>block .sberbank.ru</c> will fire even if
    /// BypassRu has a generic geosite-RU rule pointing to direct.</item>
    /// </list>
    ///
    /// <para>Implemented in <see cref="VPNRouter.Core.Services.ConfigGenerator"/>
    /// by reordering the Apply* calls (each insert pushes earlier inserts
    /// down, so the LAST Apply* ends up first in the rule list).</para>
    /// </summary>
    [YamlMember(Alias = "custom_rules_priority")]
    public string CustomRulesPriority { get; set; } = "toggles_first";

    /// <summary>
    /// When true, force IPv4-only DNS resolution to prevent IPv6 leaks
    /// when VPN tunnels only IPv4. Recommended unless you specifically need IPv6.
    /// </summary>
    [YamlMember(Alias = "force_ipv4_only")]
    public bool ForceIpv4Only { get; set; } = true;

    /// <summary>
    /// When true (default), reject QUIC (HTTP/3 over UDP) whenever the active
    /// proxy is TCP-only — i.e. a VLESS+Reality outbound with no UDP-capable
    /// (TUIC/Hysteria2) sibling. QUIC carried over a reliable VLESS-over-TCP
    /// tunnel suffers head-of-line blocking ("TCP-over-TCP meltdown"), which is
    /// the classic cause of YouTube/Google-video stalls on a VLESS VPN. A clean
    /// reject makes the browser fall back to HTTP/2-over-TCP, which rides the
    /// tunnel cleanly. Rejection is scoped: LAN/private-IP QUIC is routed direct
    /// before the reject, and setups with a UDP-capable outbound are left alone.
    /// Set false to restore raw QUIC tunneling (degraded UDP-over-VLESS).
    /// </summary>
    [YamlMember(Alias = "block_quic_on_tcp_proxy")]
    public bool BlockQuicOnTcpProxy { get; set; } = true;

    /// <summary>
    /// "Strict mode" — when true, HealthMonitor polls sing-box every 5 seconds
    /// instead of 30, so a crash is detected faster and the firewall kill switch
    /// activates sooner. Reduces the leak window from ~30s to ~5s.
    /// Note: process exit events fire immediately regardless of this setting,
    /// so detection of clean exits is unaffected. This option only helps with
    /// silent hangs (sing-box alive but Clash API not responding).
    /// </summary>
    [YamlMember(Alias = "strict_mode")]
    public bool StrictMode { get; set; } = false;

    /// <summary>
    /// v2.13.19 — one-time dismissible flag for Free Configs privacy warning.
    /// Reset via Settings/Network if user wants to see the reminder again.
    /// </summary>
    [YamlMember(Alias = "free_config_security_warning_acked")]
    public bool FreeConfigSecurityWarningAcked { get; set; } = false;

    /// <summary>
    /// v2.14.4 — user-provided VLESS source URLs (private subscriptions).
    /// Merged with built-in sources during Refresh. Each entry has name, URL, enabled flag.
    /// </summary>
    [YamlMember(Alias = "user_free_sources")]
    public List<UserFreeSource> UserFreeSources { get; set; } = new();

    /// <summary>
    /// "Strict DNS" — when true, ALL DNS queries are routed through the VPN
    /// (vpn-dns), not just queries from routed processes. Eliminates DNS leaks
    /// from system services (svchost DnsCache), background apps, and any process
    /// not in the routed list.
    ///
    /// Tradeoffs:
    /// - +50-100ms latency per DNS query (round-trip via VPN)
    /// - Local network resolution (printer.local, nas.lan) may break
    /// - DNS stops working if VPN disconnects (de facto DNS kill switch)
    ///
    /// Recommended if leak tests show ISP DNS appearing despite VPN being on.
    /// </summary>
    [YamlMember(Alias = "strict_dns")]
    public bool StrictDns { get; set; } = false;

    /// <summary>
    /// Block ads, trackers and malware at DNS + routing level.
    /// When enabled: VPN DNS switches to AdGuard DNS + adblock rule_set is added.
    /// </summary>
    [YamlMember(Alias = "block_ads")]
    public bool BlockAds { get; set; } = false;

    /// <summary>DPI bypass via zapret (winws.exe). Windows-only.</summary>
    [YamlMember(Alias = "zapret_enabled")]
    public bool ZapretEnabled { get; set; } = false;

    /// <summary>Zapret strategy: "multisplit", "fake+multisplit", "fake+disorder", "custom"</summary>
    [YamlMember(Alias = "zapret_strategy")]
    public string ZapretStrategy { get; set; } = "multisplit";

    /// <summary>Custom zapret arguments (used when ZapretStrategy="custom")</summary>
    [YamlMember(Alias = "zapret_custom_args")]
    public string ZapretCustomArgs { get; set; } = string.Empty;

    /// <summary>Telegram MTProto proxy (tg-ws-proxy) enabled state.</summary>
    [YamlMember(Alias = "tg_proxy_enabled")]
    public bool TgProxyEnabled { get; set; } = false;

    /// <summary>Telegram proxy listen port (default 1443).</summary>
    [YamlMember(Alias = "tg_proxy_port")]
    public int TgProxyPort { get; set; } = 1443;

    /// <summary>Telegram proxy secret (32 hex chars, auto-generated if empty).</summary>
    [YamlMember(Alias = "tg_proxy_secret")]
    public string TgProxySecret { get; set; } = string.Empty;

    // ── Autostart ──

    /// <summary>Auto-start VPN connection when Windows Service starts.</summary>
    [YamlMember(Alias = "autostart_vpn")]
    public bool AutostartVpn { get; set; } = false;

    /// <summary>Auto-start Zapret (DPI bypass) when Windows Service starts.</summary>
    [YamlMember(Alias = "autostart_zapret")]
    public bool AutostartZapret { get; set; } = false;

    /// <summary>Auto-start TgProxy (Telegram MTProto proxy) when Windows Service starts.</summary>
    [YamlMember(Alias = "autostart_tgproxy")]
    public bool AutostartTgProxy { get; set; } = false;

    /// <summary>Auto-start GUI minimized to tray on Windows logon (HKCU\Run).</summary>
    [YamlMember(Alias = "autostart_ui")]
    public bool AutostartUi { get; set; } = false;

    /// <summary>
    /// When true, flush system DNS cache when VPN starts to prevent
    /// resolved-pre-connect entries from leaking through direct route.
    /// </summary>
    [YamlMember(Alias = "flush_dns_on_start")]
    public bool FlushDnsOnStart { get; set; } = true;

    /// <summary>
    /// v2.35.0-r5 Wave 39 (2026-05-19): blocks outbound DNS (UDP/TCP 53 +
    /// TCP 853) on all non-loopback interfaces while VPN is connected,
    /// preventing the Windows DNS Client from leaking queries to ISP DNS
    /// resolvers despite our SMHNR/ParallelAAAA hardening
    /// (<see cref="VPNRouter.Core.Services.WindowsDnsHardening"/>).
    ///
    /// <para>Root cause: even with SMHNR + ParallelAAAA registry hardening
    /// applied, Windows 11 22H2+ still races multiple resolvers in parallel
    /// under specific conditions (TUN metric ties, NetworkLocationAwareness
    /// classifying TUN as private, partial IPv6, etc.). The brat user-report
    /// 2026-05-19 showed 119:119:119 hits to 3 Russian ISP resolvers via
    /// ipleak.net despite an active VLESS+Reality VPN — proof of full DNS
    /// privacy regression. The only foolproof block is a firewall-level
    /// outbound block on the DNS ports; sing-box's DNS flow goes via VLESS
    /// outbound on port 443 (DoH to AdGuard/Cloudflare) so port 53/853
    /// blocks do not affect the legitimate VPN-side DNS path.</para>
    ///
    /// <para><strong>BR-10 (2026-05-20)</strong>: default OFF for ALL
    /// installs (was BR-5 default-on for new installs in v2.35.0). User
    /// opts in via Settings → Leak Protection. Rationale: sing-box
    /// already routes app DNS via VLESS:443 (DoH AdGuard/Cloudflare), so
    /// the firewall block is belt-and-suspenders, not baseline. LAN-DNS-
    /// proxy users (dnscrypt-proxy, AdGuard Home on sibling NIC) lose
    /// DNS when this is on, so default-on surprised non-power users.
    /// UI toggle lives on the Settings page. See
    /// <c>plans/hotfix-dns-leak-firewall-lockdown-2026-05-19.md</c>
    /// for the full rationale, threat model, and known limitations.</para>
    /// </summary>
    [YamlMember(Alias = "dns_leak_lockdown")]
    [JsonPropertyName("dns_leak_lockdown")]
    public bool DnsLeakLockdown { get; set; } = false;

    /// <summary>
    /// v2.32.3 (2026-05-17): one-shot counter populated by
    /// <see cref="VPNRouter.Core.Services.SettingsMigrator.PruneKnownPlaceholders"/>
    /// when the load-time placeholder-fingerprint sweep removed at least one
    /// stale credential from <see cref="VlessConfig.Server"/> scalars,
    /// <see cref="VlessConfig.Servers"/>, or
    /// <see cref="SubscriptionEntry.Servers"/>. Persisted across saves so
    /// the desktop UI can surface a "we cleaned N placeholder entries"
    /// banner once and then call
    /// <see cref="VPNRouter.Core.Services.SettingsLoader.ConsumePlaceholderPruneNotice"/>
    /// to clear it. Default 0 — old yamls without the field deserialize
    /// cleanly and never trigger the banner.
    /// </summary>
    [YamlMember(Alias = "placeholder_prune_count")]
    public int PlaceholderPruneCount { get; set; }

    /// <summary>
    /// v2.32.3 (2026-05-17): ISO-8601 UTC timestamp recorded alongside
    /// <see cref="PlaceholderPruneCount"/> for forensic correlation against
    /// vpnrouter*.log entries. String-typed (not <see cref="DateTimeOffset"/>)
    /// to keep the yaml round-trip lossless on locales whose
    /// YamlDotNet date formatter disagrees with the chosen invariant
    /// representation. Empty when no prune has happened.
    /// </summary>
    [YamlMember(Alias = "placeholder_prune_at_utc")]
    public string PlaceholderPruneAtUtc_Str { get; set; } = string.Empty;
}

/// <summary>A single VLESS subscription source (URL + its servers).</summary>
public class SubscriptionEntry
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "last_refreshed_at")]
    public DateTimeOffset? LastRefreshedAt { get; set; }

    [YamlMember(Alias = "last_server_count")]
    public int LastServerCount { get; set; }

    [YamlMember(Alias = "servers")]
    public List<VlessServerEntry> Servers { get; set; } = new();
}

/// <summary>A user-created Applications category.</summary>
public class CustomCategory
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "apps")]
    public List<string> Apps { get; set; } = new();

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// v2.30.0: user-defined custom routing rule with explicit Action
/// (direct / proxy / block). Replaces the v2.29.0
/// <see cref="CustomDirectRule"/> which was direct-only.
///
/// <para>Mapping to sing-box rule actions:</para>
/// <list type="bullet">
/// <item><c>direct</c> ⇒ <c>action="route"</c>, <c>outbound="direct"</c></item>
/// <item><c>proxy</c> ⇒ <c>action="route"</c>, <c>outbound="proxy"</c>
/// (or <c>"proxy-udp"</c> when network=udp + UDP-split servers exist)</item>
/// <item><c>block</c> ⇒ <c>action="reject"</c>, <c>method="default"</c>
/// (RST — fast-fail signal to apps). For domain-type matches we ALSO
/// insert a DNS-level reject so the lookup itself fails — saves a
/// round-trip and matches user expectation of "blocked = invisible".</item>
/// </list>
///
/// <para>For <c>geosite</c> / <c>geoip</c> match types, the rule_set
/// must be downloaded + registered. v2.30 ships with <c>ru</c> already
/// bundled (via <see cref="GeoDataDownloader"/>); other rule_set names
/// (<c>cn</c>, <c>us</c>, <c>ads</c>, etc.) auto-download on first use
/// from <c>raw.githubusercontent.com/SagerNet/sing-{geosite,geoip}/rule-set/</c>.</para>
/// </summary>
public class CustomRule
{
    // Phase 7 Wave 34 (2026-05-19): explicit [JsonPropertyName] so the
    // snake_case JSON wire format (NekoBox/Hiddify interop + previously-
    // exported user files) is preserved when serializing through
    // AppJsonContext's JsonTypeInfo<List<CustomRule>>. Pre-Wave-34 the
    // local `CustomRulesImportExport.JsonOptions` used
    // PropertyNamingPolicy=SnakeCaseLower; the JsonTypeInfo<T> overload
    // pins to the context's options instead, which has no naming policy.
    // [JsonPropertyName] on each property is the property-level
    // equivalent — works the same on import/export.

    /// <summary>"direct" | "proxy" | "block".</summary>
    [YamlMember(Alias = "action")]
    [JsonPropertyName("action")]
    public string Action { get; set; } = "direct";

    /// <summary>
    /// Match type. v2.30 supported types:
    /// <list type="bullet">
    /// <item><c>domain</c> — exact FQDN match</item>
    /// <item><c>domain_suffix</c> — destination FQDN ends with value</item>
    /// <item><c>domain_keyword</c> — substring anywhere</item>
    /// <item><c>ip_cidr</c> — IPv4/IPv6 CIDR</item>
    /// <item><c>port</c> — single dest port (1-65535)</item>
    /// <item><c>port_range</c> — "min-max" range</item>
    /// <item><c>network</c> — "tcp" or "udp"</item>
    /// <item><c>process_name</c> — case-sensitive process executable name</item>
    /// <item><c>geosite</c> — sing-geosite preset (ru/cn/us/ads/etc.)</item>
    /// <item><c>geoip</c> — sing-geoip preset (same naming)</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "type")]
    [JsonPropertyName("type")]
    public string Type { get; set; } = "domain_suffix";

    /// <summary>Comma-separated multi-value (single-value for geosite/geoip).</summary>
    [YamlMember(Alias = "value")]
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional human label for the UI rule list.</summary>
    [YamlMember(Alias = "comment")]
    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>True ⇒ rule active. Allows toggling without deleting.</summary>
    [YamlMember(Alias = "enabled")]
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// v2.29.0: user-defined direct-routing rule. Each entry matches a
/// destination (domain / IP / CIDR / port) and routes it OUT of the
/// VPN tunnel (action: direct). See <see cref="AppConfig.CustomDirectRules"/>
/// for context.
///
/// <para>v2.30.0: superseded by <see cref="CustomRule"/>. Kept for
/// back-compat with v2.29 configs; <see cref="SettingsMigrator"/>
/// migrates instances on first run.</para>
/// </summary>
public class CustomDirectRule
{
    /// <summary>
    /// Match type. One of:
    /// <list type="bullet">
    /// <item><c>domain</c> — exact match (full FQDN).</item>
    /// <item><c>domain_suffix</c> — match if destination ends with the value
    /// (e.g. <c>.lan.local</c> matches <c>printer.lan.local</c>).</item>
    /// <item><c>domain_keyword</c> — substring match anywhere in the FQDN.</item>
    /// <item><c>ip_cidr</c> — IP CIDR (e.g. <c>10.0.0.0/8</c>).</item>
    /// <item><c>port</c> — destination port (1-65535).</item>
    /// <item><c>process_name</c> — matches process name (case-sensitive on
    /// Windows; sing-box uses Go map lookup via filepath.Base).</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "domain_suffix";

    /// <summary>
    /// Match value(s). Comma-separated for multi-value. Examples:
    /// <list type="bullet">
    /// <item><c>"10.0.0.0/8, 192.168.0.0/16"</c> for ip_cidr.</item>
    /// <item><c>".lan.local, .corp.example"</c> for domain_suffix.</item>
    /// <item><c>"22, 80, 443"</c> for port.</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional human label, shown in the UI rule list.</summary>
    [YamlMember(Alias = "comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>True ⇒ rule active. Allows toggling without deleting.</summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>A saved custom sing-box config entry.</summary>
public class CustomConfigEntry
{
    /// <summary>Display name (derived from filename on import, e.g. "brat-pc").</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the ProgramData copy (e.g. %ProgramData%\VPNRouter\config\custom-brat-pc.json).</summary>
    [YamlMember(Alias = "path")]
    public string Path { get; set; } = string.Empty;
}

public class ProfileSource
{
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty; // github | local

    [YamlMember(Alias = "url")]
    public string? Url { get; set; }

    [YamlMember(Alias = "path")]
    public string? Path { get; set; }

    [YamlMember(Alias = "update_interval")]
    public int UpdateInterval { get; set; } = 3600;
}

public class VlessConfig
{
    // ── Legacy single-server fields (backward compatible) ──────────────────
    [YamlMember(Alias = "server")]
    public string Server { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 443;

    [YamlMember(Alias = "uuid")]
    public string Uuid { get; set; } = string.Empty;

    /// <summary>xtls-rprx-vision for Reality, empty for plain VLESS</summary>
    [YamlMember(Alias = "flow")]
    public string Flow { get; set; } = string.Empty;

    /// <summary>tls | reality</summary>
    [YamlMember(Alias = "security")]
    public string Security { get; set; } = "reality";

    [YamlMember(Alias = "reality")]
    public VlessRealityConfig Reality { get; set; } = new();

    /// <summary>Fallback plain TLS config (used when security = tls)</summary>
    [YamlMember(Alias = "tls")]
    public VlessTlsConfig Tls { get; set; } = new();

    /// <summary>tcp | ws | grpc — tcp is default for Reality+XTLS</summary>
    [YamlMember(Alias = "transport")]
    public VlessTransportConfig Transport { get; set; } = new();

    // ── Multi-server support ───────────────────────────────────────────────
    /// <summary>
    /// List of VLESS servers. When 2+ servers, urltest outbound is used for
    /// automatic failover. When empty, legacy single-server fields are used.
    /// </summary>
    [YamlMember(Alias = "servers")]
    public List<VlessServerEntry> Servers { get; set; } = new();

    /// <summary>
    /// Name of the actively selected server. Only this server (and its
    /// TCP/UDP pair with same IP) is used for routing. Other servers remain
    /// in the list but are NOT included in the generated config.
    /// When empty, the first server is used.
    /// </summary>
    [YamlMember(Alias = "active_server")]
    public string ActiveServer { get; set; } = string.Empty;

    /// <summary>
    /// Builds the effective server list. If 'servers' is populated, returns it.
    /// Otherwise creates a single entry from the legacy fields.
    /// </summary>
    /// <summary>
    /// Returns the full list of servers (for UI display).
    /// Use <see cref="GetActiveServers"/> for the servers to actually route through.
    /// </summary>
    public List<VlessServerEntry> GetEffectiveServers()
    {
        if (Servers != null && Servers.Count > 0)
            return Servers;

        // Backward compat: build from legacy scalar fields
        if (!string.IsNullOrEmpty(Server))
        {
            return new List<VlessServerEntry>
            {
                new()
                {
                    Server = Server,
                    Port = Port,
                    Uuid = Uuid,
                    Flow = Flow,
                    Security = Security ?? "reality",
                    Reality = Reality ?? new VlessRealityConfig(),
                    Tls = Tls ?? new VlessTlsConfig(),
                    Transport = Transport ?? new VlessTransportConfig()
                }
            };
        }

        return new List<VlessServerEntry>();
    }

    /// <summary>
    /// Returns ONLY the servers to route through — the active server and
    /// its TCP/UDP pair (same IP, different flow). This is what
    /// ConfigGenerator uses to build sing-box outbounds.
    /// </summary>
    public List<VlessServerEntry> GetActiveServers()
    {
        var all = GetEffectiveServers();
        if (all.Count <= 1) return all;

        // Find active by name
        VlessServerEntry? active = null;
        if (!string.IsNullOrEmpty(ActiveServer))
            active = all.FirstOrDefault(s =>
                s.Name?.Equals(ActiveServer, StringComparison.OrdinalIgnoreCase) == true);

        // Fallback: first server
        active ??= all[0];

        // Include all servers with the same IP (TCP + UDP pair)
        var activeIp = active.Server;
        return all.Where(s => s.Server == activeIp).ToList();
    }
}

/// <summary>
/// A single proxy server entry with all connection parameters.
///
/// <para>Despite the legacy name <c>VlessServerEntry</c>, this type now
/// holds entries for any supported protocol (VLESS+Reality, Hysteria2,
/// TUIC v5, Shadowsocks 2022, optionally with ShadowTLS v3 plugin or
/// Hysteria2 Salamander obfuscation). The <see cref="Protocol"/>
/// discriminator controls which subset of fields is meaningful at
/// outbound-generation time. The class name is preserved to avoid
/// breaking the existing YAML alias mapping.</para>
/// </summary>
public class VlessServerEntry
{
    /// <summary>Optional display name for logs (e.g. "main", "backup")</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Protocol discriminator. One of:
    /// <list type="bullet">
    /// <item><c>vless</c> (default) — VLESS+Reality, optionally over TCP/WS/gRPC</item>
    /// <item><c>hysteria2</c> — Hysteria2 (with optional Salamander obfs)</item>
    /// <item><c>tuic</c> — TUIC v5</item>
    /// <item><c>shadowsocks</c> — Shadowsocks 2022 (with optional ShadowTLS v3 plugin)</item>
    /// <item><c>naive</c> — NaiveProxy (HTTP/2 or HTTP/3 via Cronet; Windows + Linux only)</item>
    /// </list>
    /// New entries default to "vless" so legacy settings.yaml without
    /// this field stays valid.
    /// </summary>
    [YamlMember(Alias = "protocol")]
    public string Protocol { get; set; } = "vless";

    [YamlMember(Alias = "server")]
    public string Server { get; set; } = string.Empty;

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 443;

    [YamlMember(Alias = "uuid")]
    public string Uuid { get; set; } = string.Empty;

    [YamlMember(Alias = "flow")]
    public string Flow { get; set; } = string.Empty;

    [YamlMember(Alias = "security")]
    public string Security { get; set; } = "reality";

    [YamlMember(Alias = "reality")]
    public VlessRealityConfig Reality { get; set; } = new();

    [YamlMember(Alias = "tls")]
    public VlessTlsConfig Tls { get; set; } = new();

    [YamlMember(Alias = "transport")]
    public VlessTransportConfig Transport { get; set; } = new();

    // ── Non-VLESS fields ────────────────────────────────────────────────────
    // All optional. Populated only when Protocol != "vless".

    /// <summary>
    /// NaiveProxy basic-auth username (paired with <see cref="Password"/>).
    /// Empty for non-naive protocols.
    /// </summary>
    [YamlMember(Alias = "username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Authentication password.
    /// <list type="bullet">
    /// <item>Hysteria2 — auth password (URL userinfo)</item>
    /// <item>TUIC — auth password (URL userinfo, paired with Uuid)</item>
    /// <item>Shadowsocks — encryption key / password (paired with Method)</item>
    /// <item>NaiveProxy — auth password (paired with Username)</item>
    /// </list>
    /// </summary>
    [YamlMember(Alias = "password")]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks cipher method (e.g. <c>2022-blake3-aes-256-gcm</c>,
    /// <c>aes-256-gcm</c>, <c>chacha20-ietf-poly1305</c>). Ignored for
    /// non-Shadowsocks protocols.
    /// </summary>
    [YamlMember(Alias = "method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// TUIC congestion-control algorithm. <c>bbr</c> | <c>cubic</c> |
    /// <c>new_reno</c>. Default <c>bbr</c>.
    /// </summary>
    [YamlMember(Alias = "congestion_control")]
    public string CongestionControl { get; set; } = "bbr";

    /// <summary>
    /// TUIC UDP relay mode. <c>native</c> | <c>quic</c>.
    /// </summary>
    [YamlMember(Alias = "udp_relay_mode")]
    public string UdpRelayMode { get; set; } = "native";

    /// <summary>
    /// Hysteria2 Salamander obfuscation. Empty = obfs disabled.
    /// </summary>
    [YamlMember(Alias = "obfs_type")]
    public string ObfsType { get; set; } = string.Empty;

    /// <summary>Salamander password (paired with <see cref="ObfsType"/> = "salamander").</summary>
    [YamlMember(Alias = "obfs_password")]
    public string ObfsPassword { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks plugin name (e.g. <c>shadow-tls</c> for ShadowTLS v3).
    /// Empty = no plugin.
    /// </summary>
    [YamlMember(Alias = "plugin")]
    public string Plugin { get; set; } = string.Empty;

    /// <summary>
    /// Shadowsocks plugin opts (semicolon-delimited <c>key=value</c> pairs:
    /// <c>version=3;password=xxx;host=cdn.example.com</c>).
    /// </summary>
    [YamlMember(Alias = "plugin_opts")]
    public string PluginOpts { get; set; } = string.Empty;

    /// <summary>
    /// r5: co-located server pairing tag from the subscription (<c>pair=</c>
    /// query param). NaiveProxy can't carry UDP, so a naive server and its
    /// same-node UDP-capable sibling (e.g. Hysteria2) share an identical
    /// PairGroup; VPNRouter routes UDP through the sibling (same physical node →
    /// same exit IP). Empty = no pairing.
    /// </summary>
    [YamlMember(Alias = "pair")]
    public string PairGroup { get; set; } = string.Empty;

    /// <summary>
    /// r7 #1: NaiveProxy HTTP/3-over-QUIC transport. Set from a
    /// <c>naive+quic://</c> share-link; emitted as the naive outbound's
    /// <c>quic</c> boolean. False (default) = HTTP/2. Naive-only.
    /// </summary>
    [YamlMember(Alias = "naive_quic")]
    public bool NaiveQuic { get; set; }
}

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
    /// <summary>tcp | ws | grpc</summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "tcp";

    [YamlMember(Alias = "path")]
    public string Path { get; set; } = "/";

    [YamlMember(Alias = "headers")]
    public Dictionary<string, string> Headers { get; set; } = new();
}

public class TunSettings
{
    [YamlMember(Alias = "interface_name")]
    public string InterfaceName { get; set; } = "VPNRouter-TUN";

    [YamlMember(Alias = "ipv4_address")]
    public string Ipv4Address { get; set; } = "172.19.0.1/30";

    [YamlMember(Alias = "ipv6_enabled")]
    public bool Ipv6Enabled { get; set; } = false;

    [YamlMember(Alias = "mtu")]
    public int Mtu { get; set; } = 9000;

    [YamlMember(Alias = "auto_route")]
    public bool AutoRoute { get; set; } = true;

    [YamlMember(Alias = "strict_route")]
    public bool StrictRoute { get; set; } = false;

    /// <summary>
    /// Exclude specific address ranges from TUN routing.
    /// Traffic to these addresses bypasses TUN and uses the system routing table.
    /// Useful for coexistence with other VPNs (e.g. WireGuard/AmneziaWG subnets).
    /// Example: ["10.9.1.0/24", "192.168.100.0/24"]
    /// </summary>
    [YamlMember(Alias = "route_exclude_address")]
    public List<string> RouteExcludeAddress { get; set; } = new();
}

public class DnsSettings
{
    [YamlMember(Alias = "strategy")]
    public string Strategy { get; set; } = "ipv4_only";

    [YamlMember(Alias = "vpn_dns")]
    public string VpnDns { get; set; } = "https://1.1.1.1/dns-query";

    [YamlMember(Alias = "local_dns")]
    public string LocalDns { get; set; } = "local";
}

public class SingBoxSettings
{
    [YamlMember(Alias = "executable_path")]
    public string ExecutablePath { get; set; } = @"%ProgramData%\VPNRouter\bin\sing-box.exe";

    [YamlMember(Alias = "auto_download")]
    public bool AutoDownload { get; set; } = true;

    [YamlMember(Alias = "download_url")]
    public string DownloadUrl { get; set; } = "https://github.com/SagerNet/sing-box/releases/latest/download/sing-box-windows-amd64.zip";

    /// <summary>
    /// Clash API address (host:port). Used for hot-reload without process restart.
    /// Must match the value in experimental.clash_api.external_controller in the generated config.
    /// </summary>
    [YamlMember(Alias = "clash_api")]
    public string ClashApi { get; set; } = "127.0.0.1:9090";
}

public class MonitoringSettings
{
    [YamlMember(Alias = "health_check_interval")]
    public int HealthCheckInterval { get; set; } = 30;

    [YamlMember(Alias = "restart_on_failure")]
    public bool RestartOnFailure { get; set; } = true;

    [YamlMember(Alias = "max_restart_attempts")]
    public int MaxRestartAttempts { get; set; } = 5;

    [YamlMember(Alias = "process_scan_interval")]
    public int ProcessScanInterval { get; set; } = 60;
}

public class UpdateSettings
{
    /// <summary>GitHub repo in "owner/repo" format for release checks.</summary>
    [YamlMember(Alias = "github_repo")]
    public string GitHubRepo { get; set; } = "PavelLizunov/VPNRouter";

    /// <summary>Check for updates on GUI startup.</summary>
    [YamlMember(Alias = "auto_check")]
    public bool AutoCheck { get; set; } = true;

    /// <summary>Update channel: "stable" or "experimental".
    /// Stable skips pre-releases, experimental includes all.</summary>
    [YamlMember(Alias = "channel")]
    public string Channel { get; set; } = "stable";

    [YamlIgnore]
    public bool IsExperimental =>
        Channel.Equals("experimental", StringComparison.OrdinalIgnoreCase);
}


/// <summary>
/// v2.14.4 — user-provided source URL for Free Configs aggregation.
/// Private subscriptions that user wants to include alongside the 14 public sources.
/// </summary>
public class UserFreeSource
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "url")]
    public string Url { get; set; } = string.Empty;

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "added_at")]
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
