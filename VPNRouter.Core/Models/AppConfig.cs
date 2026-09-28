using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

public class AppConfig
{
    [YamlMember(Alias = "log_level")]
    public string LogLevel { get; set; } = "info";

    [YamlMember(Alias = "log_file")]
    public string LogFile { get; set; } = @"%ProgramData%\VPNRouter\logs\vpnrouter.log";

    [YamlMember(Alias = "routing_mode")]
    public string RoutingMode { get; set; } = "split";

    [YamlMember(Alias = "connection_intent")]
    public string ConnectionIntent { get; set; } = VPNRouter.Core.Models.ConnectionIntent.General;

    [YamlMember(Alias = "routing_apps_mode")]
    public string RoutingAppsMode { get; set; } = "include";

    [YamlMember(Alias = "routing_apps_include")]
    public List<string> RoutingAppsInclude { get; set; } = new();

    [YamlMember(Alias = "routing_apps_include_initialized")]
    public bool RoutingAppsIncludeInitialized { get; set; }

    [YamlMember(Alias = "routing_apps_exclude")]
    public List<string> RoutingAppsExclude { get; set; } = new();

    [YamlMember(Alias = "theme")]
    public string Theme { get; set; } = "system";

    [YamlMember(Alias = "language")]
    public string Language { get; set; } = string.Empty;

    [YamlMember(Alias = "ui_mode")]
    public string UiMode { get; set; } = "advanced";

    [YamlMember(Alias = "config_mode")]
    public string ConfigMode { get; set; } = "generated";

    [YamlMember(Alias = "custom_config")]
    public string CustomConfig { get; set; } = string.Empty;

    [YamlMember(Alias = "custom_configs")]
    public List<CustomConfigEntry> CustomConfigs { get; set; } = new();

    [YamlMember(Alias = "active_custom_config")]
    public string ActiveCustomConfig { get; set; } = string.Empty;

    [YamlMember(Alias = "subscription_url")]
    public string SubscriptionUrl { get; set; } = string.Empty;

    [YamlMember(Alias = "subscription_servers")]
    public List<VlessServerEntry> SubscriptionServers { get; set; } = new();

    [YamlMember(Alias = "active_subscription_server")]
    public string ActiveSubscriptionServer { get; set; } = string.Empty;

    [YamlMember(Alias = "subscriptions")]
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    [YamlMember(Alias = "bypass_russian_traffic")]
    public bool BypassRussianTraffic { get; set; } = true;

    [YamlMember(Alias = "custom_direct_rules")]
    public List<CustomDirectRule> CustomDirectRules { get; set; } = new();

    [YamlMember(Alias = "custom_rules")]
    public List<CustomRule> CustomRules { get; set; } = new();

    [YamlMember(Alias = "custom_rules_priority")]
    public string CustomRulesPriority { get; set; } = "toggles_first";

    [YamlMember(Alias = "force_ipv4_only")]
    public bool ForceIpv4Only { get; set; } = true;

    [YamlMember(Alias = "block_quic_on_tcp_proxy")]
    public bool BlockQuicOnTcpProxy { get; set; } = true;

    [YamlMember(Alias = "strict_mode")]
    public bool StrictMode { get; set; } = false;

    [YamlMember(Alias = "free_config_security_warning_acked")]
    public bool FreeConfigSecurityWarningAcked { get; set; } = false;

    [YamlMember(Alias = "user_free_sources")]
    public List<UserFreeSource> UserFreeSources { get; set; } = new();

    [YamlMember(Alias = "strict_dns")]
    public bool StrictDns { get; set; } = false;

    [YamlMember(Alias = "resolve_lan_via_system_dns")]
    public bool ResolveLanViaSystemDns { get; set; } = true;

    [YamlMember(Alias = "lan_dns_suffixes")]
    public List<string> LanDnsSuffixes { get; set; } = new();

    [YamlMember(Alias = "block_ads")]
    public bool BlockAds { get; set; } = false;

    [YamlMember(Alias = "zapret_enabled")]
    public bool ZapretEnabled { get; set; } = false;

    [YamlMember(Alias = "zapret_strategy")]
    public string ZapretStrategy { get; set; } = "multisplit";

    [YamlMember(Alias = "zapret_custom_args")]
    public string ZapretCustomArgs { get; set; } = string.Empty;

    [YamlMember(Alias = "tg_proxy_enabled")]
    public bool TgProxyEnabled { get; set; } = false;

    [YamlMember(Alias = "tg_proxy_port")]
    public int TgProxyPort { get; set; } = 1443;

    [YamlMember(Alias = "tg_proxy_secret")]
    public string TgProxySecret { get; set; } = string.Empty;

    [YamlMember(Alias = "autostart_vpn")]
    public bool AutostartVpn { get; set; } = false;

    [YamlMember(Alias = "autostart_zapret")]
    public bool AutostartZapret { get; set; } = false;

    [YamlMember(Alias = "autostart_tgproxy")]
    public bool AutostartTgProxy { get; set; } = false;

    [YamlMember(Alias = "autostart_ui")]
    public bool AutostartUi { get; set; } = false;

    [YamlMember(Alias = "flush_dns_on_start")]
    public bool FlushDnsOnStart { get; set; } = true;

    [YamlMember(Alias = "dns_leak_lockdown")]
    [JsonPropertyName("dns_leak_lockdown")]
    public bool DnsLeakLockdown { get; set; } = false;

    [YamlMember(Alias = "placeholder_prune_count")]
    public int PlaceholderPruneCount { get; set; }

    [YamlMember(Alias = "placeholder_prune_at_utc")]
    public string PlaceholderPruneAtUtc_Str { get; set; } = string.Empty;
}
