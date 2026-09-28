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

internal static class AndroidAppSourceSurfaceHashHelper
{
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

    public static string Compute(string androidProjectDir)
    {
        var descriptions = EnumerateMembers(androidProjectDir)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var json = JsonSerializer.Serialize(descriptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hashBytes);
    }

    public static string[] DumpMembers(string androidProjectDir)
    {
        return EnumerateMembers(androidProjectDir)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateMembers(string androidProjectDir)
    {
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

    private static string StripCommentsAndStrings(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                continue;
            }
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

    private static IEnumerable<string> ExtractMembersFromPartialClass(string normalized)
    {
        var classOpenRegex = new Regex(
            @"\bpartial\s+class\s+AndroidApp\b[^{]*\{",
            RegexOptions.Compiled);

        var matches = classOpenRegex.Matches(normalized);
        foreach (Match m in matches)
        {
            int bodyStart = m.Index + m.Length;
            int bodyEnd = FindMatchingBrace(normalized, m.Index + m.Length - 1);
            if (bodyEnd < 0) continue;

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

    private static IEnumerable<string> ExtractDeclarations(string body)
    {
        var results = new List<string>();

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
                    var decl = current.ToString().Trim();
                    if (!string.IsNullOrEmpty(decl))
                    {
                        var member = ClassifyBlockDecl(decl);
                        if (member is not null) results.Add(member);
                    }
                    current.Clear();
                    depth++;
                    i++;
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
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            current.Append(c);
            i++;
        }

        return results;
    }

    private static string? ClassifySemicolonDecl(string decl)
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

        if (tokens[idx] == "event" && idx + 2 < tokens.Count)
        {
            var typeStr = tokens[idx + 1];
            var nameStr = StripInitializer(tokens[idx + 2]);
            return $"E:{nameStr}:{typeStr}";
        }

        if (idx + 1 < tokens.Count)
        {
            var typeStr = tokens[idx];
            var nameStr = StripInitializer(tokens[idx + 1]);
            if (decl.Contains("=>"))
            {
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

        if (tokens[idx] == "enum" || tokens[idx] == "class" || tokens[idx] == "struct"
            || tokens[idx] == "interface" || tokens[idx] == "record")
        {
            if (idx + 1 < tokens.Count)
            {
                var nameStr = StripGenericTail(tokens[idx + 1]);
                return $"T:{nameStr}:{tokens[idx]}";
            }
            return null;
        }

        var rawAfterModifiers = string.Join(" ", tokens.Skip(idx));
        bool hasParen = rawAfterModifiers.Contains('(');
        if (hasParen)
        {
            int parenAt = rawAfterModifiers.IndexOf('(');
            var head = rawAfterModifiers.Substring(0, parenAt).Trim();
            var headTokens = SplitTokens(head);
            if (headTokens.Count < 1) return null;
            string returnType, name;
            if (headTokens.Count == 1)
            {
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

        var parts = SplitTopLevelCommas(inner);
        var sb = new StringBuilder();
        for (int k = 0; k < parts.Count; k++)
        {
            if (k > 0) sb.Append(',');
            var trimmed = parts[k].Trim();
            trimmed = Regex.Replace(trimmed, @"^\[[^\]]*\]\s*", "");
            int eq = trimmed.IndexOf('=');
            if (eq > 0) trimmed = trimmed.Substring(0, eq).Trim();
            var pTokens = SplitTokens(trimmed);
            var paramMods = new HashSet<string>(StringComparer.Ordinal)
            {
                "ref", "out", "in", "this", "params", "scoped"
            };
            int p = 0;
            while (p < pTokens.Count && paramMods.Contains(pTokens[p])) p++;
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
