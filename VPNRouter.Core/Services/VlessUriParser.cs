using System.Web;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class VlessUriParser
{
    public static VlessServerEntry Parse(string uri)
    {
        uri = uri.Trim();

        if (!uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid VLESS URI: must start with vless://");

        var fakeUri = "https://" + uri.Substring("vless://".Length);

        if (!Uri.TryCreate(fakeUri, UriKind.Absolute, out var parsed))
            throw new FormatException($"Invalid VLESS URI: cannot parse");

        var uuid = Uri.UnescapeDataString(parsed.UserInfo);
        if (string.IsNullOrEmpty(uuid))
            throw new FormatException("Invalid VLESS URI: UUID is missing (expected vless://UUID@server:port)");

        var server = NormalizeHost(parsed.Host);
        var port = (parsed.Port > 0 && parsed.Port <= 65535) ? parsed.Port : 443;

        if (string.IsNullOrEmpty(server))
            throw new FormatException("Invalid VLESS URI: server is missing");

        var query = HttpUtility.ParseQueryString(parsed.Query);

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

        var transportType = query["type"] ?? "tcp";
        entry.Transport = new VlessTransportConfig
        {
            Type = transportType,
            Path = transportType.Equals("grpc", StringComparison.OrdinalIgnoreCase)
                ? query["serviceName"] ?? query["service_name"] ?? ""
                : query["spx"] ?? query["path"] ?? "/"
        };

        var host = query["host"];
        if (transportType.Equals("xhttp", StringComparison.OrdinalIgnoreCase))
        {
            if (!SingBoxFeatures.XhttpAvailable)
                throw new FormatException(
                    "XHTTP transport requires a sing-box-lx (with_xhttp) build. This build bundles " +
                    "upstream sing-box — use a tcp / ws / grpc VLESS server instead.");

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
            try { entries.Add(Parse(trimmed)); }
            catch (FormatException) { }
            catch (PlaceholderConfigException) { }
        }

        return entries;
    }

    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch (PlaceholderConfigException) { return null; }
        catch (FormatException) { return null; }
        catch { return null; }
    }

    internal static bool IsValidRealityPublicKey(string? pbk)
        => !string.IsNullOrEmpty(pbk) && TryDecodeBase64Url(pbk!, out var b) && b.Length == 32;

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
                case 1: return false;
            }
            bytes = System.Convert.FromBase64String(t);
            return true;
        }
        catch { return false; }
    }

    internal static string NormalizeHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;
        if (host.StartsWith('[') && host.EndsWith(']') && host.Length > 2)
            return host[1..^1];
        return host;
    }
}
