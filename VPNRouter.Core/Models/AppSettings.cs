using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class AppSettings
{
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

    [YamlMember(Alias = "update")]
    public UpdateSettings Update { get; set; } = new();
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

    /// <summary>UI theme: "light" or "dark".</summary>
    [YamlMember(Alias = "theme")]
    public string Theme { get; set; } = "light";

    /// <summary>UI language: "en" or "ru".</summary>
    [YamlMember(Alias = "language")]
    public string Language { get; set; } = "en";

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
    /// When true, traffic to Russian sites/IPs is routed directly (real IP),
    /// not through VPN. Protects VPN server from being blacklisted by RU services
    /// and unblocks RU sites that geo-restrict non-RU IPs.
    /// </summary>
    [YamlMember(Alias = "bypass_russian_traffic")]
    public bool BypassRussianTraffic { get; set; } = true;

    /// <summary>
    /// When true, force IPv4-only DNS resolution to prevent IPv6 leaks
    /// when VPN tunnels only IPv4. Recommended unless you specifically need IPv6.
    /// </summary>
    [YamlMember(Alias = "force_ipv4_only")]
    public bool ForceIpv4Only { get; set; } = true;

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
/// A single VLESS server entry with all connection parameters.
/// Each server can have its own UUID, Reality keys, etc.
/// </summary>
public class VlessServerEntry
{
    /// <summary>Optional display name for logs (e.g. "main", "backup")</summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

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
