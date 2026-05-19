#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;
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
///
/// <para>Phase 4 (2026-05-18) — migrated from Newtonsoft.Json
/// <c>[JsonProperty]</c> + <c>JObject.Parse</c> + <c>ToObject&lt;T&gt;</c> to
/// System.Text.Json <c>[JsonPropertyName]</c> + <c>JsonDocument</c> +
/// <c>JsonSerializer.Deserialize</c>. Wire format is preserved byte-for-byte
/// because every property carries an explicit <c>[JsonPropertyName]</c>
/// pinning the snake_case wire key the pre-migration Newtonsoft
/// <c>[JsonProperty]</c> annotations produced.</para>
/// </summary>
public sealed class ConfigShareDocument
{
    /// <summary>Stable schema marker — anything else is REJECTED on import.</summary>
    public const string SchemaMarker = "vpnrouter-config-share";

    /// <summary>Bump when an incompatible field shape is introduced.</summary>
    public const int CurrentVersion = 1;

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaMarker;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("exported_at")]
    public DateTimeOffset ExportedAt { get; set; }

    [JsonPropertyName("exported_from")]
    public ExportedFromInfo ExportedFrom { get; set; } = new();

    /// <summary>"subscribe" | "manual" | "custom" — mirrors AppSettings.App.ConfigMode.</summary>
    [JsonPropertyName("config_mode")]
    public string ConfigMode { get; set; } = "subscribe";

    /// <summary>Always present — empty list if no subscriptions were configured.</summary>
    [JsonPropertyName("subscriptions")]
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    /// <summary>Single-URI mode payload. Null when ConfigMode != "manual".</summary>
    [JsonPropertyName("manual_vless_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManualVlessUri { get; set; }

    /// <summary>User-pasted full sing-box JSON. Null when ConfigMode != "custom".</summary>
    [JsonPropertyName("custom_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CustomConfigPayload? CustomConfig { get; set; }

    /// <summary>Opt-in. Null when user did NOT check "include settings" at export.</summary>
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExportedSettings? Settings { get; set; }

    /// <summary>Opt-in. Null when user did NOT check "include per-app filter".</summary>
    [JsonPropertyName("per_app_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerAppFilterExport? PerAppFilter { get; set; }

    /// <summary>Serialise to indented JSON (human-friendly diff/inspect).</summary>
    public static string Serialize(ConfigShareDocument doc)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        return JsonSerializer.Serialize(doc, Json.AppJsonContext.Default.ConfigShareDocument);
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

        // Phase 4 (2026-05-18) — STJ JsonDocument for the schema-marker +
        // version probe (cheap inspect-then-deserialize-fully pattern,
        // mirrors the prior JObject.Parse → ToObject<T> flow). Defensive
        // catches mirror Newtonsoft's JsonReaderException + generic
        // Exception surfaces.
        JsonElement root;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            doc?.Dispose();
            return ConfigShareDocumentParseResult.Failure(
                $"malformed JSON: {ex.Message}");
        }
        finally
        {
            doc?.Dispose();
        }

        if (root.ValueKind != JsonValueKind.Object)
            return ConfigShareDocumentParseResult.Failure(
                $"document root is {root.ValueKind}, expected an Object");

        string? schemaToken = null;
        if (root.TryGetProperty("schema", out var schemaProp) && schemaProp.ValueKind == JsonValueKind.String)
            schemaToken = schemaProp.GetString();

        if (!string.Equals(schemaToken, SchemaMarker, StringComparison.Ordinal))
        {
            return ConfigShareDocumentParseResult.Failure(
                $"unsupported document — schema marker '{schemaToken ?? "<missing>"}' (expected '{SchemaMarker}')");
        }

        int? versionToken = null;
        if (root.TryGetProperty("version", out var versionProp) && versionProp.ValueKind == JsonValueKind.Number)
            versionToken = versionProp.GetInt32();

        if (versionToken is null)
        {
            return ConfigShareDocumentParseResult.Failure("missing 'version' field");
        }

        if (versionToken.Value > CurrentVersion)
        {
            return ConfigShareDocumentParseResult.Failure(
                $"document version {versionToken.Value} is newer than supported version {CurrentVersion} — please update VPNRouter");
        }

        ConfigShareDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, Json.AppJsonContext.Default.ConfigShareDocument);
        }
        catch (Exception ex)
        {
            return ConfigShareDocumentParseResult.Failure(
                $"document deserialise failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (document is null)
            return ConfigShareDocumentParseResult.Failure("deserialised to null");

        // Defensive: tolerate missing required containers from a future
        // schema-1 producer that elided empty arrays. Default ctor
        // populates Subscriptions=[]; the explicit null check ensures
        // a future JSON "subscriptions": null doesn't crash us.
        document.Subscriptions ??= new List<SubscriptionEntry>();
        document.ExportedFrom ??= new ExportedFromInfo();

        // Validate ConfigMode value — refuse instead of letting a typo
        // slip past and confuse the routing engine downstream.
        var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subscribe", "manual", "custom"
        };
        if (!allowedModes.Contains(document.ConfigMode ?? string.Empty))
        {
            return ConfigShareDocumentParseResult.Failure(
                $"unknown config_mode '{document.ConfigMode}' (expected: subscribe / manual / custom)");
        }

        // Per-mode invariants — soft-warn rather than hard-fail because
        // a malformed export from a future / different platform might
        // still carry useful subset data.
        if (string.Equals(document.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase) &&
            document.CustomConfig is null)
        {
            return ConfigShareDocumentParseResult.Failure(
                "config_mode='custom' but 'custom_config' payload is missing");
        }

        return ConfigShareDocumentParseResult.Success(document);
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
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("device_label")]
    public string DeviceLabel { get; set; } = string.Empty;
}

/// <summary>Custom sing-box JSON paste payload (used when ConfigMode=="custom").</summary>
public sealed class CustomConfigPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sing_box_json")]
    public string SingBoxJson { get; set; } = string.Empty;
}

/// <summary>Opt-in settings block. Field names mirror the Android SharedPreferences keys (and desktop AppSettings.App).</summary>
public sealed class ExportedSettings
{
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; set; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("routing_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoutingMode { get; set; }

    [JsonPropertyName("bypass_ru")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BypassRussianTraffic { get; set; }

    [JsonPropertyName("block_on_vpn_fail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BlockOnVpnFail { get; set; }

    [JsonPropertyName("dns_strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DnsStrategy { get; set; }

    [JsonPropertyName("update_channel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdateChannel { get; set; }

    [JsonPropertyName("autostart_vpn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutostartVpn { get; set; }

    [JsonPropertyName("autostart_zapret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutostartZapret { get; set; }

    [JsonPropertyName("autostart_tgproxy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutostartTgProxy { get; set; }
}

/// <summary>Opt-in per-app filter block. Mirrors VpnService.Builder allow/disallow lists.</summary>
public sealed class PerAppFilterExport
{
    /// <summary>"off" | "include" | "exclude" — see PerAppFilterMode.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "off";

    [JsonPropertyName("packages")]
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
