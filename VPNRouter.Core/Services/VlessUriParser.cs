using System.Web;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Parses VLESS share-link URIs into VlessServerEntry objects.
/// Format: vless://UUID@SERVER:PORT?security=reality&amp;sni=HOST&amp;fp=FP&amp;pbk=KEY&amp;sid=SID&amp;type=tcp&amp;flow=FLOW#NAME
/// </summary>
public static class VlessUriParser
{
    /// <summary>Parse a single vless:// URI into a VlessServerEntry.</summary>
    /// <remarks>
    /// v2.32.3 input gate (2026-05-17): after successful structural parse,
    /// the produced entry is routed through <see cref="PlaceholderDefense"/>.
    /// If any field matches a known-bad placeholder fingerprint (pubkey /
    /// short_id / server), the parser throws
    /// <see cref="PlaceholderConfigException"/> instead of returning a
    /// poisoned entry. Foundation for the Z:\kanareik-class incident
    /// fix — F-E catches placeholders at Connect time but the user
    /// can't dial out; rejecting at parse-time prevents the placeholder
    /// from ever reaching subscription cache / settings storage.
    /// </remarks>
    public static VlessServerEntry Parse(string uri)
    {
        uri = uri.Trim();

        if (!uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid VLESS URI: must start with vless://");

        // Strip vless:// prefix and parse as a pseudo-URI
        // Replace vless:// with https:// so Uri class can parse authority + query + fragment
        var fakeUri = "https://" + uri.Substring("vless://".Length);

        if (!Uri.TryCreate(fakeUri, UriKind.Absolute, out var parsed))
            throw new FormatException($"Invalid VLESS URI: cannot parse");

        // UUID is in UserInfo (before @)
        var uuid = Uri.UnescapeDataString(parsed.UserInfo);
        if (string.IsNullOrEmpty(uuid))
            throw new FormatException("Invalid VLESS URI: UUID is missing (expected vless://UUID@server:port)");

        // Server + Port
        var server = NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;

        if (string.IsNullOrEmpty(server))
            throw new FormatException("Invalid VLESS URI: server is missing");

        // Query parameters
        var query = HttpUtility.ParseQueryString(parsed.Query);

        // Fragment = display name
        var name = Uri.UnescapeDataString(parsed.Fragment.TrimStart('#'));

        var entry = new VlessServerEntry
        {
            Name = name,
            Server = server,
            Port = port,
            Uuid = uuid,
            Flow = query["flow"] ?? string.Empty,
            Security = query["security"] ?? "tls",
            OutboundId = query["outbound"] ?? string.Empty,
            DetourVia = query["detour"] ?? string.Empty
        };

        // Transport
        var transportType = query["type"] ?? "tcp";
        entry.Transport = new VlessTransportConfig
        {
            Type = transportType,
            // gRPC uses serviceName, WS uses path, Reality uses spx
            Path = transportType.Equals("grpc", StringComparison.OrdinalIgnoreCase)
                ? query["serviceName"] ?? query["service_name"] ?? ""
                : query["spx"] ?? query["path"] ?? "/"
        };

        var host = query["host"];
        if (transportType.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
        {
            // Runtime gate (mirror awg / naive): the XHTTP transport needs the
            // sing-box-lx fork (with_xhttp). Official builds bundle upstream
            // sing-box, which FATALs on an `xhttp` transport block. Refuse at
            // intake so a hostile / stale subscription line can't brick an
            // official-build tunnel. SubscriptionFetcher catches this per-line
            // (it wraps Parse in try/catch) and drops the entry.
            if (!SingBoxFeatures.XhttpAvailable)
                throw new FormatException(
                    "XHTTP transport requires a sing-box-lx (with_xhttp) build. This build bundles " +
                    "upstream sing-box — use a tcp / ws / grpc VLESS server instead.");

            // XHTTP (sing-box-lx): host is a top-level transport field (not a header);
            // plus mode + x_padding.
            entry.Transport.Mode = query["mode"] ?? string.Empty;
            entry.Transport.XPaddingBytes = query["x_padding_bytes"] ?? query["xpad"] ?? string.Empty;
            entry.Transport.NoGrpcHeader =
                string.Equals(query["no_grpc_header"], "true", StringComparison.OrdinalIgnoreCase)
                || query["no_grpc_header"] == "1";
            if (!string.IsNullOrEmpty(host)) entry.Transport.Host = host;
        }
        else if (!string.IsNullOrEmpty(host))
        {
            entry.Transport.Headers = new Dictionary<string, string> { ["Host"] = host };
        }

        // Reality config (when security=reality)
        if (entry.Security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            entry.Reality = new VlessRealityConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? string.Empty,
                Fingerprint = query["fp"] ?? "firefox",
                PublicKey = query["pbk"] ?? string.Empty,
                ShortId = query["sid"] ?? string.Empty
            };
        }

        // TLS config (when security=tls)
        if (entry.Security.Equals("tls", StringComparison.OrdinalIgnoreCase))
        {
            entry.Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = query["sni"] ?? server,
                Insecure = query["allowInsecure"] == "1",
                Fingerprint = query["fp"] ?? "",
                Alpn = query["alpn"] ?? ""
            };
        }

        // v2.32.3 input gate (2026-05-17) — reject placeholder URLs at
        // parse time. See Z:\kanareik incident: stas's android-port
        // placeholder pubkey "DnT9..." leaked into a real user config via
        // subscription cache, F-E caught it at Connect but the user
        // couldn't dial out. Routing every entry through PlaceholderDefense
        // here means the bad credential never makes it into storage.
        var offendingField = PlaceholderDefense.Inspect(entry);
        if (offendingField != null)
        {
            var offendingValue = offendingField switch
            {
                "reality.public_key" => entry.Reality?.PublicKey ?? string.Empty,
                "reality.short_id"   => entry.Reality?.ShortId ?? string.Empty,
                "server"             => entry.Server,
                _                    => string.Empty,
            };
            throw new PlaceholderConfigException(offendingField, offendingValue);
        }

        return entry;
    }

    /// <summary>
    /// Parse multiple VLESS URIs from text (one per line).
    /// Empty lines and non-vless:// lines are skipped.
    /// </summary>
    public static List<VlessServerEntry> ParseMultiple(string text)
    {
        var entries = new List<VlessServerEntry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(text.AsSpan()))
        {
            var trimmedSpan = lineSpan.Trim();
            if (!trimmedSpan.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                continue;
            var trimmed = trimmedSpan.ToString();
            // Drop-on-throw — match ServerUriParser.ParseMultiple's forgiving
            // contract. A single malformed line, or a fork-only type=xhttp line
            // that now throws on an official build (no with_xhttp), must not
            // abort the whole bulk parse and drop every other server.
            try { entries.Add(Parse(trimmed)); }
            catch (FormatException) { /* skip malformed / fork-unavailable */ }
            catch (PlaceholderConfigException) { /* skip placeholder bait */ }
        }

        return entries;
    }

    /// <summary>Try to parse a VLESS URI, returning null on failure.</summary>
    /// <remarks>
    /// v2.32.3 input gate (2026-05-17): also returns null when the URL
    /// matches a placeholder fingerprint
    /// (<see cref="PlaceholderConfigException"/>). Callers that want to
    /// distinguish "couldn't parse" from "placeholder rejected" should
    /// use <see cref="Parse(string)"/> and catch the typed exception.
    /// In-loop bulk filters (subscription line-walker, ParseMultiple
    /// equivalents) stay clean by relying on the boolean "drop on null"
    /// pattern.
    /// </remarks>
    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch (PlaceholderConfigException) { return null; }
        catch (FormatException) { return null; }
        catch { return null; }
    }

    // ─── Reality field validators (v2.40.0-r9 core-audit Phase A) ─────────────
    // Shared by ConfigGenerator (sanitize short_id so sing-box's hex.Decode can't
    // PANIC) and LeakProtection (fail-closed on a Reality outbound with no usable
    // public_key). NOT wired into Parse() as a hard reject — many test fixtures use
    // placeholder pbk/sid, and a paste-time reject would change that contract.

    /// <summary>True when <paramref name="pbk"/> is a 32-byte x25519 key encoded as
    /// base64url (a usable Reality public_key). Empty / wrong-length / non-base64url
    /// → false (sing-box FATALs "invalid public_key" on those).</summary>
    internal static bool IsValidRealityPublicKey(string? pbk)
        => !string.IsNullOrEmpty(pbk) && TryDecodeBase64Url(pbk!, out var b) && b.Length == 32;

    /// <summary>True when <paramref name="sid"/> is empty (Reality short_id is
    /// optional) OR even-length hex of at most 8 bytes (16 chars). sing-box's
    /// hex.Decode PANICS (index out of range) on a short_id longer than 8 bytes.</summary>
    internal static bool IsValidRealityShortId(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return true;
        if (sid!.Length > 16 || sid.Length % 2 != 0) return false;
        foreach (var c in sid)
            if (!System.Uri.IsHexDigit(c)) return false;
        return true;
    }

    internal static bool TryDecodeBase64Url(string s, out byte[] bytes)
    {
        bytes = System.Array.Empty<byte>();
        try
        {
            var t = s.Replace('-', '+').Replace('_', '/');
            switch (t.Length % 4)
            {
                case 2: t += "=="; break;
                case 3: t += "="; break;
                case 1: return false; // not a valid base64 length
            }
            bytes = System.Convert.FromBase64String(t);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Strips surrounding brackets from IPv6 host literals (e.g. "[2001:db8::1]" -> "2001:db8::1")
    /// so downstream outbound builders don't double-bracket when formatting host:port endpoints.
    /// </summary>
    internal static string NormalizeHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;
        if (host.StartsWith('[') && host.EndsWith(']') && host.Length > 2)
            return host[1..^1];
        return host;
    }
}
