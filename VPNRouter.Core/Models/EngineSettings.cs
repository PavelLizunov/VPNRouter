using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

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

    /// <summary>
    /// P1 (OPEN-DEFECTS, 2026-07-10): bearer secret for the Clash API. Without
    /// it ANY local process (or a hostile web page XHR-ing 127.0.0.1:9090 —
    /// and on Android any installed app) can read live connection metadata and
    /// issue control calls (proxy switch, config reload). Auto-generated on
    /// first load (<c>AppSettingsSane.EnsureSane</c>), persisted so the App and
    /// the Windows Service — separate processes sharing this YAML — agree.
    /// Rides the generated config as <c>experimental.clash_api.secret</c>;
    /// every in-app consumer sends <c>Authorization: Bearer</c> / WS <c>?token=</c>.
    /// </summary>
    [YamlMember(Alias = "clash_api_secret")]
    public string ClashApiSecret { get; set; } = "";
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
