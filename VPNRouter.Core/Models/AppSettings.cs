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
    /// Builds the effective server list. If 'servers' is populated, returns it.
    /// Otherwise creates a single entry from the legacy fields.
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
