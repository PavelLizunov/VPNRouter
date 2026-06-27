using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

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
    /// G6 (2026-06-27) — resolve private / LAN domain suffixes (.local, .lan,
    /// home.arpa, .internal + <see cref="LanDnsSuffixes"/>) via the SYSTEM
    /// resolver instead of the remote DoH, so split-tunnel DIRECT apps can still
    /// reach LAN devices (nas.local, printer.lan). Public domains stay on the
    /// encrypted DoH path (no ISP leak). Automatically suppressed when
    /// <see cref="StrictDns"/> is on (the user explicitly wants ALL DNS via the
    /// VPN, accepting LAN-name breakage — see StrictDns tradeoffs above).
    /// </summary>
    [YamlMember(Alias = "resolve_lan_via_system_dns")]
    public bool ResolveLanViaSystemDns { get; set; } = true;

    /// <summary>
    /// Extra private/LAN domain suffixes (beyond the built-in .local / .lan /
    /// home.arpa / .internal) to resolve via the system resolver when
    /// <see cref="ResolveLanViaSystemDns"/> is on. e.g. "corp", "home". Stored
    /// without a leading dot; matching is suffix-based on label boundaries.
    /// </summary>
    [YamlMember(Alias = "lan_dns_suffixes")]
    public List<string> LanDnsSuffixes { get; set; } = new();

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
