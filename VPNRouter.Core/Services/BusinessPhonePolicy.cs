using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VPNRouter.Core.Services;

internal sealed record BusinessPhonePolicyRule(string Type, string Value, string Action, string Comment);

internal sealed class BusinessPhonePolicySnapshot
{
    internal string PolicyId { get; }
    internal string KeyId { get; }
    internal long Revision { get; }
    internal long PublishedAt { get; }
    internal bool ApplyNow { get; }
    internal IReadOnlyList<BusinessPhonePolicyRule> Rules { get; }
    internal string PayloadDigest { get; }

    internal BusinessPhonePolicySnapshot(string policyId, string keyId, long revision, long publishedAt,
        bool applyNow, IEnumerable<BusinessPhonePolicyRule> rules, string payloadDigest)
    {
        PolicyId = policyId;
        KeyId = keyId;
        Revision = revision;
        PublishedAt = publishedAt;
        ApplyNow = applyNow;
        Rules = Array.AsReadOnly(rules.ToArray());
        PayloadDigest = payloadDigest;
    }
}

internal static class BusinessPhonePolicy
{
    internal const int MaximumRules = 64;
    internal const string LegacySipCidr = "195.211.120.35/32";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static BusinessPhonePolicySnapshot Parse(ReadOnlyMemory<byte> envelope,
        string expectedPolicyId, IReadOnlyDictionary<string, string> keys) => BusinessPolicy.ParseSigned(
        envelope, expectedPolicyId, keys,
        ["schema", "kind", "policy_id", "key_id", "revision", "published_at", "apply", "rules"],
        (data, metadata) =>
        {
            if (BusinessPolicy.String(data["kind"]) != "telephony") throw Invalid();
            var list = data["rules"];
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > MaximumRules) throw Invalid();
            var rules = new List<BusinessPhonePolicyRule>(list.GetArrayLength());
            foreach (var item in list.EnumerateArray())
            {
                var rule = BusinessPolicy.Object(item, "type", "value", "action", "comment");
                rules.Add(new(BusinessPolicy.String(rule["type"]), BusinessPolicy.String(rule["value"]),
                    BusinessPolicy.String(rule["action"]), BusinessPolicy.String(rule["comment"])));
            }
            ValidateRules(rules);
            return new BusinessPhonePolicySnapshot(metadata.PolicyId, metadata.KeyId, metadata.Revision,
                metadata.PublishedAt, metadata.ApplyNow, rules, metadata.PayloadDigest);
        });

    internal static void ValidateRules(IReadOnlyList<BusinessPhonePolicyRule> rules)
    {
        try
        {
            if (rules.Count > MaximumRules) throw Invalid();
            var seen = new HashSet<(string, string)>();
            foreach (var rule in rules)
            {
                if (rule == null || rule.Value != NormalizeInput(rule.Type, rule.Value) ||
                    rule.Action is not ("direct" or "proxy") || !seen.Add((rule.Type, rule.Value)) ||
                    rule.Comment == null || rule.Comment.Any(char.IsControl) ||
                    StrictUtf8.GetByteCount(rule.Comment) > 256) throw Invalid();
            }
            for (var i = 0; i < rules.Count; i++)
                for (var j = i + 1; j < rules.Count; j++)
                    if (rules[i].Type == rules[j].Type && rules[i].Action != rules[j].Action &&
                        (rules[i].Type == "domain_suffix"
                            ? BusinessPolicyDomains.Intersects(rules[i].Value, rules[j].Value)
                            : IntersectsIp(rules[i].Value, rules[j].Value))) throw Invalid();
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FormatException)
        { throw Invalid(); }
    }

    internal static string NormalizeInput(string type, string value)
    {
        try
        {
            if (type == "domain_suffix")
            {
                var domain = BusinessPolicyDomains.NormalizeInput(value);
                if (BusinessPolicyDomains.Intersects(domain, BusinessPolicyDomains.PortalDomain)) throw Invalid();
                return domain;
            }
            if (type != "ip_cidr") throw Invalid();
            var (bytes, prefix) = ParseNetwork(value);
            return FormatAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FormatException)
        { throw Invalid(); }
    }

    internal static bool IntersectsIp(string left, string right)
    {
        var (leftBytes, leftPrefix) = ParseNetwork(left);
        var (rightBytes, rightPrefix) = ParseNetwork(right);
        if (leftBytes.Length != rightBytes.Length) return false;
        var prefix = Math.Min(leftPrefix, rightPrefix);
        for (var i = 0; i < leftBytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            var mask = (byte)(255 << (8 - bits));
            if ((leftBytes[i] & mask) != (rightBytes[i] & mask)) return false;
        }
        return true;
    }

    private static string FormatAddress(byte[] bytes)
    {
        if (bytes.Length == 4) return new IPAddress(bytes).ToString();
        // Rust's IPv6 display uses hexadecimal groups for nonmapped addresses,
        // including IPv4-compatible IPv6. Avoid platform-dependent dotted tails.
        var groups = Enumerable.Range(0, 8).Select(i => (bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToArray();
        var bestStart = -1;
        var bestLength = 1;
        for (var i = 0; i < groups.Length; i++)
        {
            if (groups[i] != 0) continue;
            var start = i;
            while (i < groups.Length && groups[i] == 0) i++;
            if (i - start > bestLength) { bestStart = start; bestLength = i - start; }
        }
        var hex = groups.Select(group => group.ToString("x", CultureInfo.InvariantCulture)).ToArray();
        if (bestStart < 0) return string.Join(":", hex);
        return string.Join(":", hex.Take(bestStart)) + "::" + string.Join(":", hex.Skip(bestStart + bestLength));
    }

    private static (byte[] Bytes, int Prefix) ParseNetwork(string input)
    {
        if (input is not { Length: > 0 and <= 4096 } || input.Any(char.IsControl)) throw Invalid();
        var value = input.Trim();
        var parts = value.Split('/');
        if (parts.Length > 2 || parts[0].IndexOfAny(['%', '[', ']']) >= 0 ||
            !IPAddress.TryParse(parts[0], out var address) || address.IsIPv4MappedToIPv6) throw Invalid();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork && parts[0] != address.ToString()) throw Invalid();
        var prefix = bytes.Length * 8;
        if (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(c => !char.IsAsciiDigit(c)) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix))) throw Invalid();
        if (prefix < 0 || prefix > bytes.Length * 8) throw Invalid();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(255 << (8 - bits));
        }
        return (bytes, prefix);
    }

    private static InvalidDataException Invalid() => new("Invalid Business telephony policy.");
}
