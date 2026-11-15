using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal sealed class BusinessPolicyConfiguration
{
    internal string PolicyId { get; }
    internal Uri Endpoint { get; }
    internal string ReadToken { get; }
    internal IReadOnlyDictionary<string, string> Keys { get; }
    internal string TrustId { get; }

    private BusinessPolicyConfiguration(string policyId, Uri endpoint, string readToken,
        Dictionary<string, string> keys)
    {
        PolicyId = policyId;
        Endpoint = endpoint;
        ReadToken = readToken;
        Keys = new ReadOnlyDictionary<string, string>(keys);
        using var bytes = new MemoryStream();
        AppendIdentity(bytes, policyId);
        AppendIdentity(bytes, endpoint.AbsoluteUri);
        TrustId = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    // Optional capability errors must not invalidate the employee's VPN bundle.
    internal static BusinessPolicyConfiguration? ParseOptional(JsonElement business)
    {
        try
        {
            if (business.ValueKind != JsonValueKind.Object) return null;
            var fields = business.EnumerateObject().Where(p => p.NameEquals("policy")).ToArray();
            if (fields.Length != 1) return null;
            var policy = fields[0].Value;
            if (!HasFields(policy, "policy_id", "endpoint", "read_token", "keys")) return null;
            var policyId = String(policy, "policy_id");
            var endpointText = String(policy, "endpoint");
            var token = String(policy, "read_token");
            if (policyId == null || !BusinessPolicy.IsIdentifier(policyId) ||
                endpointText is not { Length: > 0 and <= 4096 } || !IsToken(token) ||
                !Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 ||
                endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                !BusinessNodeSnapshot.TryNormalizeHost(endpoint.IdnHost, out _)) return null;
            endpoint = new UriBuilder(endpoint) { Host = endpoint.IdnHost.ToLowerInvariant() }.Uri;

            var entries = policy.GetProperty("keys");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 4)
                return null;
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (!HasFields(entry, "id", "spki")) return null;
                var id = String(entry, "id");
                var spki = String(entry, "spki");
                if (id == null || !BusinessPolicy.IsIdentifier(id) ||
                    spki is not { Length: > 0 and <= 512 } ||
                    !BusinessPolicy.ValidateKey(spki) || !keys.TryAdd(id, spki)) return null;
            }
            return new(policyId, endpoint, token!, keys);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or
            FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static bool HasFields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(expected, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Remove(property.Name)) return false;
        return names.Count == 0;
    }

    private static string? String(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString() : null;

    private static bool IsToken(string? token)
    {
        if (token is not { Length: >= 32 and <= 512 }) return false;
        var end = token.Length;
        while (end > 0 && token[end - 1] == '=') end--;
        return end > 0 && token.AsSpan(0, end).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/".AsSpan()) < 0;
    }

    private static void AppendIdentity(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
