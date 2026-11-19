using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

internal static class DnsttUriParser
{
    internal const string Prefix = "dnstt://";

    internal static VlessServerEntry Parse(string uri)
    {
        if (!SingBoxFeatures.DnsttAvailable)
            throw new FormatException("DNSTT requires a compatible sing-box-vpnctl core.");
        if (uri.Length > 8192)
            throw new FormatException("DNSTT profile is too large.");

        int fragmentStart = uri.IndexOf('#', Prefix.Length);
        var payload = fragmentStart < 0 ? uri[Prefix.Length..] : uri[Prefix.Length..fragmentStart];
        if (payload.Length == 0 || payload.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new FormatException("Invalid DNSTT profile encoding.");
        string base64 = payload.Replace('-', '+').Replace('_', '/') + new string('=', (4 - payload.Length % 4) % 4);
        try
        {
            using var doc = JsonDocument.Parse(Convert.FromBase64String(base64));
            var root = doc.RootElement;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !properties.Add(property.Name)))
                throw new FormatException("Duplicate DNSTT profile field.");
            var id = RequiredString(root, "id", 128);
            var resolver = RequiredString(root, "resolver", 15);
            if (!IPAddress.TryParse(resolver, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
                || ip.GetAddressBytes()[0] is 0 or >= 224 || !string.Equals(ip.ToString(), resolver, StringComparison.Ordinal))
                throw new FormatException("Invalid DNSTT resolver address.");
            if (!root.TryGetProperty("port", out var port) || !port.TryGetInt32(out var udpPort) || udpPort != 53)
                throw new FormatException("Invalid DNSTT resolver port.");

            var name = fragmentStart < 0 ? $"{id} DNSTT" : Uri.UnescapeDataString(uri[(fragmentStart + 1)..]);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
                throw new FormatException("Invalid DNSTT profile label.");

            var domain = RequiredString(root, "domain", 100);
            if (!domain.Contains('.') || !domain.Split('.').All(label => label.Length is > 0 and <= 63
                && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new FormatException("Invalid DNSTT DNS zone.");
            var noise = RequiredString(root, "noise_key", 64);
            if (noise.Length != 64 || !noise.All(char.IsAsciiHexDigit))
                throw new FormatException("Invalid DNSTT server key.");

            var sshHost = RequiredString(root, "ssh_host_key", 192);
            var parts = sshHost.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "ssh-ed25519" || !IsEd25519Blob(Convert.FromBase64String(parts[1])))
                throw new FormatException("Invalid DNSTT SSH host key.");
            var sshPrivate = RequiredString(root, "ssh_private_key", 2048);
            if (!IsOpenSshPrivateKey(sshPrivate))
                throw new FormatException("Invalid DNSTT SSH client key.");

            return new VlessServerEntry
            {
                Name = name, Protocol = "dnstt", Security = "none", Server = resolver, Port = udpPort, OutboundId = "dnstt:" + id,
                UseAsSingleOutbound = true,
                Dnstt = new DnsttProfile { Domain = domain, NoiseKey = noise,
                    SshHostKey = $"{parts[0]} {parts[1]}", SshPrivateKey = sshPrivate },
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new FormatException("Invalid DNSTT profile payload.");
        }
    }

    private static bool IsEd25519Blob(ReadOnlySpan<byte> bytes) => bytes.Length == 51 &&
        bytes[..15].SequenceEqual("\0\0\0\vssh-ed25519"u8) &&
        bytes.Slice(15, 4).SequenceEqual("\0\0\0 "u8);

    private static bool IsOpenSshPrivateKey(string key)
    {
        if (!PemEncoding.TryFind(key, out var pem) || !key.AsSpan()[pem.Label].SequenceEqual("OPENSSH PRIVATE KEY"))
            return false;
        var bytes = new byte[pem.DecodedDataLength];
        try
        {
            return Convert.TryFromBase64Chars(key.AsSpan()[pem.Base64Data], bytes, out var written) &&
                IsOpenSshPrivateBlob(bytes.AsSpan(0, written));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsOpenSshPrivateBlob(ReadOnlySpan<byte> blob)
    {
        var magic = "openssh-key-v1\0"u8;
        if (!blob.StartsWith(magic)) return false;
        blob = blob[magic.Length..];
        if (!TakeField(ref blob, out var cipher) || !cipher.SequenceEqual("none"u8) ||
            !TakeField(ref blob, out var kdf) || !kdf.SequenceEqual("none"u8) ||
            !TakeField(ref blob, out var kdfOptions) || !kdfOptions.IsEmpty ||
            blob.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(blob) != 1)
            return false;
        blob = blob[4..];
        return TakeField(ref blob, out var publicKey) && IsEd25519Blob(publicKey) &&
            TakeField(ref blob, out var privateKey) && privateKey.Length >= 123 && blob.IsEmpty;
    }

    private static bool TakeField(ref ReadOnlySpan<byte> data, out ReadOnlySpan<byte> field)
    {
        field = default;
        if (data.Length < 4) return false;
        var length = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (length > data.Length - 4) return false;
        field = data.Slice(4, (int)length);
        data = data[(4 + (int)length)..];
        return true;
    }

    private static string RequiredString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String
            || field.GetString() is not { Length: > 0 } value || value.Length > maxLength)
            throw new FormatException($"Missing or invalid DNSTT field: {name}.");
        return value;
    }
}
