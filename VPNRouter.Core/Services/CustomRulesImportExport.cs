using System.Text;
using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class CustomRulesImportExport
{
    public enum Format
    {
        Auto,
        Csv,
        VpnrouterJson,
        SingBoxJson,
    }

    public sealed record ImportResult(
        List<CustomRule> Rules,
        List<string> Warnings,
        Format DetectedFormat);

    public static ImportResult ImportFromText(string text, Format format = Format.Auto)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ImportResult(new(), new() { "Empty input" }, Format.Auto);

        var detected = format == Format.Auto ? Detect(text) : format;
        try
        {
            var result = detected switch
            {
                Format.Csv => ImportCsv(text),
                Format.VpnrouterJson => ImportVpnrouterJson(text),
                Format.SingBoxJson => ImportSingBoxJson(text),
                _ => throw new FormatException("Unknown rule format"),
            };
            foreach (var row in result.Rules)
            {
                if (CustomRulesParser.Normalize(row) is { } error)
                    throw new FormatException(error);
            }
            return result;
        }
        catch (FormatException ex)
        {
            return new ImportResult([], [ex.Message], detected);
        }
        catch (JsonException)
        {
            return new ImportResult([], ["Invalid rules JSON"], detected);
        }
    }

    public static string ExportToText(IReadOnlyList<CustomRule> rules, Format format = Format.VpnrouterJson)
    {
        return format switch
        {
            Format.Csv => ExportCsv(rules),
            Format.VpnrouterJson => ExportVpnrouterJson(rules),
            Format.SingBoxJson => ExportSingBoxJson(rules),
            _ => ExportVpnrouterJson(rules),
        };
    }

    public static Format Detect(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("[") && !trimmed.StartsWith("{"))
            return Format.Csv;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("route", out _))
                return Format.SingBoxJson;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rules", out var nested))
                root = nested;
            if (root.ValueKind == JsonValueKind.Array && root.EnumerateArray().Any(rule =>
                rule.ValueKind == JsonValueKind.Object &&
                (rule.TryGetProperty("outbound", out _) || MatchFields.Any(key => rule.TryGetProperty(key, out _)))))
                return Format.SingBoxJson;
        }
        catch (JsonException) { /* The importer reports malformed JSON. */ }
        return Format.VpnrouterJson;
    }

    private static readonly string[] MatchFields =
    [
        "domain", "domain_suffix", "domain_keyword", "ip_cidr", "port",
        "port_range", "network", "process_name", "rule_set",
    ];

    private static ImportResult ImportCsv(string text)
    {
        var rules = new List<CustomRule>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool isFirst = true;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (isFirst)
            {
                isFirst = false;
                if (line.Equals("action,type,value,comment,enabled", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("action,type,value", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("action,type,value,comment", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var fields = ParseCsvLine(line);
            if (fields.Count is < 3 or > 5)
                throw new FormatException($"Line {i + 1}: expected 3 to 5 CSV fields");
            rules.Add(new CustomRule
            {
                Action = fields[0].Trim(), Type = fields[1].Trim(), Value = fields[2].Trim(),
                Comment = fields.Count > 3 ? fields[3].Trim() : string.Empty,
                Enabled = fields.Count <= 4 || ParseBool(fields[4]),
            });
        }
        return new ImportResult(rules, [], Format.Csv);
    }

    private static string ExportCsv(IReadOnlyList<CustomRule> rules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("action,type,value,comment,enabled");
        foreach (var r in rules)
        {
            sb.Append(EscapeCsv(r.Action ?? "direct")).Append(',');
            sb.Append(EscapeCsv(r.Type ?? "domain_suffix")).Append(',');
            sb.Append(EscapeCsv(r.Value ?? string.Empty)).Append(',');
            sb.Append(EscapeCsv(r.Comment ?? string.Empty)).Append(',');
            sb.Append(r.Enabled ? "true" : "false");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, closedQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        closedQuote = true;
                    }
                }
                else cur.Append(c);
            }
            else
            {
                if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); closedQuote = false; }
                else if (closedQuote) throw new FormatException("Unexpected character after quoted CSV field");
                else if (c == '"' && cur.Length == 0) inQuotes = true;
                else if (c == '"') throw new FormatException("Invalid quote in CSV field");
                else cur.Append(c);
            }
        }
        if (inQuotes) throw new FormatException("Unclosed quoted CSV field");
        fields.Add(cur.ToString());
        return fields;
    }

    private static string EscapeCsv(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static bool ParseBool(string s)
    {
        var v = s.Trim().ToLowerInvariant();
        return v switch
        {
            "true" or "1" or "yes" or "y" => true,
            "false" or "0" or "no" or "n" => false,
            _ => throw new FormatException("Invalid CSV enabled flag"),
        };
    }

    private static void RequireObject(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            obj.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
            throw new FormatException("Expected a rules object with unique fields");
    }

    private static string StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("Expected a string");

    private static string RequiredString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) ? StringValue(value) : throw new FormatException($"Missing {key}");

    private static JsonElement RulesArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            RequireObject(root);
            if (!root.TryGetProperty("rules", out var rules)) throw new FormatException("Missing rules array");
            root = rules;
        }
        return root.ValueKind == JsonValueKind.Array ? root : throw new FormatException("Expected rules array");
    }

    private static ImportResult ImportVpnrouterJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var rules = new List<CustomRule>();
        foreach (var item in RulesArray(doc.RootElement).EnumerateArray())
        {
            RequireObject(item);
            if (item.EnumerateObject().Any(p => p.Name is not ("action" or "type" or "value" or "comment" or "enabled")))
                throw new FormatException("Unsupported custom rule field");
            rules.Add(new CustomRule
            {
                Action = RequiredString(item, "action"), Type = RequiredString(item, "type"),
                Value = RequiredString(item, "value"),
                Comment = item.TryGetProperty("comment", out var comment) ? StringValue(comment) : "",
                Enabled = !item.TryGetProperty("enabled", out var enabled) || (enabled.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false,
                    _ => throw new FormatException("Expected a boolean enabled flag"),
                }),
            });
        }
        return new ImportResult(rules, [], Format.VpnrouterJson);
    }

    private static string ExportVpnrouterJson(IReadOnlyList<CustomRule> rules)
    {
        return JsonSerializer.Serialize(rules.ToList(), VPNRouter.Core.Json.AppJsonContext.Default.ListCustomRule);
    }

    private static ImportResult ImportSingBoxJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var container = root;
        var declaredSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredOutbounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object)
        {
            RequireObject(root);
            ReadDeclaredTags(root, "outbounds", declaredOutbounds);
            ReadDeclaredTags(root, "endpoints", declaredOutbounds);
            ReadDeclaredTags(root, "rule_set", declaredSets);
            if (root.TryGetProperty("route", out var route))
            {
                if (root.TryGetProperty("rules", out _)) throw new FormatException("Ambiguous rules arrays");
                RequireObject(route);
                ReadDeclaredTags(route, "rule_set", declaredSets);
                container = route;
            }
        }

        var rules = new List<CustomRule>();
        int index = 0;
        foreach (var item in RulesArray(container).EnumerateArray())
        {
            index++;
            RequireObject(item);
            var matchers = item.EnumerateObject().Where(p => p.Name is not ("type" or "outbound" or "action" or "invert")).ToList();
            if (matchers.Count != 1 || !MatchFields.Contains(matchers[0].Name) ||
                (item.TryGetProperty("invert", out var invert) && invert.ValueKind != JsonValueKind.False) ||
                (item.TryGetProperty("type", out var type) && StringValue(type) != "default"))
                throw new FormatException($"Rule {index}: expected one supported matcher and no modifiers");

            var outbound = item.TryGetProperty("outbound", out var o) ? StringValue(o).ToLowerInvariant() : null;
            var action = item.TryGetProperty("action", out var a) ? StringValue(a) : null;
            if (outbound != null && declaredOutbounds.Contains(outbound))
                throw new FormatException($"Rule {index}: cannot import a separately defined outbound");
            var direction = (action, outbound) switch
            {
                ("reject", null) or (null, "block" or "reject") => "block",
                ("route" or null, "proxy") => "proxy",
                ("route" or null, "direct") => "direct",
                _ => throw new FormatException($"Rule {index}: unsupported action or outbound"),
            };

            var matcher = matchers[0];
            JsonElement[] elements = matcher.Value.ValueKind == JsonValueKind.Array
                ? matcher.Value.EnumerateArray().ToArray() : [matcher.Value];
            if (elements.Length == 0) throw new FormatException($"Rule {index}: empty matcher");
            var values = elements.Select(v => matcher.Name == "port" && v.ValueKind == JsonValueKind.Number
                ? v.GetRawText() : StringValue(v)).ToList();
            if (values.Any(v => v.Length == 0 || v != v.Trim() || v.Contains(',')))
                throw new FormatException($"Rule {index}: unrepresentable matcher value");

            void AddRow(string kind, IEnumerable<string> entries) => rules.Add(new CustomRule
            {
                Action = direction, Type = kind, Value = string.Join(", ", entries), Comment = $"sing-box import #{index}",
            });

            if (matcher.Name == "rule_set")
            {
                if (values.Any(declaredSets.Contains))
                    throw new FormatException($"Rule {index}: cannot infer the content of a declared rule-set");
                // Tags within the same matcher are alternatives with the same action.
                foreach (var group in values.Select(DecodeRuleSetTag).GroupBy(t => t.Type))
                    AddRow(group.Key, group.Select(t => t.Name));
            }
            else if (matcher.Name == "port_range")
            {
                AddRow(matcher.Name, values.Select(v =>
                {
                    var range = CustomRulesParser.ToSingBoxPortRange(v) ?? throw new FormatException("Invalid port range");
                    return range.StartsWith(':') || range.EndsWith(':') || range.StartsWith("0:", StringComparison.Ordinal)
                        ? range : range.Replace(':', '-');
                }));
            }
            else AddRow(matcher.Name, values);
        }
        return new ImportResult(rules, [], Format.SingBoxJson);
    }

    private static void ReadDeclaredTags(JsonElement obj, string key, HashSet<string> tags)
    {
        if (!obj.TryGetProperty(key, out var declarations)) return;
        if (declarations.ValueKind != JsonValueKind.Array) throw new FormatException($"Expected an array for {key}");
        foreach (var declaration in declarations.EnumerateArray())
        {
            RequireObject(declaration);
            if (!declaration.TryGetProperty("tag", out var tag)) continue;
            var name = StringValue(tag);
            if (key == "outbounds" && declaration.EnumerateObject().All(p => p.Name is "tag" or "type") &&
                declaration.TryGetProperty("type", out var type) &&
                (StringValue(type), name) is ("direct", "direct") or ("block", "block" or "reject"))
                continue;
            tags.Add(name);
        }
    }

    private static (string Type, string Name) DecodeRuleSetTag(string tag)
    {
        if (tag.Equals("vpnrouter-geosite-ru", StringComparison.OrdinalIgnoreCase)) return ("geosite", "tld-ru");
        if (tag.Equals("vpnrouter-geoip-ru", StringComparison.OrdinalIgnoreCase)) return ("geoip", "ru");
        foreach (var type in new[] { "geosite", "geoip" })
        {
            var prefix = $"user-{type}-";
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (type, tag[prefix.Length..]);
        }
        throw new FormatException("Unknown rule-set tag");
    }

    private static string ExportSingBoxJson(IReadOnlyList<CustomRule> rules)
    {
        var routes = rules.Where(rule => rule.Enabled)
            .Select(rule => ConfigGenerator.BuildCustomRouteRule(rule) ?? throw new FormatException("Invalid custom rule"))
            .ToList();
        return JsonSerializer.Serialize(routes, Json.AppJsonContext.Default.ListRouteRule);
    }
}
