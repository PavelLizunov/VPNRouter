using System.Globalization;
using System.Text;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.29.0 — text-format parser/serializer for <see cref="CustomDirectRule"/>
/// list. The UI presents a multi-line TextBox so power users (the target
/// audience for "I want to write my own direct rules") can edit / paste /
/// version-control rules without clicking through a row-by-row table.
///
/// <para>Format (one rule per line):</para>
/// <code>
/// # Comments start with #. Empty lines are ignored.
/// # Disabled rules: prefix with !
///
/// ip_cidr 10.0.0.0/8, 192.168.0.0/16       # Local LANs
/// domain_suffix .lan.local, .corp.example
/// domain_keyword internal
/// port 22, 8080
/// process_name Discord.exe
/// !port 53                                 # disabled (commented-out style)
/// </code>
///
/// <para>Round-trip property: <c>SerializeToText(ParseFromText(text)) ==</c>
/// canonical form (preserves type/value/comment/enabled, may reformat
/// whitespace).</para>
/// </summary>
public static class CustomDirectRulesParser
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain", "domain_suffix", "domain_keyword", "ip_cidr", "port", "process_name",
    };

    public sealed record ParseError(int LineNumber, string Line, string Reason);

    public sealed record ParseResult(List<CustomDirectRule> Rules, List<ParseError> Errors);

    /// <summary>Parse the user-edited text into a (rules, errors) pair.
    /// Errors don't fail the whole parse — valid lines are still returned;
    /// the UI surfaces the error list as inline diagnostics.</summary>
    public static ParseResult ParseFromText(string? text)
    {
        var rules = new List<CustomDirectRule>();
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

            // Strip inline comment after `#` (must be preceded by whitespace
            // so we don't accidentally cut a CIDR with a `#` in it — CIDR
            // never has `#`, but defensive).
            var commentStart = FindInlineCommentStart(trimmed);
            string comment = string.Empty;
            if (commentStart >= 0)
            {
                comment = trimmed[(commentStart + 1)..].Trim();
                trimmed = trimmed[..commentStart].TrimEnd();
            }

            // Split into <type> <value...>.
            var firstSpace = -1;
            for (int c = 0; c < trimmed.Length; c++)
            {
                if (char.IsWhiteSpace(trimmed[c])) { firstSpace = c; break; }
            }
            if (firstSpace < 0)
            {
                errors.Add(new ParseError(i + 1, raw, "Expected '<type> <value>' but no space found"));
                continue;
            }

            var type = trimmed[..firstSpace].Trim();
            var value = trimmed[(firstSpace + 1)..].Trim();

            if (!KnownTypes.Contains(type))
            {
                errors.Add(new ParseError(i + 1, raw,
                    $"Unknown type '{type}'. Allowed: domain / domain_suffix / domain_keyword / ip_cidr / port / process_name"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new ParseError(i + 1, raw, "Empty value"));
                continue;
            }

            // Type-specific validation.
            var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Where(v => v.Length > 0)
                              .ToList();
            if (values.Count == 0)
            {
                errors.Add(new ParseError(i + 1, raw, "Empty value list after splitting on ','"));
                continue;
            }

            var lower = type.ToLowerInvariant();
            string? typeError = lower switch
            {
                "ip_cidr"      => values.All(IsValidCidr) ? null : "Invalid CIDR format (expected e.g. 10.0.0.0/8)",
                "port"         => values.All(IsValidPort) ? null : "Invalid port (must be 1-65535)",
                _              => null,
            };
            if (typeError != null)
            {
                errors.Add(new ParseError(i + 1, raw, typeError));
                continue;
            }

            rules.Add(new CustomDirectRule
            {
                Type = lower,
                Value = string.Join(", ", values),
                Comment = comment,
                Enabled = enabled,
            });
        }

        return new ParseResult(rules, errors);
    }

    /// <summary>Render a list of rules back to multi-line text in canonical
    /// form. Round-trips losslessly with <see cref="ParseFromText"/>.</summary>
    public static string SerializeToText(IReadOnlyList<CustomDirectRule>? rules)
    {
        if (rules == null || rules.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var r in rules)
        {
            if (!r.Enabled) sb.Append('!');
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

    /// <summary>Find the start of an inline comment (`#` preceded by a
    /// whitespace character). Returns -1 if none. Skips `#` at column 0
    /// (already handled at line level).</summary>
    private static int FindInlineCommentStart(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '#' && char.IsWhiteSpace(s[i - 1]))
                return i;
        }
        return -1;
    }

    private static bool IsValidCidr(string v)
    {
        // Format: <ip>/<bits>. ip is IPv4 dotted quad or IPv6 hex+colon.
        var slashIdx = v.IndexOf('/');
        if (slashIdx < 1 || slashIdx == v.Length - 1) return false;
        var ip = v[..slashIdx];
        var bitsStr = v[(slashIdx + 1)..];
        if (!int.TryParse(bitsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)) return false;

        if (System.Net.IPAddress.TryParse(ip, out var addr))
        {
            int max = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return bits >= 0 && bits <= max;
        }
        return false;
    }

    private static bool IsValidPort(string v) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p >= 1 && p <= 65535;
}
