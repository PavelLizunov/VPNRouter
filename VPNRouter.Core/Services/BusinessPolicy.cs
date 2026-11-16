using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal sealed record BusinessPolicyRule(string Domain, string Action, string Comment);

internal sealed class BusinessPolicySnapshot
{
    internal string PolicyId { get; }
    internal string KeyId { get; }
    internal long Revision { get; }
    internal long PublishedAt { get; }
    internal bool ApplyNow { get; }
    internal IReadOnlyList<BusinessPolicyRule> Rules { get; }
    internal string PayloadDigest { get; }
    internal BusinessPhonePolicySnapshot? Phone { get; }
    internal string EffectiveDigest { get; }

    internal BusinessPolicySnapshot(string policyId, string keyId, long revision, long publishedAt,
        bool applyNow, IEnumerable<BusinessPolicyRule> rules, string payloadDigest,
        BusinessPhonePolicySnapshot? phone = null)
    {
        PolicyId = policyId;
        KeyId = keyId;
        Revision = revision;
        PublishedAt = publishedAt;
        ApplyNow = applyNow;
        Rules = Array.AsReadOnly(rules.ToArray());
        PayloadDigest = payloadDigest;
        if (phone != null && phone.PolicyId != policyId)
            throw new InvalidDataException("Invalid Business policy.");
        Phone = phone;
        EffectiveDigest = phone == null ? payloadDigest : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"telephony-v1:{payloadDigest}:{phone.PayloadDigest}")));
    }

    internal BusinessPolicySnapshot WithPhone(BusinessPhonePolicySnapshot? phone, bool? applyNowOverride = null) =>
        new(PolicyId, KeyId, Revision, PublishedAt, applyNowOverride ?? ApplyNow, Rules, PayloadDigest, phone);
}

internal static class BusinessPolicy
{
    internal const int MaximumResponseBytes = 65536;
    internal const int MaximumPayloadBytes = 49152;
    internal const long MaximumRevision = 9007199254740991;
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static BusinessPolicySnapshot Parse(ReadOnlyMemory<byte> envelope,
        string expectedPolicyId, IReadOnlyDictionary<string, string> keys)
        => ParseSigned(envelope, expectedPolicyId, keys,
            ["schema", "policy_id", "key_id", "revision", "published_at", "apply", "rules"],
            (data, metadata) =>
            {
                var list = data["rules"];
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 512) throw Invalid();
                var rules = new List<BusinessPolicyRule>(list.GetArrayLength());
                foreach (var item in list.EnumerateArray())
                {
                    var rule = Object(item, "domain", "action", "comment");
                    rules.Add(new(String(rule["domain"]), String(rule["action"]), String(rule["comment"])));
                }
                BusinessPolicyDomains.ValidateRules(rules);
                return new BusinessPolicySnapshot(metadata.PolicyId, metadata.KeyId, metadata.Revision,
                    metadata.PublishedAt, metadata.ApplyNow, rules, metadata.PayloadDigest);
            });

    internal sealed record Metadata(string PolicyId, string KeyId, long Revision, long PublishedAt,
        bool ApplyNow, string PayloadDigest);

    internal static T ParseSigned<T>(ReadOnlyMemory<byte> envelope, string expectedPolicyId,
        IReadOnlyDictionary<string, string> keys, string[] fields,
        Func<Dictionary<string, JsonElement>, Metadata, T> parse)
    {
        try
        {
            if (envelope.Length is 0 or > MaximumResponseBytes || !IsIdentifier(expectedPolicyId))
                throw Invalid();
            _ = StrictUtf8.GetCharCount(envelope.Span);
            using var outer = JsonDocument.Parse(envelope, new JsonDocumentOptions { MaxDepth = 8 });
            var outerFields = Object(outer.RootElement, "payload", "signature");
            var payload = Base64(String(outerFields["payload"]), MaximumPayloadBytes);
            var signature = Base64(String(outerFields["signature"]), 64);
            if (signature.Length != 64 || payload.Length == 0) throw Invalid();
            _ = StrictUtf8.GetCharCount(payload);
            using var inner = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var data = Object(inner.RootElement, fields);
            if (Integer(data["schema"], 1) != 1) throw Invalid();
            var policyId = String(data["policy_id"]);
            var keyId = String(data["key_id"]);
            if (!IsIdentifier(policyId) || policyId != expectedPolicyId || !IsIdentifier(keyId) ||
                !keys.Keys.Contains(keyId, StringComparer.Ordinal) ||
                !keys.TryGetValue(keyId, out var encodedKey)) throw Invalid();
            using var verifier = ImportKey(encodedKey);
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw Invalid();
            var revision = Integer(data["revision"], MaximumRevision);
            var publishedAt = Integer(data["published_at"], 253402300799);
            var apply = String(data["apply"]);
            if (apply is not ("next_connection" or "now")) throw Invalid();
            return parse(data, new(policyId, keyId, revision, publishedAt, apply == "now",
                Convert.ToHexString(SHA256.HashData(payload))));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or
            InvalidOperationException or FormatException or CryptographicException or KeyNotFoundException)
        {
            throw Invalid();
        }
    }

    internal static bool ValidateKey(string base64Spki)
    {
        try { using var key = ImportKey(base64Spki); return true; }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException or CryptographicException)
        { return false; }
    }

    internal static bool IsIdentifier(string value) => value is { Length: >= 1 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static ECDsa ImportKey(string encoded)
    {
        var bytes = Base64(encoded, 256);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            var parameters = key.ExportParameters(false);
            if (read != bytes.Length || parameters.Curve.Oid.Value != P256Oid || key.KeySize != 256 ||
                parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32) throw Invalid();
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    private static byte[] Base64(string value, int maximumBytes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > ((maximumBytes + 2) / 3) * 4 ||
            value.Length % 4 != 0) throw Invalid();
        var decoded = Convert.FromBase64String(value);
        if (decoded.Length > maximumBytes || Convert.ToBase64String(decoded) != value) throw Invalid();
        return decoded;
    }

    internal static Dictionary<string, JsonElement> Object(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Contains(field.Name, StringComparer.Ordinal) || !result.TryAdd(field.Name, field.Value))
                throw Invalid();
        if (result.Count != names.Length) throw Invalid();
        return result;
    }

    internal static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? throw Invalid() : throw Invalid();

    private static long Integer(JsonElement value, long maximum)
    {
        if (value.ValueKind != JsonValueKind.Number) throw Invalid();
        var raw = value.GetRawText();
        if (raw.Length == 0 || raw.Any(c => !char.IsAsciiDigit(c)) ||
            !value.TryGetInt64(out var number) || number < 1 || number > maximum) throw Invalid();
        return number;
    }

    private static InvalidDataException Invalid() => new("Invalid Business policy.");
}
