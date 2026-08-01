using System.Globalization;
using System.Text;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.30.0 — text-format parser/serializer for <see cref="CustomRule"/>
/// list. Extends the v2.29.0-r4 direct-only rule format with explicit
/// Action keyword (direct/proxy/block) + new match types (geosite,
/// geoip, port_range, network).
///
/// <para>Text format (one rule per line):</para>
/// <code>
/// # Comments start with #. Empty lines are ignored.
/// # Disabled rules: prefix with !
///
/// # Format: &lt;action&gt; &lt;type&gt; &lt;value&gt; [, &lt;value&gt;...] [# inline comment]
/// # Actions: direct / proxy / block
/// # Types: domain / domain_suffix / domain_keyword / ip_cidr / port /
/// #        port_range / network / process_name / geosite / geoip
///
/// # Direct (bypass VPN, send via real interface):
/// direct ip_cidr 10.0.0.0/8, 192.168.0.0/16    # LAN
/// direct domain_suffix .internal.corp
///
/// # Proxy (force through VPN, even if app not in selection):
/// proxy domain_suffix .corp.example
/// proxy port_range 1024-5000
/// proxy geosite category-news-iran
///
/// # Block (drop with TCP RST):
/// block domain_keyword tracker
/// block geosite ads
/// block geoip cn
///
/// !block port 53                                # disabled rule
/// </code>
///
/// <para>Round-trip property: <c>SerializeToText(ParseFromText(text))</c>
/// preserves Action/Type/Value/Comment/Enabled, may reformat whitespace.</para>
/// </summary>
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

    /// <summary>Parse the user-edited text into a (rules, errors) pair.
    /// Errors don't fail the whole parse — valid lines are still returned;
    /// the UI surfaces the error list as inline diagnostics.</summary>
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
                if (trimmed.Length == 0) continue;
            }

            // Strip inline comment (# preceded by whitespace).
            var commentStart = FindInlineCommentStart(trimmed);
            var comment = string.Empty;
            if (commentStart >= 0)
            {
                comment = trimmed[(commentStart + 1)..].Trim();
                trimmed = trimmed[..commentStart].TrimEnd();
            }

            // Tokenize: <action> <type> <value...>
            var tokens = SplitFirstThree(trimmed);
            if (tokens == null)
            {
                errors.Add(new ParseError(i + 1, raw,
                    "Expected '<action> <type> <value>' (3 fields)"));
                continue;
            }

            var (action, type, value) = tokens.Value;

            if (!KnownActions.Contains(action))
            {
                errors.Add(new ParseError(i + 1, raw,
                    $"Unknown action '{action}'. Allowed: direct / proxy / block"));
                continue;
            }

            if (!KnownTypes.Contains(type))
            {
                errors.Add(new ParseError(i + 1, raw,
                    $"Unknown type '{type}'. Allowed: domain / domain_suffix / domain_keyword / " +
                    $"ip_cidr / port / port_range / network / process_name / geosite / geoip"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new ParseError(i + 1, raw, "Empty value"));
                continue;
            }

            var values = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => v.Length > 0)
                .ToList();
            if (values.Count == 0)
            {
                errors.Add(new ParseError(i + 1, raw, "Empty value list after splitting on ','"));
                continue;
            }

            // Per-type validation.
            var lowerType = type.ToLowerInvariant();
            string? typeError = lowerType switch
            {
                "ip_cidr"     => values.All(IsValidCidr) ? null
                    : "Invalid CIDR format (expected e.g. 10.0.0.0/8)",
                "port"        => values.All(IsValidPort) ? null
                    : "Invalid port (must be 1-65535)",
                "port_range"  => values.All(IsValidPortRange) ? null
                    : "Invalid port range (expected MIN-MAX, both 1-65535, MIN <= MAX)",
                "network"     => values.All(v => KnownNetworks.Contains(v)) ? null
                    : "Invalid network (must be 'tcp' or 'udp')",
                "geosite"     => values.All(IsValidRuleSetName) ? null
                    : "Invalid geosite name (lowercase letters/digits/dash, e.g. 'ru', 'ads', 'category-games')",
                "geoip"       => values.All(IsValidRuleSetName) ? null
                    : "Invalid geoip name (lowercase letters/digits/dash, e.g. 'ru', 'cn', 'us')",
                _             => null,
            };
            if (typeError != null)
            {
                errors.Add(new ParseError(i + 1, raw, typeError));
                continue;
            }

            rules.Add(new CustomRule
            {
                Action = action.ToLowerInvariant(),
                Type = lowerType,
                Value = string.Join(", ", values),
                Comment = comment,
                Enabled = enabled,
            });
        }

        return new ParseResult(rules, errors);
    }

    /// <summary>Render rules to canonical multi-line text. Round-trips
    /// losslessly with <see cref="ParseFromText"/>.</summary>
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

    /// <summary>Detects rules that would shadow all subsequent rules
    /// (catch-all matches like ip_cidr 0.0.0.0/0). Returns inline
    /// diagnostic strings, empty list if no conflicts.</summary>
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

    // ─── helpers ──────────────────────────────────────────────────────────

    private static int FindInlineCommentStart(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '#' && char.IsWhiteSpace(s[i - 1]))
                return i;
        }
        return -1;
    }

    /// <summary>Split "a b c d e" into ("a", "b", "c d e"). Whitespace
    /// run treated as a single delimiter. Returns null if fewer than
    /// 3 tokens.</summary>
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
        // Skip leading whitespace.
        while (from < s.Length && char.IsWhiteSpace(s[from])) from++;
        if (from >= s.Length) return (string.Empty, -1);
        var start = from;
        while (from < s.Length && !char.IsWhiteSpace(s[from])) from++;
        return (s[start..from], from);
    }

    private static bool IsValidCidr(string v)
    {
        var slashIdx = v.IndexOf('/');
        if (slashIdx < 1 || slashIdx == v.Length - 1) return false;
        var ip = v[..slashIdx];
        var bitsStr = v[(slashIdx + 1)..];
        if (!int.TryParse(bitsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits))
            return false;
        if (System.Net.IPAddress.TryParse(ip, out var addr))
        {
            int max = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return bits >= 0 && bits <= max;
        }
        return false;
    }

    private static bool IsValidPort(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) &&
        p >= 1 && p <= 65535;

    private static bool IsValidPortRange(string v)
    {
        var dashIdx = v.IndexOf('-');
        if (dashIdx < 1 || dashIdx == v.Length - 1) return false;
        if (!int.TryParse(v[..dashIdx], out var min)) return false;
        if (!int.TryParse(v[(dashIdx + 1)..], out var max)) return false;
        return min >= 1 && max <= 65535 && min <= max;
    }

    private static bool IsValidRuleSetName(string v)
    {
        // sing-box rule_set names: lowercase letters, digits, dash, underscore.
        // Matches common SagerNet names: ru, cn, us, ads, category-games,
        // category-streaming, etc.
        if (v.Length == 0 || v.Length > 64) return false;
        foreach (var c in v)
        {
            if (!(c == '-' || c == '_' || char.IsLower(c) || char.IsDigit(c)))
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
