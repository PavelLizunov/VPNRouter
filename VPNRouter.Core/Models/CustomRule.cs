using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Models;

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
