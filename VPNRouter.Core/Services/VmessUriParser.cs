using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

// Standard V2Ray vmess:// links carry one padded base64 JSON object. This parser
// deliberately accepts only the AEAD subset (alter_id 0) and the transports the
// generator can render without dropping dial or TLS options.
public static class VmessUriParser
{
    public const string Prefix = "vmess://";

    public static VlessServerEntry Parse(string uri)
    {
        uri = (uri ?? string.Empty).Trim();
        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Invalid VMess URI: must start with vmess://");

        var body = uri.Substring(Prefix.Length);
        string? fragmentName = null;
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            fragmentName = Uri.UnescapeDataString(body.Substring(hash + 1));
            body = body.Substring(0, hash);
        }

        body = body.Trim();
        if (body.Length == 0)
            throw new FormatException("Invalid VMess URI: empty payload");
        if (body.Length > 64 * 1024)
            throw new FormatException("Invalid VMess URI: payload is too large");

        var payload = DecodePayload(body);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return ParsePayload(doc.RootElement, fragmentName);
        }
        catch (JsonException)
        {
            throw new FormatException("Invalid VMess URI: payload is not valid JSON");
        }
    }

    private static VlessServerEntry ParsePayload(JsonElement root, string? fragmentName)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Invalid VMess URI: payload JSON must be an object");

        var version = ReadText(root, "v")?.Trim();
        if (version is not null && version != "2")
            throw new FormatException("Invalid VMess URI: only payload version 2 is supported");

        var server = VlessUriParser.NormalizeHost((ReadString(root, "add") ?? string.Empty).Trim());
        if (Uri.CheckHostName(server) == UriHostNameType.Unknown)
            throw new FormatException("Invalid VMess URI: add is missing or invalid");

        var port = ReadInt(root, "port");
        if (port is null || port <= 0 || port > 65535)
            throw new FormatException("Invalid VMess URI: port is missing or invalid");

        var uuid = ReadString(root, "id")?.Trim();
        if (!Guid.TryParse(uuid, out _))
            throw new FormatException("Invalid VMess URI: id must be a UUID");

        if (root.TryGetProperty("aid", out _) && ReadInt(root, "aid") != 0)
            throw new FormatException("Invalid VMess URI: legacy alter_id is not supported (AEAD requires aid 0)");

        if (!TryNormalizeAeadSecurity(ReadString(root, "scy"), out var security))
            throw new FormatException("Invalid VMess URI: only AEAD ciphers are supported");

        var transportType = (ReadString(root, "net") ?? "tcp").Trim().ToLowerInvariant();
        if (transportType.Length == 0) transportType = "tcp";
        if (transportType != "tcp" && transportType != "ws")
            throw new FormatException("Invalid VMess URI: unsupported transport (expected tcp or ws)");

        var headerType = (ReadString(root, "type") ?? "none").Trim().ToLowerInvariant();
        if (headerType.Length > 0 && headerType != "none")
            throw new FormatException("Invalid VMess URI: unsupported header type (expected none)");

        var tlsValue = (ReadString(root, "tls") ?? string.Empty).Trim().ToLowerInvariant();
        if (tlsValue.Length > 0 && tlsValue != "tls" && tlsValue != "none")
            throw new FormatException("Invalid VMess URI: unsupported tls value (expected tls or none)");
        var tlsEnabled = tlsValue == "tls";

        var host = ReadString(root, "host") ?? string.Empty;
        var sni = tlsEnabled ? (ReadString(root, "sni") ?? string.Empty) : string.Empty;
        if (tlsEnabled && string.IsNullOrWhiteSpace(sni))
            sni = !string.IsNullOrWhiteSpace(host) ? host : server;

        var name = ReadString(root, "ps");
        if (string.IsNullOrWhiteSpace(name)) name = fragmentName;
        if (string.IsNullOrWhiteSpace(name)) name = $"vmess-{server}-{port.Value}";

        var entry = new VlessServerEntry
        {
            Name = name!,
            Protocol = "vmess",
            Server = server,
            Port = port.Value,
            Uuid = uuid!,
            Security = tlsEnabled ? "tls" : "none",
            VmessSecurity = security,
            AlterId = 0,
            OutboundId = ReadId(root, "outbound"),
            DetourVia = ReadId(root, "detour"),
            Tls = new VlessTlsConfig
            {
                Enabled = tlsEnabled,
                ServerName = sni,
                Insecure = tlsEnabled && (ReadBool(root, "allowInsecure") ?? ReadBool(root, "insecure") ?? false),
                Fingerprint = ReadString(root, "fp") ?? string.Empty,
                Alpn = ReadString(root, "alpn") ?? string.Empty,
            },
            Transport = new VlessTransportConfig
            {
                Type = transportType,
                Path = transportType == "ws"
                    ? (ReadString(root, "path") is { Length: > 0 } path ? path : "/")
                    : string.Empty,
                Headers = transportType == "ws" && !string.IsNullOrWhiteSpace(host)
                    ? new Dictionary<string, string> { ["Host"] = host }
                    : new Dictionary<string, string>(),
            },
        };

        var offendingField = PlaceholderDefense.Inspect(entry);
        if (offendingField != null)
        {
            var offendingValue = offendingField switch
            {
                "reality.public_key" => entry.Reality?.PublicKey ?? string.Empty,
                "reality.short_id" => entry.Reality?.ShortId ?? string.Empty,
                "server" => entry.Server,
                _ => string.Empty,
            };
            throw new PlaceholderConfigException(offendingField, offendingValue);
        }

        return entry;
    }

    public static VlessServerEntry? TryParse(string uri)
    {
        try { return Parse(uri); }
        catch (PlaceholderConfigException) { return null; }
        catch (FormatException) { return null; }
        catch { return null; }
    }

    public static List<VlessServerEntry> ParseMultiple(string text)
    {
        var entries = new List<VlessServerEntry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        foreach (var lineSpan in MemoryExtensions.EnumerateLines(text.AsSpan()))
        {
            var trimmedSpan = lineSpan.Trim();
            if (!trimmedSpan.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            try { entries.Add(Parse(trimmedSpan.ToString())); }
            catch (FormatException) { }
            catch (PlaceholderConfigException) { }
        }

        return entries;
    }

    internal static bool TryNormalizeAeadSecurity(string? raw, out string normalized)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (value)
        {
            case "":
            case "auto":
            case "aes-128-gcm":
                normalized = "aes-128-gcm";
                return true;
            case "chacha20-poly1305":
                normalized = "chacha20-poly1305";
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }

    internal static string BuildShareUri(VlessServerEntry entry)
    {
        if (!TryNormalizeAeadSecurity(entry.VmessSecurity, out var security))
            throw new FormatException("VMess requires an AEAD cipher");
        if (entry.AlterId != 0)
            throw new FormatException("VMess requires AEAD alter_id 0");

        var transportType = (entry.Transport?.Type ?? "tcp").Trim().ToLowerInvariant();
        if (transportType.Length == 0) transportType = "tcp";
        if (transportType != "tcp" && transportType != "ws")
            throw new FormatException("VMess share links support tcp and ws transports only");

        var tlsEnabled = entry.Tls?.Enabled == true
            || string.Equals(entry.Security, "tls", StringComparison.OrdinalIgnoreCase);
        var host = HostHeader(entry);
        var sni = entry.Tls?.ServerName ?? string.Empty;
        if (tlsEnabled && string.IsNullOrWhiteSpace(sni))
            sni = !string.IsNullOrWhiteSpace(host) ? host : entry.Server;

        var json = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = entry.Name ?? string.Empty,
            ["add"] = entry.Server,
            ["port"] = entry.Port.ToString(CultureInfo.InvariantCulture),
            ["id"] = entry.Uuid,
            ["aid"] = "0",
            ["scy"] = security,
            ["net"] = transportType,
            ["type"] = "none",
        };

        if (!string.IsNullOrWhiteSpace(host))
            json["host"] = host;
        if (transportType == "ws")
        {
            var path = entry.Transport?.Path;
            json["path"] = string.IsNullOrEmpty(path) ? "/" : path;
        }
        json["tls"] = tlsEnabled ? "tls" : string.Empty;
        if (tlsEnabled && !string.IsNullOrWhiteSpace(sni))
            json["sni"] = sni;
        if (!string.IsNullOrWhiteSpace(entry.Tls?.Alpn))
            json["alpn"] = entry.Tls.Alpn;
        if (!string.IsNullOrWhiteSpace(entry.Tls?.Fingerprint))
            json["fp"] = entry.Tls.Fingerprint;
        if (tlsEnabled && entry.Tls?.Insecure == true)
            json["allowInsecure"] = "1";
        if (!string.IsNullOrEmpty(entry.OutboundId))
            json["outbound"] = entry.OutboundId;
        if (!string.IsNullOrEmpty(entry.DetourVia))
            json["detour"] = entry.DetourVia;

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));
        return Prefix + payload + "#" + Uri.EscapeDataString(entry.Name ?? string.Empty);
    }

    internal static string AddChainMetadata(string uri, string outboundId, string? detourId)
    {
        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Invalid VMess URI: must start with vmess://");

        var body = uri.Substring(Prefix.Length);
        var fragment = string.Empty;
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            fragment = body.Substring(hash);
            body = body.Substring(0, hash);
        }

        var payload = DecodePayload(body.Trim());
        var node = JsonNode.Parse(payload);
        if (node is not JsonObject obj)
            throw new FormatException("Invalid VMess URI: payload JSON must be an object");

        obj["outbound"] = outboundId;
        if (!string.IsNullOrEmpty(detourId))
            obj["detour"] = detourId;
        else
            obj.Remove("detour");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(obj.ToJsonString()));
        return Prefix + encoded + fragment;
    }

    internal static string HostHeader(VlessServerEntry entry)
    {
        if (entry.Transport?.Headers is { Count: > 0 } headers)
        {
            foreach (var pair in headers)
                if (pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value;
        }
        return entry.Transport?.Host ?? string.Empty;
    }

    private static byte[] DecodePayload(string body)
    {
        var normalized = body.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
            case 1: throw new FormatException("Invalid VMess URI: payload is not valid base64");
        }

        try { return Convert.FromBase64String(normalized); }
        catch (FormatException) { throw new FormatException("Invalid VMess URI: payload is not valid base64"); }
    }

    private static string ReadId(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("Invalid VMess URI: chain IDs must be strings");
        return value.GetString() ?? string.Empty;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is 0 or 1)
            return number == 1;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return null;
    }
}
