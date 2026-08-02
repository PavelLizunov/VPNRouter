#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNRouter.Tests;

/// <summary>
/// Phase 2C (Wave 9, 2026-05-18) — characterization helper for
/// <c>VPNRouter.Android.AndroidApp</c>. Sibling of
/// <see cref="PublicSurfaceHashHelper"/> with the same invariant contract
/// (any drift in member shape between pre- and post-split fails the
/// hash check) but a different input modality.
///
/// <para><strong>Why source-based instead of reflection-based?</strong>
/// <see cref="PublicSurfaceHashHelper"/> walks a <see cref="Type"/> via
/// reflection. <c>AndroidApp</c> is compiled into the
/// <c>net8.0-android</c>-target <c>VPNRouter.Android</c> assembly, which
/// the <c>VPNRouter.Tests</c> (net8.0) project can neither
/// <c>ProjectReference</c> nor reflect on. So we go the other way:
/// parse the source files declarations directly.</para>
///
/// <para><strong>What's included</strong> (each declaration of a single
/// <c>partial class AndroidApp</c> body's member, scanned across every
/// <c>AndroidApp*.cs</c> file in the project):</para>
/// <list type="bullet">
///   <item>Field declarations (incl. <c>readonly</c>, <c>static</c>):
///   shape is <c>F:&lt;name&gt;:&lt;type&gt;</c>.</item>
///   <item>Property declarations: <c>P:&lt;name&gt;:&lt;type&gt;</c>.</item>
///   <item>Event declarations: <c>E:&lt;name&gt;:&lt;type&gt;</c>.</item>
///   <item>Method declarations (incl. overrides): shape is
///   <c>M:&lt;name&gt;:&lt;returnType&gt;:(&lt;paramTypes&gt;)</c>.
///   Generic constraints + body content are intentionally ignored —
///   we care about signature drift, not implementation drift.</item>
///   <item>Nested type declarations (<c>private enum ChipState</c>,
///   <c>private enum AdvancedTab</c>): shape is
///   <c>T:&lt;name&gt;:&lt;kind&gt;</c>.</item>
/// </list>
///
/// <para><strong>What's excluded</strong>:</para>
/// <list type="bullet">
///   <item>Anything inside method bodies (local variables, lambdas, etc.)</item>
///   <item>Members of the <c>StyledElementResourceExtensions</c> helper
///   class (separate type, not part of <c>AndroidApp</c>)</item>
///   <item>XML doc comments — informational, not surface</item>
///   <item>Compiler-generated, attribute decorations, partial-method stubs</item>
/// </list>
///
/// <para><strong>Conservative bias</strong>: this parser uses pragmatic
/// regex over normalized source text. It is intentionally narrower than a
/// full Roslyn parse — it captures the bulk of declarations a refactor
/// might disturb (method signatures, field types) while being trivially
/// auditable. The hash captures the union of declarations across all
/// partial files; if any extraction loses or renames a member, the
/// union changes and the hash changes.</para>
/// </summary>
internal static class AndroidAppSourceSurfaceHashHelper
{
    /// <summary>
    /// Find the repository's <c>VPNRouter.Android/</c> directory by
    /// walking upward from the test assembly location until a
    /// <c>VPNRouter.sln</c> is found, then descending. Returns the full
    /// path or null if not found (e.g. running outside the worktree).
    /// </summary>
    public static string? FindAndroidProjectDir()
    {
        var probe = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (string.IsNullOrEmpty(probe)) break;
            var slnPath = Path.Combine(probe, "VPNRouter.sln");
            if (File.Exists(slnPath))
            {
                var dir = Path.Combine(probe, "VPNRouter.Android");
                return Directory.Exists(dir) ? dir : null;
            }
            probe = Path.GetDirectoryName(probe);
            if (probe is null) break;
        }
        return null;
    }

    /// <summary>
    /// Compute the lowercase-hex SHA-256 of the union of AndroidApp
    /// partial-class declarations across all <c>AndroidApp*.cs</c> files
    /// under <paramref name="androidProjectDir"/>.
    /// </summary>
    public static string Compute(string androidProjectDir)
    {
        var descriptions = EnumerateMembers(androidProjectDir)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Detailed dump of every member description — useful when a hash
    /// mismatch reveals drift and you want to see exactly which member
    /// changed.
    /// </summary>
    public static string[] DumpMembers(string androidProjectDir)
    {
        return EnumerateMembers(androidProjectDir)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateMembers(string androidProjectDir)
    {
        // Scan only AndroidApp*.cs files. AndroidApp.axaml.cs + every
        // AndroidApp.<Concern>.cs partial qualifies.
        var files = Directory.EnumerateFiles(androidProjectDir, "AndroidApp*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var normalized = StripCommentsAndStrings(text);
            foreach (var desc in ExtractMembersFromPartialClass(normalized))
                yield return desc;
        }
    }

    /// <summary>
    /// Strip line/block comments and string literals so they don't
    /// confuse the regex extraction (e.g. a string containing
    /// <c>"private int foo"</c> shouldn't register as a field). String
    /// contents are replaced with empty quoted literals to preserve
    /// brace-balance.
    /// </summary>
    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            // Line comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            // Block comment
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                continue;
            }
            // Verbatim string
            if (i + 1 < source.Length && source[i] == '@' && source[i + 1] == '"')
            {
                sb.Append("\"\"");
                i += 2;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            // Interpolated verbatim string
            if (i + 2 < source.Length && source[i] == '$' && source[i + 1] == '@' && source[i + 2] == '"')
            {
                sb.Append("\"\"");
                i += 3;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            // Regular string (escape-aware, single-line)
            if (source[i] == '"')
            {
                sb.Append("\"\"");
                i++;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    i++;
                }
                if (i < source.Length && source[i] == '"') i++;
                continue;
            }
            // Char literal
            if (source[i] == '\'')
            {
                sb.Append("''");
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    i++;
                }
                if (i < source.Length && source[i] == '\'') i++;
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Find every <c>partial class AndroidApp</c> body in the file, and
    /// extract its directly-declared member signatures. Members inside
    /// other classes in the same file (e.g.
    /// <c>StyledElementResourceExtensions</c> in
    /// <c>AndroidApp.axaml.cs</c>) are skipped — we only care about the
    /// god-class itself.
    /// </summary>
    private static IEnumerable<string> ExtractMembersFromPartialClass(string normalized)
    {
        // Locate `partial class AndroidApp` openings.
        var classOpenRegex = new Regex(
            @"\bpartial\s+class\s+AndroidApp\b[^{]*\{",
            RegexOptions.Compiled);

        var matches = classOpenRegex.Matches(normalized);
        foreach (Match m in matches)
        {
            int bodyStart = m.Index + m.Length;
            int bodyEnd = FindMatchingBrace(normalized, m.Index + m.Length - 1);
            if (bodyEnd < 0) continue;

            // Take only the immediate-body text — nested blocks (method
            // bodies) are still in the substring, but our member regex
            // is anchored on member-declaration starts that wouldn't
            // appear inside nested braces.
            var body = normalized.Substring(bodyStart, bodyEnd - bodyStart);
            foreach (var desc in ExtractDeclarations(body))
                yield return desc;
        }
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '{') return -1;
        int depth = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Walk the class body brace-aware, extract every top-level member
    /// declaration (depth-0 within the class body). Methods have braces
    /// so the walker skips their body; field/property/event lines are
    /// the trailing tokens before a semicolon at depth 0.
    ///
    /// <para><strong>Why no angle-bracket tracking?</strong> Generic
    /// types (<c>Dictionary&lt;string,int&gt;</c>) are balanced within
    /// their declaration head — we never need to disambiguate them
    /// from comparison operators when looking for top-level
    /// <c>;</c> or <c>{</c>. Conversely, treating <c>&gt;</c> in
    /// <c>=&gt;</c> (expression-bodied member) as a generic close
    /// would mis-track depth and skip subsequent semicolons. Simpler
    /// to skip angle tracking entirely.</para>
    /// </summary>
    private static IEnumerable<string> ExtractDeclarations(string body)
    {
        var results = new List<string>();

        // Token-by-token walk. Depth tracks nested braces (method
        // bodies, property accessor blocks, lambda bodies, type
        // initializers, etc.). Members are declarations that appear at
        // depth 0 and end either with `;` (field/event/etc.) or with a
        // `{ ... }` block (method/property/nested type).
        int i = 0;
        int n = body.Length;
        int depth = 0;
        int parenDepth = 0;
        var current = new StringBuilder(256);

        while (i < n)
        {
            char c = body[i];
            if (depth == 0 && parenDepth == 0)
            {
                if (c == ';')
                {
                    var decl = current.ToString().Trim();
                    if (!string.IsNullOrEmpty(decl))
                    {
                        var member = ClassifySemicolonDecl(decl);
                        if (member is not null) results.Add(member);
                    }
                    current.Clear();
                    i++;
                    continue;
                }
                if (c == '{')
                {
                    // Method / nested-type body / property accessor block.
                    var decl = current.ToString().Trim();
                    if (!string.IsNullOrEmpty(decl))
                    {
                        var member = ClassifyBlockDecl(decl);
                        if (member is not null) results.Add(member);
                    }
                    current.Clear();
                    depth++;
                    i++;
                    // Skip to matching close brace.
                    while (i < n && depth > 0)
                    {
                        if (body[i] == '{') depth++;
                        else if (body[i] == '}') depth--;
                        i++;
                    }
                    continue;
                }
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                current.Append(c);
                i++;
                continue;
            }
            // Inside paren — accumulate as part of the current declaration
            // head; brace-skipping (for property accessor blocks) is
            // handled above.
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            current.Append(c);
            i++;
        }

        return results;
    }

    /// <summary>
    /// Classify a declaration ending in a semicolon: field, event, or
    /// expression-bodied member.
    /// </summary>
    private static string? ClassifySemicolonDecl(string decl)
    {
        // Drop attributes [Foo, Bar] from the front. There may be
        // multiple, each on its own bracket pair.
        decl = StripLeadingAttributes(decl);

        // Drop modifiers (cumulative; preserve type+name afterwards).
        var modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal",
            "static", "readonly", "const", "extern", "unsafe",
            "abstract", "virtual", "override", "sealed", "new",
            "volatile", "async", "partial", "required"
        };
        var tokens = SplitTokens(decl);
        int idx = 0;
        while (idx < tokens.Count && modifiers.Contains(tokens[idx])) idx++;

        if (idx >= tokens.Count) return null;

        // event keyword?
        if (tokens[idx] == "event" && idx + 2 < tokens.Count)
        {
            var typeStr = tokens[idx + 1];
            var nameStr = StripInitializer(tokens[idx + 2]);
            // Trim invalid chars from name (initializer "Name = value" → "Name").
            return $"E:{nameStr}:{typeStr}";
        }

        // Expression-bodied member: `Type Name => ...;` — we won't
        // see `=>` in tokens here (treated as a normal token); the
        // shape mirrors a field for our purposes.
        // Field/event: at least 2 tokens (type + name) remain.
        if (idx + 1 < tokens.Count)
        {
            var typeStr = tokens[idx];
            var nameStr = StripInitializer(tokens[idx + 1]);
            // If declaration includes `=>` it's an expression-bodied
            // property/method.
            if (decl.Contains("=>"))
            {
                // Distinguish methods from properties by paren presence
                // in the head before the arrow. Trim any trailing `(...)`
                // suffix from nameStr (an expression-bodied method may
                // appear as a single token "Initialize()" because the
                // paren-balanced group survives tokenization).
                int parenCut = nameStr.IndexOf('(');
                if (parenCut > 0) nameStr = nameStr.Substring(0, parenCut);

                int arrowAt = decl.IndexOf("=>");
                var head = arrowAt >= 0 ? decl.Substring(0, arrowAt) : decl;
                if (head.Contains('(')) return $"M:{nameStr}:{typeStr}:({ExtractParamsFromDecl(head)})";
                return $"P:{nameStr}:{typeStr}";
            }
            return $"F:{nameStr}:{typeStr}";
        }
        return null;
    }

    /// <summary>
    /// Classify a declaration whose head was followed by an open brace
    /// (method, property, indexer, nested type).
    /// </summary>
    private static string? ClassifyBlockDecl(string decl)
    {
        decl = StripLeadingAttributes(decl);

        var modifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal",
            "static", "readonly", "const", "extern", "unsafe",
            "abstract", "virtual", "override", "sealed", "new",
            "volatile", "async", "partial", "required"
        };
        var tokens = SplitTokens(decl);
        int idx = 0;
        while (idx < tokens.Count && modifiers.Contains(tokens[idx])) idx++;

        if (idx >= tokens.Count) return null;

        // Nested type?
        if (tokens[idx] == "enum" || tokens[idx] == "class" || tokens[idx] == "struct"
            || tokens[idx] == "interface" || tokens[idx] == "record")
        {
            if (idx + 1 < tokens.Count)
            {
                var nameStr = StripGenericTail(tokens[idx + 1]);
                // Trim any inheritance ": Base" — name token only.
                return $"T:{nameStr}:{tokens[idx]}";
            }
            return null;
        }

        // Method (has parens) vs property (no parens). Token stream
        // for a method may look like ["void", "FooBar", "(", ...]
        // but we joined paren contents as one super-token; check decl
        // string for '(' presence.
        var rawAfterModifiers = string.Join(" ", tokens.Skip(idx));
        bool hasParen = rawAfterModifiers.Contains('(');
        if (hasParen)
        {
            // Find the position of '(' to split type/name and params.
            int parenAt = rawAfterModifiers.IndexOf('(');
            var head = rawAfterModifiers.Substring(0, parenAt).Trim();
            var headTokens = SplitTokens(head);
            if (headTokens.Count < 1) return null;
            // Last token = name; everything before = return type.
            // For ctors there's only one token (the type name).
            string returnType, name;
            if (headTokens.Count == 1)
            {
                // Ctor or finalizer. Treat as method shape with return type "ctor".
                returnType = "ctor";
                name = headTokens[0];
            }
            else
            {
                name = StripGenericTail(headTokens[headTokens.Count - 1]);
                returnType = string.Join(" ", headTokens.Take(headTokens.Count - 1));
            }
            var paramList = ExtractParamsFromDecl(rawAfterModifiers);
            return $"M:{name}:{returnType}:({paramList})";
        }

        // Property: "Type Name {" — exactly two tokens after modifiers.
        if (idx + 1 < tokens.Count)
        {
            var typeStr = tokens[idx];
            var nameStr = tokens[idx + 1];
            return $"P:{nameStr}:{typeStr}";
        }
        return null;
    }

    private static string ExtractParamsFromDecl(string decl)
    {
        int open = decl.IndexOf('(');
        if (open < 0) return string.Empty;
        // Find matching close paren in the head (we ignore the rest
        // — body is already stripped).
        int depth = 0;
        int close = -1;
        for (int j = open; j < decl.Length; j++)
        {
            if (decl[j] == '(') depth++;
            else if (decl[j] == ')')
            {
                depth--;
                if (depth == 0) { close = j; break; }
            }
        }
        if (close < 0) return string.Empty;
        var inner = decl.Substring(open + 1, close - open - 1).Trim();
        if (inner.Length == 0) return string.Empty;

        // Split by commas at the top level (respect generic angle brackets).
        var parts = SplitTopLevelCommas(inner);
        var sb = new StringBuilder();
        for (int k = 0; k < parts.Count; k++)
        {
            if (k > 0) sb.Append(',');
            // Strip param modifiers (ref/out/in/this/params/scoped) +
            // attributes + default-value tails.
            var trimmed = parts[k].Trim();
            // Remove leading [Attr]
            trimmed = Regex.Replace(trimmed, @"^\[[^\]]*\]\s*", "");
            // Strip default value tail " = ..."
            int eq = trimmed.IndexOf('=');
            if (eq > 0) trimmed = trimmed.Substring(0, eq).Trim();
            var pTokens = SplitTokens(trimmed);
            var paramMods = new HashSet<string>(StringComparer.Ordinal)
            {
                "ref", "out", "in", "this", "params", "scoped"
            };
            int p = 0;
            while (p < pTokens.Count && paramMods.Contains(pTokens[p])) p++;
            // Parameter is "Type name" — drop the name (the type is the
            // signature invariant). If only one token, that IS the type.
            if (p < pTokens.Count)
            {
                int last = pTokens.Count - 1;
                int typeEnd = (last > p) ? last - 1 : p;
                var typeStr = string.Join(" ", pTokens.Skip(p).Take(typeEnd - p + 1));
                sb.Append(typeStr);
            }
        }
        return sb.ToString();
    }

    private static List<string> SplitTopLevelCommas(string s)
    {
        // SplitTopLevelCommas operates on a parameter list (between
        // method-decl parens). We DO need angle tracking here so that
        // a generic-typed parameter like <c>Dictionary&lt;string,int&gt; x</c>
        // doesn't get split on the inner comma. Unlike the walker
        // above, this string was already extracted from inside a
        // parens-balanced region so there's no <c>=&gt;</c> false-positive
        // risk.
        var result = new List<string>();
        int depth = 0;
        int angle = 0;
        int paren = 0;
        var current = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '<') angle++;
            else if (c == '>') angle--;
            else if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0 && angle == 0 && paren == 0)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Split a declaration head into whitespace-separated tokens,
    /// preserving generic + nested types (Foo&lt;Bar, Baz&gt;) and
    /// paren-balanced groups (params lists) as single tokens.
    ///
    /// <para><strong>Arrow-aware</strong>: <c>=&gt;</c> in an
    /// expression-bodied member must NOT be treated as a generic close
    /// — we look back one char in the current buffer to detect it.</para>
    /// </summary>
    private static List<string> SplitTokens(string s)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        int angle = 0;
        int paren = 0;
        int bracket = 0;
        for (int idx = 0; idx < s.Length; idx++)
        {
            var c = s[idx];
            if (char.IsWhiteSpace(c) && angle == 0 && paren == 0 && bracket == 0)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            if (c == '<')
            {
                angle++;
            }
            else if (c == '>')
            {
                // Distinguish generic close from `=>` (arrow) and
                // `>=` / `>>` operators. Only decrement on a genuine
                // generic close. Heuristic: if the previous non-space
                // char in `cur` is `=`, this is an arrow — leave angle
                // alone. If next char is `=` or `>`, this is a binary
                // operator — also leave angle alone.
                bool prevIsEqual = cur.Length > 0 && cur[cur.Length - 1] == '=';
                bool nextIsOperator = idx + 1 < s.Length && (s[idx + 1] == '=' || s[idx + 1] == '>');
                if (!prevIsEqual && !nextIsOperator && angle > 0)
                    angle--;
            }
            else if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '[') bracket++;
            else if (c == ']') bracket--;
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    private static string StripLeadingAttributes(string decl)
    {
        while (true)
        {
            int i = 0;
            while (i < decl.Length && char.IsWhiteSpace(decl[i])) i++;
            if (i >= decl.Length || decl[i] != '[') break;
            // Find matching ']'
            int depth = 0;
            int j = i;
            for (; j < decl.Length; j++)
            {
                if (decl[j] == '[') depth++;
                else if (decl[j] == ']') { depth--; if (depth == 0) { j++; break; } }
            }
            if (j >= decl.Length || depth != 0) break;
            decl = decl.Substring(j).TrimStart();
        }
        return decl;
    }

    private static string StripInitializer(string token)
    {
        int eq = token.IndexOf('=');
        if (eq > 0) token = token.Substring(0, eq);
        return token.Trim();
    }

    private static string StripGenericTail(string name)
    {
        int lt = name.IndexOf('<');
        if (lt > 0) return name.Substring(0, lt);
        return name;
    }
}
