using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

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
