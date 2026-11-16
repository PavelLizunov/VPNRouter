using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyPhoneTests
{
    [Fact]
    public void SignedPhone_AuthenticatesTypedRulesAndAuthoritativeEmpty()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rules = "[{\"type\":\"domain_suffix\",\"value\":\"uiscom.ru\",\"action\":\"proxy\",\"comment\":\"Edited\"}," +
            "{\"type\":\"ip_cidr\",\"value\":\"195.211.120.35/32\",\"action\":\"proxy\",\"comment\":\"\"}]";
        var payload = Payload(rules);
        var parsed = Parse(key, payload);
        Assert.Equal(2, parsed.Rules.Count);
        Assert.True(parsed.ApplyNow);
        Assert.Equal(7, parsed.Revision);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), parsed.PayloadDigest);
        Assert.Empty(Parse(key, Payload()).Rules);
        Assert.Throws<NotSupportedException>(() => ((IList<BusinessPhonePolicyRule>)parsed.Rules).Clear());
    }

    [Fact]
    public void SignatureKindAndIdentity_CannotBeSubstitutedAcrossFeeds()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = Keys(key);
        var envelope = Sign(key, Payload());
        Assert.Throws<InvalidDataException>(() => BusinessPolicy.Parse(envelope, "corp-test", keys));
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(envelope, "other-policy", keys));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(envelope, "corp-test", Keys(other)));
        var modified = JsonSerializer.Deserialize<Dictionary<string, string>>(envelope)!;
        modified["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Payload().Replace("\"revision\":7", "\"revision\":8")));
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(modified), "corp-test", keys));
    }

    public static IEnumerable<object[]> InvalidPayloads()
    {
        yield return [Payload().Replace("\"kind\":\"telephony\",", "")];
        yield return [Payload().Replace("telephony", "sites")];
        yield return [Payload().Replace("\"kind\":\"telephony\"", "\"kind\":\"telephony\",\"kind\":\"telephony\"")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1,\"future\":1")];
        yield return [Payload().Replace("\"revision\":7", "\"revision\":7.0")];
        yield return [Payload().Replace("\"rules\":[]", "\"rules\":null")];
        yield return [Payload("[{\"type\":\"ip_cidr\",\"value\":\"195.211.120.35/32\",\"action\":\"direct\"}]")];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "example.com", "direct", "\n")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "example.com", "direct", new string('я', 129))))];
        yield return [Payload(Rules(new("domain", "example.com", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "EXAMPLE.COM", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "co.uk", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "corp.ninitux.com", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "admin.corp.ninitux.com", "proxy", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "x.admin.corp.ninitux.com", "proxy", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "195.211.120.35", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "195.211.120.35/24", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "::ffff:195.211.120.35/128", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "2001:0db8::/32", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "2001:db8::/129", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "example.com", "block", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "192.0.2.0/24", "direct", ""), new BusinessPhonePolicyRule("ip_cidr", "192.0.2.0/24", "direct", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "192.0.2.0/24", "direct", ""), new BusinessPhonePolicyRule("ip_cidr", "192.0.2.1/32", "proxy", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("ip_cidr", "2001:db8::/32", "direct", ""), new BusinessPhonePolicyRule("ip_cidr", "2001:db8:1::/48", "proxy", "")))];
        yield return [Payload(Rules(new BusinessPhonePolicyRule("domain_suffix", "example.com", "direct", ""), new BusinessPhonePolicyRule("domain_suffix", "sub.example.com", "proxy", "")))];
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public void SignedMalformedPhone_IsRejectedWithSanitizedError(string payload)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var error = Assert.Throws<InvalidDataException>(() => Parse(key, payload));
        Assert.Equal("Invalid Business policy.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void PhoneBoundsAndCanonicalEnvelope_MatchSiteWireLimits()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rules = Enumerable.Range(0, 65).Select(i => new BusinessPhonePolicyRule("domain_suffix", $"s{i}.example.com", "direct", "")).ToArray();
        Assert.Equal(64, Parse(key, Payload(Rules(rules.Take(64).ToArray()))).Rules.Count);
        Assert.Throws<InvalidDataException>(() => Parse(key, Payload(Rules(rules))));
        var signed = Sign(key, Payload());
        var boundary = new byte[BusinessPolicy.MaximumResponseBytes];
        signed.CopyTo(boundary, 0);
        Array.Fill(boundary, (byte)' ', signed.Length, boundary.Length - signed.Length);
        Assert.Empty(BusinessPhonePolicy.Parse(boundary, "corp-test", Keys(key)).Rules);
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(new byte[BusinessPolicy.MaximumResponseBytes + 1], "corp-test", Keys(key)));
        Assert.Throws<InvalidDataException>(() => Parse(key, Payload() + new string(' ', BusinessPolicy.MaximumPayloadBytes)));
        var outer = JsonSerializer.Deserialize<Dictionary<string, string>>(signed)!;
        outer["payload"] = " " + outer["payload"];
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(outer), "corp-test", Keys(key)));
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.Parse(new byte[] { 0xff }, "corp-test", Keys(key)));
    }

    [Theory]
    [InlineData("domain_suffix", "https://UISCOM.RU/path", "uiscom.ru")]
    [InlineData("ip_cidr", "195.211.120.35", "195.211.120.35/32")]
    [InlineData("ip_cidr", "192.0.2.34/24", "192.0.2.0/24")]
    [InlineData("ip_cidr", "2001:0DB8::1", "2001:db8::1/128")]
    [InlineData("ip_cidr", "2001:db8::7/32", "2001:db8::/32")]
    [InlineData("ip_cidr", "::192.0.2.1", "::c000:201/128")]
    [InlineData("ip_cidr", "::/0", "::/0")]
    [InlineData("ip_cidr", "2001:db8:0:1:0:0:0:1", "2001:db8:0:1::1/128")]
    public void NormalizeInput_ProducesPreviewCanonicalValues(string type, string value, string expected) =>
        Assert.Equal(expected, BusinessPhonePolicy.NormalizeInput(type, value));

    [Theory]
    [InlineData("127.1")]
    [InlineData("192.0.2.1/33")]
    [InlineData("::ffff:c000:201")]
    [InlineData("fe80::1%eth0/128")]
    [InlineData("[2001:db8::1]")]
    public void NormalizeIp_RejectsAmbiguousOrUnroutableSyntax(string value) =>
        Assert.Throws<InvalidDataException>(() => BusinessPhonePolicy.NormalizeInput("ip_cidr", value));

    [Fact]
    public void SameDirectionOverlapAndDifferentAddressFamilies_AreValid()
    {
        BusinessPhonePolicy.ValidateRules([
            new BusinessPhonePolicyRule("domain_suffix", "example.com", "direct", ""),
            new BusinessPhonePolicyRule("domain_suffix", "sub.example.com", "direct", ""),
            new BusinessPhonePolicyRule("ip_cidr", "192.0.2.0/24", "direct", ""),
            new BusinessPhonePolicyRule("ip_cidr", "192.0.2.1/32", "direct", ""),
            new BusinessPhonePolicyRule("ip_cidr", "2001:db8::/32", "proxy", "")]);
        Assert.False(BusinessPhonePolicy.IntersectsIp("0.0.0.0/0", "::/0"));
        Assert.True(BusinessPhonePolicy.IntersectsIp("2001:db8::/32", "2001:db8:ffff::/48"));
        Assert.False(BusinessPhonePolicy.IntersectsIp("192.0.2.0/25", "192.0.2.128/25"));
    }

    private static string Payload(string rules = "[]") =>
        "{\"schema\":1,\"kind\":\"telephony\",\"policy_id\":\"corp-test\",\"key_id\":\"phone-key\",\"revision\":7,\"published_at\":1794657600,\"apply\":\"now\",\"rules\":" + rules + "}";

    private static string Rules(params BusinessPhonePolicyRule[] rules) => JsonSerializer.Serialize(
        rules.Select(r => new { type = r.Type, value = r.Value, action = r.Action, comment = r.Comment }));

    private static BusinessPhonePolicySnapshot Parse(ECDsa key, string payload) => BusinessPhonePolicy.Parse(Sign(key, payload), "corp-test", Keys(key));
    private static Dictionary<string, string> Keys(ECDsa key) => new() { ["phone-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
    private static byte[] Sign(ECDsa key, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        });
    }
}
