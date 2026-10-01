using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace VPNRouter.Core.Services.Diagnostics;

// Fail safe: a redaction bug leaks credentials, so redact whenever unsure.
public static class DiagnosticsRedactor
{
    public const string Redacted = "***";

    public const string OmittedOnParseFailure =
        "[diagnostics: structured redaction failed for this file; it was omitted to avoid leaking secrets]";

    private static readonly HashSet<string> SafeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "tag", "type", "enabled", "label", "title",
        "server", "server_port", "port", "listen", "listen_port",
        "address", "server_name", "sni",
        "network", "transport", "security", "alpn", "flow", "fingerprint",
        "utls", "public_key", "pbk", "packet_encoding", "disable_sni",
        "insecure", "allow_insecure", "reality", "tls",
        "domain_strategy", "domain_resolver", "address_resolver", "detour",
        "strategy", "action", "outbound", "final", "clash_mode",
        "domain", "domain_suffix", "domain_keyword", "domain_regex",
        "geosite", "geoip", "ip_cidr", "source_ip_cidr", "ip_is_private",
        "port_range", "process_name", "package_name", "network_type",
        "rule_set", "format", "download_detour", "update_interval",
        "inbound", "protocol", "client_subnet", "rewrite_ttl",
        "interface_name", "stack", "mtu", "strict_route",
        "auto_detect_interface", "endpoint_independent_nat", "sniff",
        "sniff_override_destination", "sniff_timeout", "store_fakeip",
        "udp_fragment", "udp_timeout", "tcp_fast_open", "udp_disable_domain_unmapping",
        "up_mbps", "down_mbps", "congestion_control", "idle_timeout",
        "heartbeat", "mtu_discovery",
        "external_controller", "external_ui", "default_mode", "store_rdrc",
        "level", "output", "timestamp",
        "schema_version", "config_mode", "routing_mode", "dns_mode",
        "routing_apps_mode", "routing_apps_include", "routing_apps_exclude",
        "custom_rules_priority", "force_ipv4_only", "strict_mode", "strict_dns",
        "log_level", "bypass_russian_traffic", "bypassrussiantraffic",
        "block_on_vpn_fail", "include_children", "channel", "auto_update",
        "autostart", "boot_autostart", "start_on_boot", "dns_leak_lockdown",
        "kill_switch", "active_server", "active_subscription_server",
        "active_custom_config", "selected_server_mode", "ui_mode", "theme",
        "language", "minimize_to_tray", "experimental", "prerelease",
    };

    private static readonly HashSet<string> UrlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "subscription_url", "subscriptionurl", "sub_url", "vk_link",
        "wgturn_url", "endpoint", "source", "remote",
    };

    private static readonly Regex _urlKeepHost = new(
        @"^(\w+://)(?:[^@/?#\s]+@)?([^/?#\s]+).*$", RegexOptions.Compiled);

    private static readonly Regex _logKeyValueSecret = new(
        @"(?i)\b((?:[a-z0-9_]*[_-])?(?:password|passwd|pass|secret|token|uuid|short[_-]?id|sid|private[_-]?key|secret[_-]?key|api[_-]?key|access[_-]?key|enc(?:ryption)?[_-]?key|auth[_-]?key|session[_-]?key|client[_-]?key|app[_-]?key|user[_-]?key|psk|pre[_-]?shared[_-]?key|preshared[_-]?key|auth|authorization|proxy[-_]?authorization|credential|obfs[_-]?password)|client[_-]?secret|client[_-]?pass(?:word|wd)?|refresh[_-]?token|access[_-]?token|id[_-]?token|app[_-]?secret)\b([""']?\s*[=:]\s*)([""']?)(?:(?:bearer|basic|token|digest|negotiate)\s+)?([^\s""',]+)",
        RegexOptions.Compiled);

    private static readonly Regex _yamlKeyValuePair = new(
        @"^(\s*(?:-\s*)?([a-zA-Z0-9_-]+)\s*:\s*)(.*)$",
        RegexOptions.Compiled);

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
            return RedactMalformedYaml(yaml);
        }
    }

    internal static string RedactMalformedYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return yaml ?? string.Empty;
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var m = _yamlKeyValuePair.Match(line);
            if (m.Success)
            {
                var key = m.Groups[2].Value.Trim();
                var val = m.Groups[3].Value;
                if (SafeKeys.Contains(key))
                {
                    lines[i] = RedactLogText(line);
                }
                else if (UrlKeys.Contains(key))
                {
                    lines[i] = $"{m.Groups[1].Value}{RedactUrlKeepHost(val)}";
                }
                else
                {
                    lines[i] = $"{m.Groups[1].Value}{Redacted}";
                }
            }
            else
            {
                lines[i] = RedactLogText(line);
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

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

    public static string RedactLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var scrubbed = CrashReporter.ScrubSecrets(lines[i]);
            lines[i] = _logKeyValueSecret.Replace(scrubbed,
                m => $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}{Redacted}");
        }
        return string.Join(Environment.NewLine, lines);
    }

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
                    result.Add(WalkYaml(item, parentKey));
                return result;
            }
            case string s:
                return RedactScalar(parentKey, s);
            default:
                return node;
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
                        arr[i] = RedactScalar(parentKey, s);
                }
                break;
            }
        }
    }

    private static string RedactScalar(string? key, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        if (key != null && SafeKeys.Contains(key))
            return value;

        if (key != null && UrlKeys.Contains(key))
            return RedactUrlKeepHost(value);

        return Redacted;
    }

    private static string RedactUrlKeepHost(string value)
    {
        var m = _urlKeepHost.Match(value);
        return m.Success ? $"{m.Groups[1].Value}{m.Groups[2].Value}/{Redacted}" : Redacted;
    }
}
