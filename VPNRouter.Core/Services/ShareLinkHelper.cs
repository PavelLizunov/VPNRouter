using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Services;

/// <summary>
/// High-performance span-based URI parser for multi-protocol VPN share links.
/// Eliminates intermediate string concatenations, System.Uri instantiation,
/// and legacy NameValueCollection allocations across all protocols.
/// </summary>
internal static class ShareLinkHelper
{
    public readonly struct QueryDictionary
    {
        private readonly Dictionary<string, string>? _dict;

        public QueryDictionary(Dictionary<string, string> dict) => _dict = dict;

        public string? this[string key] =>
            _dict != null && _dict.TryGetValue(key, out var val) ? val : null;
    }

    public static void ParseComponents(
        ReadOnlySpan<char> span,
        out ReadOnlySpan<char> userinfo,
        out string host,
        out int port,
        out ReadOnlySpan<char> query,
        out string name)
    {
        // 1. Fragment (#name)
        int hashIdx = span.IndexOf('#');
        if (hashIdx >= 0)
        {
            name = Unescape(span.Slice(hashIdx + 1));
            span = span.Slice(0, hashIdx);
        }
        else
        {
            name = string.Empty;
        }

        // 2. Query (?params)
        int qIdx = span.IndexOf('?');
        if (qIdx >= 0)
        {
            query = span.Slice(qIdx + 1);
            span = span.Slice(0, qIdx);
        }
        else
        {
            query = default;
        }

        // 3. Strip trailing '/' if present before query/fragment
        if (span.EndsWith("/"))
        {
            span = span.Slice(0, span.Length - 1);
        }

        // 4. UserInfo (before @)
        int atIdx = span.LastIndexOf('@');
        if (atIdx >= 0)
        {
            userinfo = span.Slice(0, atIdx);
            span = span.Slice(atIdx + 1);
        }
        else
        {
            userinfo = default;
        }

        // 5. Host and Port
        port = 443;
        if (span.StartsWith("["))
        {
            int closeBracket = span.IndexOf(']');
            if (closeBracket < 0)
                throw new FormatException("Invalid URI: cannot parse IPv6 host");
            host = span.Slice(1, closeBracket - 1).ToString();
            span = span.Slice(closeBracket + 1);
            if (span.StartsWith(":"))
            {
                port = ParsePort(span.Slice(1));
            }
        }
        else
        {
            int colonIdx = span.LastIndexOf(':');
            if (colonIdx >= 0)
            {
                host = span.Slice(0, colonIdx).ToString();
                port = ParsePort(span.Slice(colonIdx + 1));
            }
            else
            {
                host = span.ToString();
            }
        }

        host = VlessUriParser.NormalizeHost(host);
    }

    private static int ParsePort(ReadOnlySpan<char> portSpan)
    {
        if (portSpan.IsEmpty) return 443;
        if (!int.TryParse(portSpan, out var p) || p < 0 || p > 65535)
            throw new FormatException($"Invalid URI: invalid port '{portSpan.ToString()}'");
        return p == 0 ? 443 : p;
    }

    public static QueryDictionary ParseQuery(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return default;

        var dict = new Dictionary<string, string>(8, StringComparer.OrdinalIgnoreCase);

        while (!span.IsEmpty)
        {
            int ampIdx = span.IndexOf('&');
            var pair = ampIdx >= 0 ? span.Slice(0, ampIdx) : span;
            span = ampIdx >= 0 ? span.Slice(ampIdx + 1) : default;

            if (pair.IsEmpty) continue;

            int eqIdx = pair.IndexOf('=');
            string key;
            string val;
            if (eqIdx >= 0)
            {
                key = Unescape(pair.Slice(0, eqIdx));
                val = Unescape(pair.Slice(eqIdx + 1));
            }
            else
            {
                key = Unescape(pair);
                val = string.Empty;
            }

            if (key.Length > 0)
            {
                dict[key] = val;
            }
        }

        return new QueryDictionary(dict);
    }

    public static string Unescape(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return string.Empty;
        if (span.IndexOf('%') < 0) return span.ToString();
        return Uri.UnescapeDataString(span.ToString());
    }
}
