using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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
        return detected switch
        {
            Format.Csv => ImportCsv(text),
            Format.VpnrouterJson => ImportVpnrouterJson(text),
            Format.SingBoxJson => ImportSingBoxJson(text),
            _ => new ImportResult(new(), new() { "Unknown format" }, detected),
        };
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
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            if (trimmed.Contains("\"outbound\":") ||
                trimmed.Contains("\"domain_suffix\":") ||
                trimmed.Contains("\"ip_cidr\":") ||
                trimmed.Contains("\"process_name\":["))
            {
                return Format.SingBoxJson;
            }
            return Format.VpnrouterJson;
        }
        return Format.Csv;
    }

    private static ImportResult ImportCsv(string text)
    {
        var rules = new List<CustomRule>();
        var warnings = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool isFirst = true;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (isFirst)
            {
                isFirst = false;
                if (line.StartsWith("action", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var fields = ParseCsvLine(line);
            if (fields.Count < 3)
            {
                warnings.Add($"Line {i + 1}: expected at least 3 fields (action,type,value), got {fields.Count}");
                continue;
            }
            try
            {
                rules.Add(new CustomRule
                {
                    Action = fields[0].Trim().ToLowerInvariant(),
                    Type = fields[1].Trim().ToLowerInvariant(),
                    Value = fields[2].Trim(),
                    Comment = fields.Count > 3 ? fields[3].Trim() : string.Empty,
                    Enabled = fields.Count <= 4 || ParseBool(fields[4]),
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Line {i + 1}: {ex.Message}");
            }
        }
        return new ImportResult(rules, warnings, Format.Csv);
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
        bool inQuotes = false;
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
                    }
                }
                else cur.Append(c);
            }
            else
            {
                if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
                else if (c == '"' && cur.Length == 0) inQuotes = true;
                else cur.Append(c);
            }
        }
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
        return v == "true" || v == "1" || v == "yes" || v == "y";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Json.AppJsonContext.Default,
            new DefaultJsonTypeInfoResolver()),
    };

    private static ImportResult ImportVpnrouterJson(string text)
    {
        var warnings = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("rules", out var inner) &&
                     inner.ValueKind == JsonValueKind.Array)
            {
                arr = inner;
            }
            else
            {
                warnings.Add("JSON: expected an array of rules, or an object with a \"rules\" array");
                return new ImportResult(new(), warnings, Format.VpnrouterJson);
            }
            var rules = JsonSerializer.Deserialize(arr.GetRawText(), VPNRouter.Core.Json.AppJsonContext.Default.ListCustomRule)
                ?? new List<CustomRule>();
            return new ImportResult(rules, warnings, Format.VpnrouterJson);
        }
        catch (Exception ex)
        {
            warnings.Add($"JSON parse failed: {ex.Message}");
            return new ImportResult(new(), warnings, Format.VpnrouterJson);
        }
    }

    private static string ExportVpnrouterJson(IReadOnlyList<CustomRule> rules)
    {
        return JsonSerializer.Serialize(rules.ToList(), VPNRouter.Core.Json.AppJsonContext.Default.ListCustomRule);
    }

    private static ImportResult ImportSingBoxJson(string text)
    {
        var rules = new List<CustomRule>();
        var warnings = new List<string>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            warnings.Add($"sing-box JSON parse failed: {ex.Message}");
            return new ImportResult(rules, warnings, Format.SingBoxJson);
        }

        JsonElement rulesArray;
        if (root.ValueKind == JsonValueKind.Array)
        {
            rulesArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("rules", out var inner)) rulesArray = inner;
            else if (root.TryGetProperty("route", out var route)
                  && route.TryGetProperty("rules", out var nested)) rulesArray = nested;
            else
            {
                warnings.Add("sing-box JSON: no rules array found at root, .rules, or .route.rules");
                return new ImportResult(rules, warnings, Format.SingBoxJson);
            }
        }
        else
        {
            warnings.Add("sing-box JSON: root is neither array nor object");
            return new ImportResult(rules, warnings, Format.SingBoxJson);
        }

        if (rulesArray.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("sing-box JSON: rules is not an array");
            return new ImportResult(rules, warnings, Format.SingBoxJson);
        }

        int idx = 0;
        foreach (var rule in rulesArray.EnumerateArray())
        {
            idx++;
            if (rule.ValueKind != JsonValueKind.Object) continue;

            string action;
            if (rule.TryGetProperty("action", out var actionEl) &&
                actionEl.GetString()?.Equals("reject", StringComparison.OrdinalIgnoreCase) == true)
            {
                action = "block";
            }
            else
            {
                var outbound = rule.TryGetProperty("outbound", out var o) ? o.GetString() : null;
                if (string.IsNullOrEmpty(outbound) || outbound == "direct")
                    action = "direct";
                else if (outbound == "block" || outbound == "reject") action = "block";
                else action = "proxy";
            }

            var matchFields = new[]
            {
                ("domain", "domain"),
                ("domain_suffix", "domain_suffix"),
                ("domain_keyword", "domain_keyword"),
                ("ip_cidr", "ip_cidr"),
                ("port", "port"),
                ("port_range", "port_range"),
                ("network", "network"),
                ("process_name", "process_name"),
                ("rule_set", "geosite"),
            };

            int matchCount = 0;
            foreach (var (jsonKey, ourType) in matchFields)
            {
                if (!rule.TryGetProperty(jsonKey, out var matchEl)) continue;

                var values = new List<string>();
                if (matchEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in matchEl.EnumerateArray())
                        values.Add(v.ToString());
                }
                else if (matchEl.ValueKind == JsonValueKind.String ||
                         matchEl.ValueKind == JsonValueKind.Number)
                {
                    values.Add(matchEl.ToString());
                }
                if (values.Count == 0) continue;

                if (jsonKey == "rule_set")
                {
                    var cleaned = values.Select(v =>
                    {
                        var name = v;
                        foreach (var pfx in new[] { "user-geosite-", "user-geoip-", "vpnrouter-geosite-", "vpnrouter-geoip-" })
                            if (name.StartsWith(pfx)) name = name[pfx.Length..];
                        return name;
                    }).ToList();
                    values = cleaned;
                }

                rules.Add(new CustomRule
                {
                    Action = action,
                    Type = ourType,
                    Value = string.Join(", ", values),
                    Comment = $"sing-box import #{idx}",
                    Enabled = true,
                });
                matchCount++;
            }

            if (matchCount == 0)
            {
                warnings.Add($"sing-box rule #{idx}: no recognized match fields, skipped");
            }
            else if (matchCount > 1)
            {
                warnings.Add($"sing-box rule #{idx}: had {matchCount} match types — exploded into {matchCount} rows (our schema is one-match-per-rule)");
            }
        }

        return new ImportResult(rules, warnings, Format.SingBoxJson);
    }

    private static string ExportSingBoxJson(IReadOnlyList<CustomRule> rules)
    {
        var entries = new List<object>();
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            var values = (r.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (values.Count == 0) continue;

            var entry = new Dictionary<string, object>();

            switch ((r.Type ?? "domain_suffix").ToLowerInvariant())
            {
                case "domain": entry["domain"] = values; break;
                case "domain_suffix": entry["domain_suffix"] = values; break;
                case "domain_keyword": entry["domain_keyword"] = values; break;
                case "ip_cidr": entry["ip_cidr"] = values; break;
                case "port":
                    var ports = values.Select(v => int.TryParse(v, out var p) ? p : 0)
                                       .Where(p => p > 0).ToList();
                    if (ports.Count == 0) continue;
                    entry["port"] = ports;
                    break;
                case "port_range":
                    entry["port_range"] = values;
                    break;
                case "network":
                    entry["network"] = values[0];
                    break;
                case "process_name": entry["process_name"] = values; break;
                case "geosite":
                    entry["rule_set"] = values.Select(v => "user-geosite-" + v).ToList();
                    break;
                case "geoip":
                    entry["rule_set"] = values.Select(v => "user-geoip-" + v).ToList();
                    break;
                default: continue;
            }

            switch ((r.Action ?? "direct").ToLowerInvariant())
            {
                case "direct":
                    entry["action"] = "route";
                    entry["outbound"] = "direct";
                    break;
                case "proxy":
                    entry["action"] = "route";
                    entry["outbound"] = "proxy";
                    break;
                case "block":
                    entry["action"] = "reject";
                    break;
                default: continue;
            }

            entries.Add(entry);
        }
        return JsonSerializer.Serialize(entries, JsonOptions);
    }
}
