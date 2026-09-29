using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class AppSettings
{
    public const int CurrentSchemaVersion = 8;

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

    [YamlMember(Alias = "custom_apps")]
    public List<string> CustomApps { get; set; } = new();

    [YamlMember(Alias = "custom_group_apps")]
    public Dictionary<string, List<string>> CustomGroupApps { get; set; } = new();

    [YamlMember(Alias = "custom_categories")]
    public List<CustomCategory> CustomCategories { get; set; } = new();

    [YamlMember(Alias = "excluded_apps")]
    public List<string> ExcludedApps { get; set; } = new();

    [YamlMember(Alias = "update")]
    public UpdateSettings Update { get; set; } = new();

}
