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
        var server = parsed.Host;
        var port = parsed.Port > 0 ? parsed.Port : 443;

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
            Security = query["security"] ?? "tls"
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
        if (!string.IsNullOrEmpty(host))
            entry.Transport.Headers = new Dictionary<string, string> { ["Host"] = host };

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

        return entry;
    }

    /// <summary>
    /// Parse multiple VLESS URIs from text (one per line).
    /// Empty lines and non-vless:// lines are skipped.
    /// </summary>
    public static List<VlessServerEntry> ParseMultiple(string text)
    {
        var entries = new List<VlessServerEntry>();

        foreach (var line in text.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                entries.Add(Parse(trimmed));
        }

        return entries;
    }

    /// <summary>Try to parse a VLESS URI, returning null on failure.</summary>
    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch { return null; }
    }
}
