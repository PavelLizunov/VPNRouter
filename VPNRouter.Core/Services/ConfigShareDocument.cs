#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed class ConfigShareDocument
{
    public const string SchemaMarker = "vpnrouter-config-share";

    public const int CurrentVersion = 1;

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = SchemaMarker;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("exported_at")]
    public DateTimeOffset ExportedAt { get; set; }

    [JsonPropertyName("exported_from")]
    public ExportedFromInfo ExportedFrom { get; set; } = new();

    [JsonPropertyName("config_mode")]
    public string ConfigMode { get; set; } = "subscribe";

    [JsonPropertyName("subscriptions")]
    public List<SubscriptionEntry> Subscriptions { get; set; } = new();

    [JsonPropertyName("manual_vless_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManualVlessUri { get; set; }

    [JsonPropertyName("custom_config")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CustomConfigPayload? CustomConfig { get; set; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExportedSettings? Settings { get; set; }

    [JsonPropertyName("per_app_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerAppFilterExport? PerAppFilter { get; set; }

    public static string Serialize(ConfigShareDocument doc)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        return JsonSerializer.Serialize(doc, Json.AppJsonContext.Default.ConfigShareDocument);
    }

    public static ConfigShareDocumentParseResult TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ConfigShareDocumentParseResult.Failure("empty content");

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

        document.Subscriptions ??= new List<SubscriptionEntry>();
        document.ExportedFrom ??= new ExportedFromInfo();

        var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subscribe", "manual", "custom"
        };
        if (!allowedModes.Contains(document.ConfigMode ?? string.Empty))
        {
            return ConfigShareDocumentParseResult.Failure(
                $"unknown config_mode '{document.ConfigMode}' (expected: subscribe / manual / custom)");
        }

        if (string.Equals(document.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase) &&
            document.CustomConfig is null)
        {
            return ConfigShareDocumentParseResult.Failure(
                "config_mode='custom' but 'custom_config' payload is missing");
        }

        return ConfigShareDocumentParseResult.Success(document);
    }

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

    public static string SuggestFilename(DateTimeOffset? when = null)
    {
        var ts = (when ?? DateTimeOffset.UtcNow).ToLocalTime();
        return $"vpnrouter-config-{ts:yyyyMMdd-HHmm}.json";
    }
}

public sealed class ExportedFromInfo
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("device_label")]
    public string DeviceLabel { get; set; } = string.Empty;
}

public sealed class CustomConfigPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sing_box_json")]
    public string SingBoxJson { get; set; } = string.Empty;
}

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

public sealed class PerAppFilterExport
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "off";

    [JsonPropertyName("packages")]
    public List<string> Packages { get; set; } = new();
}

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
