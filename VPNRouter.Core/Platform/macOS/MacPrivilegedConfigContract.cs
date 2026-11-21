using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// Normalizes generated or imported sing-box JSON into the typed contract the
/// privileged helper independently enforces. Object keys are canonicalized to
/// printable ASCII, and reserved structural names must keep their exact
/// lowercase spelling, before any lookup. This class improves the normal path
/// and error messages; it is never the authority for root execution.
/// </summary>
public static class MacPrivilegedConfigContract
{
    private const int MaxJsonDepth = 64;

    // sing-box binds struct fields case-insensitively, so "Inbounds" selects
    // the same field as "inbounds". These names carry the privileged contract;
    // only their exact lowercase spelling is accepted before any lookup.
    private static readonly string[] ReservedStructuralKeys =
    {
        "log", "output", "experimental", "cache_file", "services", "clash_api",
        "external_ui", "external_controller", "secret", "inbounds", "type", "route",
        "rule_set", "format", "path", "certificate_path", "key_path",
        "client_certificate_path", "client_key_path", "ech_file", "include",
        "geosite", "geoip", "v2ray_api", "external_ui_download_url",
        "external_ui_download_detour", "store_mode", "store_selected",
        "store_fake_ip",
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
    };

    public static string Prepare(string configJson) =>
        Prepare(configJson, MacPrivilegePaths.CurrentUid, AppPaths.GeoDir, AppPaths.CacheDir);

    /// <summary>
    /// Adds a per-config loopback controller secret before the app reads the
    /// controller endpoint. The helper requires the same secret, so a different
    /// local user cannot call the root sing-box API without it.
    /// </summary>
    public static string EnsureControllerSecret(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new InvalidOperationException("Privileged sing-box config is empty.");

        ValidateCanonicalKeys(configJson);
        var root = JsonNode.Parse(configJson) as JsonObject
            ?? throw new InvalidOperationException("Privileged sing-box config must be a JSON object.");
        EnsureController(root);
        var output = root.ToJsonString(OutputOptions);
        ValidateCanonicalKeys(output);
        return output;
    }

    public static string Prepare(string configJson, int uid, string geoDir, string cacheDir)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new InvalidOperationException("Privileged sing-box config is empty.");

        // Keys must be unambiguous before normalization or any lookup. The
        // helper compares raw key bytes while sing-box decodes escapes and
        // binds fields case-insensitively, so an escaped or case-variant
        // structural key could mean one thing here and another at runtime.
        ValidateCanonicalKeys(configJson);

        var root = JsonNode.Parse(configJson) as JsonObject
            ?? throw new InvalidOperationException("Privileged sing-box config must be a JSON object.");

        EnsureController(root);
        NormalizeLog(root);
        NormalizeExperimental(root);
        ValidateInbounds(root);
        RewriteRuleSets(root, uid, geoDir, cacheDir);
        RejectForbiddenKeys(root, parentKey: null);
        var output = root.ToJsonString(OutputOptions);
        // The serializer may escape an HTML-sensitive key even when the input
        // was canonical. The helper only accepts raw canonical keys, so verify
        // the exact bytes that will be staged.
        ValidateCanonicalKeys(output);
        return output;
    }

    private static void ValidateCanonicalKeys(string configJson)
    {
        ReadOnlySpan<byte> utf8 = Encoding.UTF8.GetBytes(configJson);
        var scopes = new Stack<HashSet<string>>();
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                MaxDepth = MaxJsonDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        if (scopes.Count == 0)
                            throw new InvalidOperationException(
                                "Privileged sing-box config has unbalanced objects.");
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (reader.ValueIsEscaped)
                            throw new InvalidOperationException(
                                "Privileged sing-box config keys must be canonical printable ASCII without escapes.");
                        var name = reader.GetString() ?? string.Empty;
                        if (name.Length == 0)
                            throw new InvalidOperationException(
                                "Privileged sing-box config contains an empty key.");
                        foreach (var ch in name)
                        {
                            if (ch < '\u0020' || ch > '\u007e')
                                throw new InvalidOperationException(
                                    "Privileged sing-box config keys must be canonical printable ASCII without escapes.");
                        }
                        if (HasNoncanonicalStructuralCasing(name))
                            throw new InvalidOperationException(
                                $"Privileged sing-box config structural key '{name}' must use its exact lowercase spelling.");
                        if (scopes.Count == 0 || !scopes.Peek().Add(name))
                            throw new InvalidOperationException(
                                $"Privileged sing-box config contains a duplicate key '{name}'.");
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Privileged sing-box config is not valid JSON.", ex);
        }
    }

    private static bool HasNoncanonicalStructuralCasing(string name)
    {
        foreach (var canonical in ReservedStructuralKeys)
        {
            if (name.Length != canonical.Length ||
                string.Equals(name, canonical, StringComparison.Ordinal))
                continue;
            if (string.Equals(name, canonical, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void NormalizeLog(JsonObject root)
    {
        if (!root.TryGetPropertyValue("log", out var node) || node is null) return;
        if (node is not JsonObject log)
            throw new InvalidOperationException("Privileged sing-box 'log' must be an object.");

        // Root must never open a user-controlled log path. sing-box writes to
        // stderr, which the app already drains and records.
        log["output"] = string.Empty;
    }

    private static void NormalizeExperimental(JsonObject root)
    {
        if (!root.TryGetPropertyValue("experimental", out var node) || node is null) return;
        if (node is not JsonObject experimental)
            throw new InvalidOperationException("Privileged sing-box 'experimental' must be an object.");

        experimental.Remove("cache_file");
        experimental.Remove("external_ui");
        experimental.Remove("services");

        if (!experimental.TryGetPropertyValue("clash_api", out var clashNode) || clashNode is null)
            return;
        if (clashNode is not JsonObject clash)
            throw new InvalidOperationException("Privileged sing-box 'clash_api' must be an object.");

        clash.Remove("external_ui");
        var controller = clash["external_controller"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(controller))
            clash["external_controller"] = "127.0.0.1:9090";
        else if (!IsLoopbackController(controller))
            throw new InvalidOperationException(
                "The privileged sing-box Clash API must listen on loopback only.");
    }

    private static void EnsureController(JsonObject root)
    {
        if (!root.TryGetPropertyValue("experimental", out var experimentalNode) ||
            experimentalNode is null)
        {
            experimentalNode = new JsonObject();
            root["experimental"] = experimentalNode;
        }
        if (experimentalNode is not JsonObject experimental)
            throw new InvalidOperationException("Privileged sing-box 'experimental' must be an object.");

        if (!experimental.TryGetPropertyValue("clash_api", out var clashNode) ||
            clashNode is null)
        {
            clashNode = new JsonObject();
            experimental["clash_api"] = clashNode;
        }
        if (clashNode is not JsonObject clash)
            throw new InvalidOperationException("Privileged sing-box 'clash_api' must be an object.");

        var controller = clash["external_controller"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(controller))
            clash["external_controller"] = "127.0.0.1:9090";
        else if (!IsLoopbackController(controller))
            throw new InvalidOperationException(
                "The privileged sing-box Clash API must listen on loopback only.");

        var secret = clash["secret"]?.GetValue<string>();
        if (!IsSafeSecret(secret))
            clash["secret"] = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    private static bool IsSafeSecret(string? value)
    {
        if (value is null || value.Length < 32 || value.Length > 128)
            return false;
        foreach (var ch in value)
        {
            var ascii = (ch >= 'a' && ch <= 'z') ||
                        (ch >= 'A' && ch <= 'Z') ||
                        (ch >= '0' && ch <= '9');
            if (!ascii && ch is not ('_' or '-' or '.' or '~'))
                return false;
        }
        return true;
    }

    private static void ValidateInbounds(JsonObject root)
    {
        if (!root.TryGetPropertyValue("inbounds", out var node) || node is not JsonArray inbounds ||
            inbounds.Count == 0)
            throw new InvalidOperationException(
                "Privileged sing-box config must contain at least one TUN inbound.");

        foreach (var entry in inbounds)
        {
            if (entry is not JsonObject inbound ||
                !string.Equals(inbound["type"]?.GetValue<string>(), "tun", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Privileged sing-box config may contain TUN inbounds only.");
        }
    }

    private static void RewriteRuleSets(JsonObject root, int uid, string geoDir, string cacheDir)
    {
        if (!root.TryGetPropertyValue("route", out var routeNode) || routeNode is null) return;
        if (routeNode is not JsonObject route)
            throw new InvalidOperationException("Privileged sing-box 'route' must be an object.");
        if (!route.TryGetPropertyValue("rule_set", out var setsNode) || setsNode is null) return;
        if (setsNode is not JsonArray sets)
            throw new InvalidOperationException("Privileged sing-box 'route.rule_set' must be an array.");

        foreach (var entry in sets)
        {
            if (entry is not JsonObject set)
                throw new InvalidOperationException("Every privileged rule_set entry must be an object.");

            var type = set["type"]?.GetValue<string>();
            var format = set["format"]?.GetValue<string>();
            var path = set["path"]?.GetValue<string>();
            if (!string.Equals(type, "local", StringComparison.Ordinal) ||
                !string.Equals(format, "binary", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException(
                    "Privileged rule sets must be local binary .srs files.");

            var fullPath = Path.GetFullPath(path);
            if (MacPrivilegePaths.IsRootAssetPath(fullPath, uid))
                continue;

            if (!IsUnder(fullPath, geoDir) && !IsUnder(fullPath, cacheDir))
                throw new InvalidOperationException(
                    "Privileged rule sets must live in the VPNRouter geo or cache directory.");
            if (!File.Exists(fullPath))
                throw new InvalidOperationException("Privileged rule set is missing on disk.");

            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Privileged rule set has an unsafe file name.");

            set["path"] = MacPrivilegePaths.UserAssetPath(uid, fileName);
        }
    }

    private static void RejectForbiddenKeys(JsonNode node, string? parentKey)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (IsForbiddenKey(pair.Key, parentKey))
                    throw new InvalidOperationException(
                        $"Privileged sing-box config contains the forbidden key '{pair.Key}'.");
                if (pair.Value is not null)
                    RejectForbiddenKeys(pair.Value, pair.Key);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var entry in array)
                if (entry is not null)
                    RejectForbiddenKeys(entry, parentKey);
        }
    }

    private static bool IsForbiddenKey(string key, string? parentKey) => key switch
    {
        "cache_file" => true,
        "external_ui" => true,
        "services" => true,
        "v2ray_api" => true,
        "external_ui_download_url" => true,
        "external_ui_download_detour" => true,
        "store_mode" => true,
        "store_selected" => true,
        "store_fake_ip" => true,
        "certificate_path" => true,
        "key_path" => true,
        "client_certificate_path" => true,
        "client_key_path" => true,
        "ech_file" => true,
        "include" => true,
        "geosite" => true,
        "geoip" => true,
        "output" => !string.Equals(parentKey, "log", StringComparison.Ordinal),
        _ => false,
    };

    internal static bool IsLoopbackController(string value)
    {
        if (!Uri.TryCreate("http://" + value, UriKind.Absolute, out var uri))
            return false;
        return uri.IdnHost.Trim('[', ']') is "127.0.0.1" or "::1" or "localhost";
    }

    private static bool IsUnder(string fullPath, string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.EndsWith(Path.DirectorySeparatorChar))
            fullDirectory += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.Ordinal);
    }
}
