using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.32.0 (Android-led, 2026-05-07) — single-file export/import of a
/// VPNRouter user's full configuration.
///
/// <para>The document is a JSON blob with a fixed schema marker
/// (<c>"vpnrouter-config-share"</c>) and an integer <see cref="Version"/>
/// that bumps when the schema changes incompatibly. Always-included
/// payload — subscriptions list, manual VLESS URI, custom sing-box JSON
/// (whichever is in use). Opt-in payload — settings (theme/lang/routing/
/// dns/etc) and the per-app filter (mode + packages). Opt-in is OFF by
/// default because both sections are device-specific: theme ≠ portable
/// across two users; the per-app filter package list is bound to apps
/// installed on THIS device.</para>
///
/// <para>The schema is deliberately platform-neutral so a desktop
/// follow-up can adopt the same Document shape — the
/// <see cref="ExportedFromInfo.Platform"/> field reflects the source so
/// the importer can ignore platform-specific extras (e.g. an iOS PerApp
/// list won't make sense on Windows, but the validator soft-skips it
/// rather than rejecting the whole import).</para>
///
/// <para>Mirror desktop pattern reference:
/// <c>VPNRouter.Core/Services/CustomRulesImportExport.cs</c> (rules-only
/// pre-2.32). When desktop adopts whole-config share, this class is the
/// single source of truth — no duplicate Document type.</para>
/// </summary>
public sealed class ConfigShareDocument
{
    /// <summary>Stable schema marker — anything else is REJECTED on import.</summary>
    public const string SchemaMarker = "vpnrouter-config-share";

    /// <summary>Bump when an incompatible field shape is introduced.</summary>
    public const int CurrentVersion = 1;

    [JsonProperty("schema")]
    public string Schema { get; set; } = SchemaMarker;

    [JsonProperty("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonProperty("exported_at")]
    public DateTimeOffset ExportedAt { get; set; }

    [JsonProperty("exported_from")]
    public ExportedFromInfo ExportedFrom { get; set; } = new();

    /// <summary>"subscribe" | "manual" | "custom" — mirrors AppSettings.App.ConfigMode.</summary>
    [JsonProperty("config_mode")]
    public string ConfigMode { get; set; } = "subscribe";

    /// <summary>Always present — empty list if no subscriptions were configured.</summary>
    [JsonProperty("subscriptions")]
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    /// <summary>Single-URI mode payload. Null when ConfigMode != "manual".</summary>
    [JsonProperty("manual_vless_uri", NullValueHandling = NullValueHandling.Ignore)]
    public string? ManualVlessUri { get; set; }

    /// <summary>User-pasted full sing-box JSON. Null when ConfigMode != "custom".</summary>
    [JsonProperty("custom_config", NullValueHandling = NullValueHandling.Ignore)]
    public CustomConfigPayload? CustomConfig { get; set; }

    /// <summary>Opt-in. Null when user did NOT check "include settings" at export.</summary>
    [JsonProperty("settings", NullValueHandling = NullValueHandling.Ignore)]
    public ExportedSettings? Settings { get; set; }

    /// <summary>Opt-in. Null when user did NOT check "include per-app filter".</summary>
    [JsonProperty("per_app_filter", NullValueHandling = NullValueHandling.Ignore)]
    public PerAppFilterExport? PerAppFilter { get; set; }

    /// <summary>Serialise to indented JSON (human-friendly diff/inspect).</summary>
    public static string Serialize(ConfigShareDocument doc)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        return JsonConvert.SerializeObject(doc, Formatting.Indented);
    }

    /// <summary>
    /// Validate-and-parse. Returns a result carrying either the Document
    /// or a single-line human-readable error reason. The error message
    /// is suitable to surface verbatim in a toast — no stack traces.
    /// </summary>
    public static ConfigShareDocumentParseResult TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ConfigShareDocumentParseResult.Failure("empty content");

        JObject jobj;
        try
        {
            jobj = JObject.Parse(json);
        }
        catch (JsonReaderException ex)
        {
            return ConfigShareDocumentParseResult.Failure(
                $"malformed JSON: {ex.Message}");
        }

        var schemaToken = jobj["schema"]?.Value<string>();
        if (!string.Equals(schemaToken, SchemaMarker, StringComparison.Ordinal))
        {
            return ConfigShareDocumentParseResult.Failure(
                $"unsupported document — schema marker '{schemaToken ?? "<missing>"}' (expected '{SchemaMarker}')");
        }

        var versionToken = jobj["version"]?.Value<int?>();
        if (versionToken is null)
        {
            return ConfigShareDocumentParseResult.Failure("missing 'version' field");
        }

        if (versionToken.Value > CurrentVersion)
        {
            return ConfigShareDocumentParseResult.Failure(
                $"document version {versionToken.Value} is newer than supported version {CurrentVersion} — please update VPNRouter");
        }

        ConfigShareDocument? doc;
        try
        {
            doc = jobj.ToObject<ConfigShareDocument>();
        }
        catch (Exception ex)
        {
            return ConfigShareDocumentParseResult.Failure(
                $"document deserialise failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (doc is null)
            return ConfigShareDocumentParseResult.Failure("deserialised to null");

        // Defensive: tolerate missing required containers from a future
        // schema-1 producer that elided empty arrays. Newtonsoft already
        // gives us Subscriptions=[] from default ctor, but explicit null
        // would replace it.
        doc.Subscriptions ??= new List<SubscriptionEntry>();
        doc.ExportedFrom ??= new ExportedFromInfo();

        // Validate ConfigMode value — refuse instead of letting a typo
        // slip past and confuse the routing engine downstream.
        var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subscribe", "manual", "custom"
        };
        if (!allowedModes.Contains(doc.ConfigMode ?? string.Empty))
        {
            return ConfigShareDocumentParseResult.Failure(
                $"unknown config_mode '{doc.ConfigMode}' (expected: subscribe / manual / custom)");
        }

        // Per-mode invariants — soft-warn rather than hard-fail because
        // a malformed export from a future / different platform might
        // still carry useful subset data.
        if (string.Equals(doc.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase) &&
            doc.CustomConfig is null)
        {
            return ConfigShareDocumentParseResult.Failure(
                "config_mode='custom' but 'custom_config' payload is missing");
        }

        return ConfigShareDocumentParseResult.Success(doc);
    }

    /// <summary>
    /// Build a one-line preview suitable for a confirmation dialog —
    /// "Подписки: 2, Серверы: 17, Custom JSON: нет, Настройки: вкл, Per-app: 4 пакета".
    /// Never throws; returns "(invalid)" for null input. Bilingual via the
    /// supplied <paramref name="ru"/> flag (false → English).
    /// </summary>
    public string BuildPreview(bool ru)
    {
        var parts = new List<string>();
        var subCount = Subscriptions?.Count ?? 0;
        var srvCount = 0;
        if (Subscriptions != null)
        {
            foreach (var s in Subscriptions)
            {
                if (s?.Servers != null) srvCount += s.Servers.Count;
            }
        }

        parts.Add(ru ? $"Подписки: {subCount}" : $"Subscriptions: {subCount}");
        parts.Add(ru ? $"Серверы: {srvCount}" : $"Servers: {srvCount}");

        if (!string.IsNullOrWhiteSpace(ManualVlessUri))
        {
            parts.Add(ru ? "Ручной URI: да" : "Manual URI: yes");
        }

        if (CustomConfig != null && !string.IsNullOrWhiteSpace(CustomConfig.SingBoxJson))
        {
            parts.Add(ru ? "Custom JSON: да" : "Custom JSON: yes");
        }

        if (Settings != null)
        {
            parts.Add(ru ? "Настройки: вкл" : "Settings: included");
        }

        if (PerAppFilter != null)
        {
            var n = PerAppFilter.Packages?.Count ?? 0;
            parts.Add(ru ? $"Per-app: {n}" : $"Per-app: {n} apps");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Suggested filename for the system file picker save UI. Mirrors
    /// <c>vpnrouter-config-YYYYMMDD-HHmm.json</c> — sortable + extension
    /// matches MIME type application/json.
    /// </summary>
    public static string SuggestFilename(DateTimeOffset? when = null)
    {
        var ts = (when ?? DateTimeOffset.UtcNow).ToLocalTime();
        return $"vpnrouter-config-{ts:yyyyMMdd-HHmm}.json";
    }
}

/// <summary>"Where did this export come from" provenance block.</summary>
public sealed class ExportedFromInfo
{
    [JsonProperty("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonProperty("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonProperty("device_label")]
    public string DeviceLabel { get; set; } = string.Empty;
}

/// <summary>Custom sing-box JSON paste payload (used when ConfigMode=="custom").</summary>
public sealed class CustomConfigPayload
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("sing_box_json")]
    public string SingBoxJson { get; set; } = string.Empty;
}

/// <summary>Opt-in settings block. Field names mirror the Android SharedPreferences keys (and desktop AppSettings.App).</summary>
public sealed class ExportedSettings
{
    [JsonProperty("theme", NullValueHandling = NullValueHandling.Ignore)]
    public string? Theme { get; set; }

    [JsonProperty("language", NullValueHandling = NullValueHandling.Ignore)]
    public string? Language { get; set; }

    [JsonProperty("routing_mode", NullValueHandling = NullValueHandling.Ignore)]
    public string? RoutingMode { get; set; }

    [JsonProperty("bypass_ru", NullValueHandling = NullValueHandling.Ignore)]
    public bool? BypassRussianTraffic { get; set; }

    [JsonProperty("block_on_vpn_fail", NullValueHandling = NullValueHandling.Ignore)]
    public bool? BlockOnVpnFail { get; set; }

    [JsonProperty("dns_strategy", NullValueHandling = NullValueHandling.Ignore)]
    public string? DnsStrategy { get; set; }

    [JsonProperty("update_channel", NullValueHandling = NullValueHandling.Ignore)]
    public string? UpdateChannel { get; set; }

    [JsonProperty("autostart_vpn", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AutostartVpn { get; set; }

    [JsonProperty("autostart_zapret", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AutostartZapret { get; set; }

    [JsonProperty("autostart_tgproxy", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AutostartTgProxy { get; set; }
}

/// <summary>Opt-in per-app filter block. Mirrors VpnService.Builder allow/disallow lists.</summary>
public sealed class PerAppFilterExport
{
    /// <summary>"off" | "include" | "exclude" — see PerAppFilterMode.</summary>
    [JsonProperty("mode")]
    public string Mode { get; set; } = "off";

    [JsonProperty("packages")]
    public List<string> Packages { get; set; } = new();
}

/// <summary>Result of <see cref="ConfigShareDocument.TryParse"/>.</summary>
public sealed class ConfigShareDocumentParseResult
{
    public bool Ok { get; }
    public ConfigShareDocument? Document { get; }
    public string? Error { get; }

    private ConfigShareDocumentParseResult(bool ok, ConfigShareDocument? doc, string? err)
    {
        Ok = ok;
        Document = doc;
        Error = err;
    }

    public static ConfigShareDocumentParseResult Success(ConfigShareDocument doc) =>
        new(true, doc, null);

    public static ConfigShareDocumentParseResult Failure(string error) =>
        new(false, null, error);
}
