using System.Net;
using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class TurnRelayUriParser
{
    internal const string Prefix = "turnrelay://";
    private const int MaxPayloadChars = 8192;
    private const int MaxDecodedBytes = 8192;

    internal static VlessServerEntry Parse(string uri)
    {
        if (!SingBoxFeatures.TurnRelayAvailable)
            throw new FormatException("TurnRelay requires a sing-box-vpnctl core with with_turnrelay.");
        if (uri.Length > MaxPayloadChars + 2048)
            throw new FormatException("TurnRelay profile is too large.");

        int fragmentStart = uri.IndexOf('#', Prefix.Length);
        var payload = fragmentStart < 0 ? uri[Prefix.Length..] : uri[Prefix.Length..fragmentStart];
        if (payload.Length == 0 || payload.Length > MaxPayloadChars ||
            payload.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new FormatException("Invalid TurnRelay profile encoding.");

        byte[] jsonBytes;
        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - payload.Length % 4) % 4);
            jsonBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new FormatException("Invalid TurnRelay profile encoding.");
        }

        try
        {
            if (jsonBytes.Length > MaxDecodedBytes)
                throw new FormatException("TurnRelay profile is too large.");

            using var document = JsonDocument.Parse(jsonBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("TurnRelay profile JSON must be an object.");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !keys.Add(property.Name)))
                throw new FormatException("Duplicate TurnRelay profile field.");

            var id = RequiredString(root, "id", 64);
            if (!IsValidId(id))
                throw new FormatException("Invalid TurnRelay outbound id.");

            var provider = ReadString(root, "provider", 16);
            if (provider.Length != 0 && provider != TurnRelayConfig.DefaultProvider)
                throw new FormatException("Invalid TurnRelay provider.");

            var serverType = ReadString(root, "server_type", 16);
            if (serverType.Length != 0 && serverType != TurnRelayConfig.DefaultServerType)
                throw new FormatException("Invalid TurnRelay server type.");

            var server = RequiredString(root, "server", 64);
            if (!TurnRelayConfig.IsLiteralIp(server))
                throw new FormatException("Invalid TurnRelay server address.");
            var normalizedServer = IPAddress.Parse(server).ToString();

            if (!TryReadInt(root, "server_port", out var port) || port is < 1 or > 65535)
                throw new FormatException("Invalid TurnRelay server port.");

            var turnServer = RequiredString(root, "turn_server", 255);
            if (!TurnRelayUriParserRules.IsValidHostPort(turnServer))
                throw new FormatException("Invalid TurnRelay TURN server.");

            var turnUsername = RequiredString(root, "turn_username", 512);
            var turnPassword = RequiredString(root, "turn_password", 512);
            var password = RequiredString(root, "password", 512);

            var fingerprint = RequiredString(root, "server_fingerprint", 64);
            if (!TurnRelayConfig.IsFingerprint(fingerprint))
                throw new FormatException("Invalid TurnRelay server fingerprint.");
            fingerprint = fingerprint.ToLowerInvariant();

            var connections = TurnRelayConfig.DefaultConnections;
            if (root.TryGetProperty("connections", out var connectionsNode))
            {
                if (connectionsNode.ValueKind != JsonValueKind.Number ||
                    !connectionsNode.TryGetInt32(out connections))
                    throw new FormatException("Invalid TurnRelay connections value.");
                if (connections == 0)
                    connections = TurnRelayConfig.DefaultConnections;
            }
            if (connections is < 1 or > 60)
                throw new FormatException("Invalid TurnRelay connections value.");

            var mode = ReadString(root, "mode", 16);
            if (mode.Length == 0)
                mode = TurnRelayConfig.DefaultMode;
            if (mode is not (TurnRelayConfig.DefaultMode or "dtls"))
                throw new FormatException("Invalid TurnRelay mode.");

            var udp = true;
            if (root.TryGetProperty("udp", out var udpNode))
            {
                if (udpNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("Invalid TurnRelay UDP flag.");
                udp = udpNode.GetBoolean();
            }

            string name;
            try
            {
                name = fragmentStart < 0 ? id : Uri.UnescapeDataString(uri[(fragmentStart + 1)..]);
            }
            catch (UriFormatException)
            {
                throw new FormatException("Invalid TurnRelay profile label.");
            }
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
                throw new FormatException("Invalid TurnRelay profile label.");

            return new VlessServerEntry
            {
                Name = name,
                Protocol = "turnrelay",
                Security = "none",
                Server = normalizedServer,
                Port = port,
                OutboundId = id,
                TurnRelay = new TurnRelayConfig
                {
                    Provider = provider.Length == 0 ? TurnRelayConfig.DefaultProvider : provider,
                    ServerType = serverType.Length == 0 ? TurnRelayConfig.DefaultServerType : serverType,
                    TurnServer = turnServer,
                    TurnUsername = turnUsername,
                    TurnPassword = turnPassword,
                    Password = password,
                    ServerFingerprint = fingerprint,
                    Connections = connections,
                    Mode = mode,
                    Udp = udp,
                },
            };
        }
        catch (JsonException)
        {
            throw new FormatException("Invalid TurnRelay profile payload.");
        }
        finally
        {
            Array.Clear(jsonBytes);
        }
    }

    internal static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return false;
        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '-'))
                return false;
        }
        return true;
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        var value = ReadString(root, name, maxLength);
        if (value.Length == 0)
            throw new FormatException($"Missing or invalid TurnRelay field: {name}.");
        return value;
    }

    private static string ReadString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var field))
            return string.Empty;
        if (field.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (field.ValueKind != JsonValueKind.String)
            throw new FormatException($"Invalid TurnRelay field: {name}.");
        var value = field.GetString() ?? string.Empty;
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw new FormatException($"Invalid TurnRelay field: {name}.");
        return value;
    }

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var field))
            return false;
        return field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }
}
