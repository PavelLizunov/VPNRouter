using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.30.0-r3 — import/export of <see cref="CustomRule"/> lists across
/// three formats:
///
/// <list type="bullet">
/// <item><b>CSV</b> — simple flat-table format (header row +
/// action,type,value,comment,enabled). Easy for spreadsheet edits.</item>
/// <item><b>VPNRouter JSON</b> — System.Text.Json over List&lt;CustomRule&gt;.
/// Native, lossless. Default for export.</item>
/// <item><b>sing-box-native</b> — sing-box <c>route.rules</c> JSON
/// fragment as exported by NekoBox / Hiddify. Cross-import from those
/// apps. Lossy: sing-box rules can have multiple match types per rule
/// (e.g. domain_suffix + network), our schema is one-match-per-rule —
/// we explode such rules into multiple entries.</item>
/// </list>
///
/// <para>Format auto-detection: file extension (.csv / .json) +
/// content sniffing (presence of "outbound" or "action" keys vs
/// "action" + "type" keys distinguishes sing-box-native from ours).</para>
/// </summary>
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

    /// <summary>Import rules from text content. <paramref name="format"/>
    /// = Auto auto-detects via content sniff. Returns parsed rules +
    /// any per-line warnings (lossy conversions, skipped entries).</summary>
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

    /// <summary>Export rules to text. Default = VpnrouterJson.</summary>
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

    /// <summary>Detect the format from a content fragment.</summary>
    public static Format Detect(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("[") || trimmed.StartsWith("{"))
        {
            // JSON-like. Distinguish ours (rules with "action"+"type"+"value")
            // from sing-box-native (rules with match-fields like "domain_suffix"
            // and "outbound" or "action":"reject").
            if (trimmed.Contains("\"outbound\":") ||
                trimmed.Contains("\"domain_suffix\":") ||
                trimmed.Contains("\"ip_cidr\":") ||
                trimmed.Contains("\"process_name\":["))
            {
                return Format.SingBoxJson;
            }
            return Format.VpnrouterJson;
        }
        // Otherwise assume CSV.
        return Format.Csv;
    }

    // ─── CSV ──────────────────────────────────────────────────────────────

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
            // Skip header row if it looks like one (case-insensitive
            // "action" first token).
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
                    // Escaped "" inside quoted = literal ".
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

    // ─── VPNRouter JSON (native) ──────────────────────────────────────────

    // Phase 6 — .NET 10 ships with
    // JsonSerializerIsReflectionEnabledByDefault=false. Without a
    // TypeInfoResolver, serialization throws at first call. Combine the
    // source-gen context with the reflective fallback so List<CustomRule>
    // round-trips on both .NET 8 and .NET 10 runtimes.
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
            // v2.30.0-r20 — accept either a bare array `[ {...} ]` (default
            // export shape) OR a wrapping object `{ "rules": [...] }` (user
            // edits, sample files with $schema metadata, etc.). Pre-r20 only
            // accepted the bare array; user report «example-rules.json
            // выдаёт ошибки» because the sample shipped with a wrapping
            // object.
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
            var rules = JsonSerializer.Deserialize<List<CustomRule>>(arr.GetRawText(), JsonOptions)
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
        return JsonSerializer.Serialize(rules.ToList(), JsonOptions);
    }

    // ─── sing-box-native JSON ─────────────────────────────────────────────

    /// <summary>
    /// Import a sing-box <c>route.rules</c> array fragment. Each rule may
    /// have multiple match fields (e.g. domain_suffix + network); we
    /// explode such rules into multiple <see cref="CustomRule"/> entries
    /// (one per match field) since our schema is one-match-per-rule.
    /// Action mapping:
    /// <list type="bullet">
    /// <item><c>"outbound":"direct"</c> ⇒ direct</item>
    /// <item><c>"outbound":"proxy"</c> (or any non-direct/dns-out) ⇒ proxy</item>
    /// <item><c>"action":"reject"</c> ⇒ block</item>
    /// </list>
    /// </summary>
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

        // Accept either a bare rules array OR a wrapping {"route":{"rules":[...]}}
        // OR {"rules":[...]} object.
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

            // Determine action.
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
                else action = "proxy"; // any other tag = through-VPN
            }

            // Iterate match fields, emit one CustomRule per match.
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
                ("rule_set", "geosite"),  // best-guess; user can flip to geoip
            };

            int matchCount = 0;
            foreach (var (jsonKey, ourType) in matchFields)
            {
                if (!rule.TryGetProperty(jsonKey, out var matchEl)) continue;

                // Values can be string, number, or array.
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

                // For rule_set, strip any "user-geosite-" / "user-geoip-" /
                // "vpnrouter-geo*-" prefix that we add on export.
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

    /// <summary>Export rules as a sing-box <c>route.rules</c> array.
    /// Each <see cref="CustomRule"/> becomes one entry.</summary>
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

            // Match field.
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

            // Action.
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
        // Phase 6 — Wave 31b (2026-05-19): retire the inline
        // `new JsonSerializerOptions { WriteIndented = true }` duplicate.
        // Reuse the file's existing JsonOptions field (also
        // WriteIndented=true; PropertyNamingPolicy=SnakeCaseLower is a
        // no-op here because the serialised payload is
        // List<object>/Dictionary<string,object> — naming policy only
        // applies to property names, and these structures expose no
        // typed properties to the serializer. Dictionary keys + nested
        // List<string>/List<int>/string/int values pass through verbatim.
        //
        // Note: this is the one branch in this file that the AOT
        // source-gen cannot pin via AppJsonContext — the
        // object/Dictionary recursion is fundamentally reflective.
        // Wave 31b leaves it on the reflective fallback path; a
        // future wave will restructure the export DTO to a concrete
        // record tree (one record per match-type + one wrapper) to
        // make it AOT-clean. For now, the duplicate-options cleanup
        // is enough — and the existing test
        // `SingBoxJson_ExportProducesValidImportableForm` pins the
        // wire format byte-equivalent.
        //
        // (Supersedes the e3b3ef4 hotfix's separate SingBoxNativeOptions
        // field — that was a defensive guard before Wave 31b's analysis
        // showed JsonOptions covers this case fine.)
        return JsonSerializer.Serialize(entries, JsonOptions);
    }
}
