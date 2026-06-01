using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Services.Diagnostics;

/// <summary>
/// Redacts secrets from the structured config files (config.yaml +
/// current.json) and from log text, so a diagnostics bundle can be shared
/// with support without leaking the user's VPN credentials.
///
/// CARDINAL RULE — fail safe. A redaction bug here is a credential leak, so
/// this is built to over-redact rather than under-redact:
///
///  • Structured data (YAML/JSON) uses an ALLOWLIST of known-safe scalar keys.
///    Any key NOT on the allowlist has its scalar value replaced with
///    <c>***</c>. This means an unknown / newly-added secret field defaults to
///    redacted (the audit acceptance criterion "unknown structured fields
///    default to redacted"). Keys are always preserved — only VALUES are
///    redacted — so the bundle stays diagnostic (you see the field exists).
///  • Numbers and booleans are kept regardless of key (a port or a flag is not
///    a credential), which preserves diagnostic value without leaking anything.
///  • URL-bearing keys keep only their host; the path/query (where a
///    subscription token lives) is dropped.
///  • If a file fails to parse, it is OMITTED entirely rather than emitted raw
///    — never risk leaking a secret because the parser tripped.
///  • Log text (no key structure) uses the existing best-effort regex scrubber
///    <see cref="CrashReporter.ScrubSecrets"/> (proxy URIs, http URLs, UUIDs,
///    long base64/key runs).
/// </summary>
public static class DiagnosticsRedactor
{
    /// <summary>Replacement token for a redacted scalar value.</summary>
    public const string Redacted = "***";

    /// <summary>Emitted in place of a file whose structured redaction failed.</summary>
    public const string OmittedOnParseFailure =
        "[diagnostics: structured redaction failed for this file; it was omitted to avoid leaking secrets]";

    // Known-safe scalar keys. ONLY these have their string value preserved;
    // everything else is replaced with `***`. Generous but deliberately
    // excludes every credential field (uuid, password, short_id, private_key,
    // secret, token, psk, auth, generic "key") and PII-heavy paths
    // (process_path, rule_set local path can carry the OS username).
    private static readonly HashSet<string> SafeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // identity / structure
        "name", "tag", "type", "enabled", "label", "title",
        // endpoint (host kept per design — not a credential)
        "server", "server_port", "port", "listen", "listen_port",
        "address", "server_name", "sni",
        // transport / TLS (public-by-design values)
        "network", "transport", "security", "alpn", "flow", "fingerprint",
        "utls", "public_key", "pbk", "packet_encoding", "disable_sni",
        "insecure", "allow_insecure", "reality", "tls",
        // DNS / routing structure
        "domain_strategy", "domain_resolver", "address_resolver", "detour",
        "strategy", "action", "outbound", "final", "clash_mode",
        "domain", "domain_suffix", "domain_keyword", "domain_regex",
        "geosite", "geoip", "ip_cidr", "source_ip_cidr", "ip_is_private",
        "port_range", "process_name", "package_name", "network_type",
        "rule_set", "format", "download_detour", "update_interval",
        "inbound", "protocol", "client_subnet", "rewrite_ttl",
        // TUN / inbound knobs
        "interface_name", "stack", "mtu", "strict_route",
        "auto_detect_interface", "endpoint_independent_nat", "sniff",
        "sniff_override_destination", "sniff_timeout", "store_fakeip",
        "udp_fragment", "udp_timeout", "tcp_fast_open", "udp_disable_domain_unmapping",
        // hysteria/tuic/wireguard non-secret knobs
        "up_mbps", "down_mbps", "congestion_control", "idle_timeout",
        "heartbeat", "mtu_discovery",
        // experimental / clash api (controller is local host:port; secret is NOT here)
        "external_controller", "external_ui", "default_mode", "store_rdrc",
        // log
        "level", "output", "timestamp",
        // config.yaml (AppSettings) scalar flags / modes
        "schema_version", "config_mode", "routing_mode", "dns_mode",
        "log_level", "bypass_russian_traffic", "bypassrussiantraffic",
        "block_on_vpn_fail", "include_children", "channel", "auto_update",
        "autostart", "boot_autostart", "start_on_boot", "dns_leak_lockdown",
        "kill_switch", "active_server", "active_subscription_server",
        "active_custom_config", "selected_server_mode", "ui_mode", "theme",
        "language", "minimize_to_tray", "experimental", "prerelease",
    };

    // Keys whose value is a URL: keep the scheme+host (diagnostic — which
    // provider / source), drop the path & query (where tokens hide).
    private static readonly HashSet<string> UrlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "subscription_url", "subscriptionurl", "sub_url", "vk_link",
        "wgturn_url", "endpoint", "source", "remote",
    };

    private static readonly Regex _urlKeepHost = new(
        @"^(\w+://[^/?#\s]+).*$", RegexOptions.Compiled);

    private static readonly Regex _numberLike = new(
        @"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Redact the main settings YAML. Returns redacted YAML, or an omission
    /// placeholder if it cannot be parsed (never the raw input).
    /// </summary>
    public static string RedactConfigYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml ?? string.Empty;
        try
        {
            var deserializer = new DeserializerBuilder().Build();
            var root = deserializer.Deserialize<object?>(yaml);
            var redacted = WalkYaml(root, parentKey: null);
            var serializer = new SerializerBuilder().Build();
            return serializer.Serialize(redacted ?? new Dictionary<object, object>());
        }
        catch
        {
            return OmittedOnParseFailure;
        }
    }

    /// <summary>
    /// Redact a sing-box JSON config (current.json). Returns redacted,
    /// indented JSON, or an omission placeholder if it cannot be parsed.
    /// </summary>
    public static string RedactSingboxJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json ?? string.Empty;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            WalkJson(node, parentKey: null);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                   ?? OmittedOnParseFailure;
        }
        catch
        {
            return OmittedOnParseFailure;
        }
    }

    /// <summary>
    /// Redact free-form log text line by line using the regex scrubber.
    /// </summary>
    public static string RedactLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = CrashReporter.ScrubSecrets(lines[i]);
        return string.Join(Environment.NewLine, lines);
    }

    // ── structured walkers ──────────────────────────────────────────────

    private static object? WalkYaml(object? node, string? parentKey)
    {
        switch (node)
        {
            case IDictionary<object, object> map:
            {
                var result = new Dictionary<object, object>();
                foreach (var kv in map)
                {
                    var key = kv.Key?.ToString();
                    result[kv.Key ?? "null"] = WalkYaml(kv.Value, key) ?? string.Empty;
                }
                return result;
            }
            case IEnumerable<object?> list when node is not string:
            {
                var result = new List<object?>();
                foreach (var item in list)
                    result.Add(WalkYaml(item, parentKey)); // scalar items inherit parent key
                return result;
            }
            case string s:
                return RedactScalar(parentKey, s);
            default:
                return node; // null/number/bool — kept
        }
    }

    private static void WalkJson(JsonNode? node, string? parentKey)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonObject or JsonArray)
                        WalkJson(child, key);
                    else if (child is JsonValue val && val.TryGetValue<string>(out var s))
                        obj[key] = RedactScalar(key, s);
                    // numbers/bools/null untouched
                }
                break;
            }
            case JsonArray arr:
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    if (child is JsonObject or JsonArray)
                        WalkJson(child, parentKey);
                    else if (child is JsonValue val && val.TryGetValue<string>(out var s))
                        arr[i] = RedactScalar(parentKey, s); // scalar items inherit parent key
                }
                break;
            }
        }
    }

    // ── scalar policy ───────────────────────────────────────────────────

    private static string RedactScalar(string? key, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // Numbers / booleans are never credentials — keep regardless of key.
        if (_numberLike.IsMatch(value)) return value;
        if (value is "true" or "false" or "True" or "False") return value;

        if (key != null && UrlKeys.Contains(key))
            return RedactUrlKeepHost(value);

        if (key != null && SafeKeys.Contains(key))
            return value; // allowlisted scalar — safe to keep verbatim

        // Unknown / non-allowlisted key → fail safe.
        return Redacted;
    }

    private static string RedactUrlKeepHost(string value)
    {
        var m = _urlKeepHost.Match(value);
        return m.Success ? $"{m.Groups[1].Value}/{Redacted}" : Redacted;
    }
}
