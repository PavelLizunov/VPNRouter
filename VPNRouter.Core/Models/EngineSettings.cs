using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class DnsSettings
{
    [YamlMember(Alias = "strategy")]
    public string Strategy { get; set; } = "ipv4_only";

    [YamlMember(Alias = "vpn_dns")]
    public string VpnDns { get; set; } = "https://8.8.8.8/dns-query";

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

    [YamlMember(Alias = "clash_api")]
    public string ClashApi { get; set; } = "127.0.0.1:9090";

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
    [YamlMember(Alias = "github_repo")]
    public string GitHubRepo { get; set; } = "PavelLizunov/VPNRouter";

    [YamlMember(Alias = "auto_check")]
    public bool AutoCheck { get; set; } = true;

    [YamlMember(Alias = "channel")]
    public string Channel { get; set; } = "stable";

    [YamlIgnore]
    public bool IsExperimental =>
        Channel.Equals("experimental", StringComparison.OrdinalIgnoreCase);
}
