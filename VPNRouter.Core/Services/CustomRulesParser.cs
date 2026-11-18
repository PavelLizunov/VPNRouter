using System.Globalization;
using System.Text;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class CustomRulesParser
{
    private static readonly HashSet<string> KnownActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "proxy", "block",
    };

    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain", "domain_suffix", "domain_keyword",
        "ip_cidr", "port", "port_range", "network",
        "process_name", "geosite", "geoip",
    };

    private static readonly HashSet<string> KnownNetworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "tcp", "udp",
    };

    public sealed record ParseError(int LineNumber, string Line, string Reason);

    public sealed record ParseResult(List<CustomRule> Rules, List<ParseError> Errors);

    public static ParseResult ParseFromText(string? text)
    {
        var rules = new List<CustomRule>();
        var errors = new List<ParseError>();
        if (string.IsNullOrWhiteSpace(text))
            return new ParseResult(rules, errors);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("#")) continue;

            var enabled = true;
            if (trimmed.StartsWith("!"))
            {
                enabled = false;
                trimmed = trimmed[1..].TrimStart();
            }

            var commentStart = FindInlineCommentStart(trimmed);
            var comment = string.Empty;
            if (commentStart >= 0)
            {
                comment = trimmed[(commentStart + 1)..].Trim();
                trimmed = trimmed[..commentStart].TrimEnd();
            }

            var tokens = SplitFirstThree(trimmed);
            if (tokens == null)
            {
                errors.Add(new ParseError(i + 1, raw,
                    "Expected '<action> <type> <value>' (3 fields)"));
                continue;
            }

            var (action, type, value) = tokens.Value;

            var rule = new CustomRule { Action = action, Type = type, Value = value, Comment = comment, Enabled = enabled };
            if (Normalize(rule) is { } reason)
                errors.Add(new ParseError(i + 1, raw, reason));
            else
                rules.Add(rule);
        }

        return new ParseResult(rules, errors);
    }

    // Callers validate detached rows before publishing any part of a replacement.
    public static string? Normalize(CustomRule rule)
    {
        if (!KnownActions.Contains(rule.Action)) return "Unknown action (expected direct, proxy or block)";
        if (!KnownTypes.Contains(rule.Type)) return "Unknown rule type";
        if (string.IsNullOrWhiteSpace(rule.Value) || rule.Value.Any(c => c is '\r' or '\n') ||
            FindInlineCommentStart(" " + rule.Value) >= 0 || rule.Comment?.Any(c => c is '\r' or '\n') == true)
            return "Invalid or empty rule value";

        var type = rule.Type.ToLowerInvariant();
        var values = rule.Value.Split(',', StringSplitOptions.TrimEntries);
        if (values.Any(string.IsNullOrEmpty)) return "Empty rule value";
        if (values.Any(v => v.Any(char.IsControl))) return "Invalid rule value";
        string? error = type switch
        {
            "ip_cidr" when !values.All(IsValidCidr) => "Invalid CIDR",
            "port" when !values.All(IsValidPort) => "Invalid port (must be 1-65535)",
            "port_range" when !values.All(v => ToSingBoxPortRange(v) != null) => "Invalid port range",
            "network" when !values.All(KnownNetworks.Contains) => "Invalid network (expected tcp or udp)",
            "geosite" or "geoip" when !values.All(IsValidRuleSetName) => "Invalid rule-set category name",
            _ => null,
        };
        if (error != null) return error;
        if (type is "geosite" or "geoip" or "network")
            values = values.Select(v => v.ToLowerInvariant()).ToArray();
        rule.Action = rule.Action.ToLowerInvariant();
        rule.Type = type;
        rule.Value = string.Join(", ", values);
        return null;
    }

    public static string SerializeToText(IReadOnlyList<CustomRule>? rules)
    {
        if (rules == null || rules.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var r in rules)
        {
            if (!r.Enabled) sb.Append('!');
            sb.Append(r.Action ?? "direct");
            sb.Append(' ');
            sb.Append(r.Type ?? "domain_suffix");
            sb.Append(' ');
            sb.Append(r.Value ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(r.Comment))
            {
                sb.Append("  # ");
                sb.Append(r.Comment.Trim());
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static List<string> DetectConflicts(IReadOnlyList<CustomRule> rules)
    {
        var conflicts = new List<string>();
        for (int i = 0; i < rules.Count - 1; i++)
        {
            var r = rules[i];
            if (!r.Enabled) continue;
            if (IsCatchAll(r))
            {
                conflicts.Add(
                    $"line {i + 1}: '{r.Action} {r.Type} {r.Value}' matches everything " +
                    $"— rules below this line will never fire.");
            }
        }
        return conflicts;
    }

    private static int FindInlineCommentStart(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '#' && char.IsWhiteSpace(s[i - 1]))
                return i;
        }
        return -1;
    }

    private static (string a, string b, string c)? SplitFirstThree(string s)
    {
        int p = 0;
        var (a, p1) = ReadToken(s, p);
        if (p1 < 0) return null;
        var (b, p2) = ReadToken(s, p1);
        if (p2 < 0) return null;
        var rest = s[p2..].Trim();
        if (rest.Length == 0) return null;
        return (a, b, rest);
    }

    private static (string token, int next) ReadToken(string s, int from)
    {
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;
        if (from >= s.Length) return (string.Empty, -1);
        var start = from;
        while (from < s.Length && !char.IsWhiteSpace(s[from])) from++;
        return (s[start..from], from);
    }

    private static bool IsValidCidr(string v)
    {
        var parts = v.Split('/');
        if (parts.Length > 2 || parts[0].Contains('%') ||
            !System.Net.IPAddress.TryParse(parts[0], out var address)) return false;
        bool ipv4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        // IPAddress.TryParse also accepts legacy abbreviated/octal IPv4 values that sing-box rejects.
        if (ipv4 && address.ToString() != parts[0]) return false;
        if (parts.Length == 1) return true; // Native sing-box also accepts a single address.
        return uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
            && bits <= (ipv4 ? 32u : 128u);
    }

    private static bool IsValidPort(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) &&
        p >= 1 && p <= 65535;

    // Saved settings use MIN-MAX. Imported sing-box ranges can also have open bounds.
    internal static string? ToSingBoxPortRange(string value)
    {
        bool native = value.Contains(':');
        var bounds = value.Split(native ? ':' : '-', StringSplitOptions.TrimEntries);
        if (bounds.Length != 2) return null;

        ushort min = 0, max = ushort.MaxValue;
        if ((!native || bounds[0].Length > 0) &&
            !ushort.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out min))
            return null;
        if ((!native || bounds[1].Length > 0) &&
            !ushort.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
            return null;
        if (min > max || (!native && min == 0)) return null;

        var start = bounds[0].Length == 0 ? "" : min.ToString(CultureInfo.InvariantCulture);
        var end = bounds[1].Length == 0 ? "" : max.ToString(CultureInfo.InvariantCulture);
        return $"{start}:{end}";
    }

    private static bool IsValidRuleSetName(string v)
    {
        if (v.Length == 0 || v.Length > 64) return false;
        foreach (var c in v)
        {
            if (!(c == '-' || c == '_' || char.IsAsciiLetterOrDigit(c)))
                return false;
        }
        return true;
    }

    private static bool IsCatchAll(CustomRule r)
    {
        return r.Type switch
        {
            "ip_cidr" => r.Value?.Contains("0.0.0.0/0") == true || r.Value?.Contains("::/0") == true,
            "domain_suffix" => string.IsNullOrWhiteSpace(r.Value) ||
                               r.Value!.Split(',').Any(v => v.Trim() == "."),
            _ => false,
        };
    }
}
