using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyTests
{
    [Fact]
    public void Parse_SharedPublicFixture_VerifiesExactBytesAndBothTimings()
    {
        using var stream = typeof(BusinessPolicyTests).Assembly.GetManifestResourceStream(
            "VPNRouter.Tests.Fixtures.BusinessPolicy.v1.json");
        Assert.NotNull(stream);
        using var fixture = JsonDocument.Parse(stream);
        var root = fixture.RootElement;
        var identity = root.GetProperty("policy_id").GetString()!;
        var keys = new Dictionary<string, string>
        {
            [root.GetProperty("key_id").GetString()!] = root.GetProperty("spki").GetString()!
        };
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var envelope = Encoding.UTF8.GetBytes(item.GetProperty("envelope").GetRawText());
            if (!item.GetProperty("valid").GetBoolean())
            {
                AssertInvalid(() => BusinessPolicy.Parse(envelope, identity, keys));
                continue;
            }
            var snapshot = BusinessPolicy.Parse(envelope, identity, keys);
            Assert.Equal(item.GetProperty("payload_sha256").GetString(), snapshot.PayloadDigest);
            Assert.Equal(item.GetProperty("name").GetString() == "empty", snapshot.ApplyNow);
            Assert.Equal(snapshot.ApplyNow ? 8 : 7, snapshot.Revision);
            Assert.Equal(snapshot.ApplyNow ? 0 : 2, snapshot.Rules.Count);
            Assert.Equal(identity, snapshot.PolicyId);
            Assert.Equal("fixture-key", snapshot.KeyId);
            Assert.Equal(1794657600, snapshot.PublishedAt);
        }
    }

    [Fact]
    public void Parse_SignedWhitespaceIsPreservedInDigest_AndCannotBeReserialized()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = " \n" + Payload() + "\n";
        var envelope = Sign(key, payload);
        var snapshot = BusinessPolicy.Parse(envelope, "corp-fixture", Keys(key));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), snapshot.PayloadDigest);
        var changed = JsonNode.Parse(envelope)!.AsObject();
        changed["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Trim()));
        AssertInvalid(() => BusinessPolicy.Parse(Encoding.UTF8.GetBytes(changed.ToJsonString()), "corp-fixture", Keys(key)));
    }

    [Fact]
    public void Parse_RejectsWrongPolicyIdentityUnknownKeyAndDifferentTrustedKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(key, Payload());
        AssertInvalid(() => BusinessPolicy.Parse(envelope, "other-corp", Keys(key)));
        AssertInvalid(() => BusinessPolicy.Parse(envelope, "corp-fixture", new Dictionary<string, string>()));
        AssertInvalid(() => BusinessPolicy.Parse(envelope, "corp-fixture", Keys(other)));
    }

    [Fact]
    public void Parse_KeyIdMustMatchExactly_EvenWhenCallerDictionaryIgnoresCase()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["FIXTURE-KEY"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };
        AssertInvalid(() => BusinessPolicy.Parse(Sign(key, Payload()), "corp-fixture", keys));
    }

    [Fact]
    public void ValidateKey_RequiresCompleteCanonicalP256Spki()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);
        var bytes = p256.ExportSubjectPublicKeyInfo();
        Assert.True(BusinessPolicy.ValidateKey(Convert.ToBase64String(bytes)));
        Assert.False(BusinessPolicy.ValidateKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        Assert.False(BusinessPolicy.ValidateKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
        Assert.False(BusinessPolicy.ValidateKey(Convert.ToBase64String(bytes) + "\n"));
        byte[] trailing = [.. bytes, 0];
        Assert.False(BusinessPolicy.ValidateKey(Convert.ToBase64String(trailing)));
        Assert.False(BusinessPolicy.ValidateKey(Convert.ToBase64String(p256.ExportECPrivateKey())));
        Assert.False(BusinessPolicy.ValidateKey("not a public key"));
    }

    public static IEnumerable<object[]> InvalidPayloads()
    {
        yield return ["[]"];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1,\"schema\":1")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1,\"schema\\u0000\":1")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1,\"future\":true")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":2")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1.0")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":1e0")];
        yield return [Payload().Replace("\"schema\":1", "\"schema\":\"1\"")];
        yield return [Payload().Replace("\"key_id\":\"fixture-key\"", "\"key_id\":\"fixture-key\",\"key_id\":\"fixture-key\"")];
        foreach (var invalid in new[] { "0", "-1", "1.0", "1e1", "9007199254740992", "9223372036854775808", "null", "true", "\"1\"" })
            yield return [Payload().Replace("\"revision\":7", "\"revision\":" + invalid)];
        foreach (var invalid in new[] { "0", "-1", "253402300800", "1.5", "1e3", "null" })
            yield return [Payload().Replace("\"published_at\":1794657600", "\"published_at\":" + invalid)];
        yield return [Payload().Replace("\"apply\":\"next_connection\"", "\"apply\":\"always\"")];
        yield return [Payload().Replace("\"rules\":[]", "\"rules\":null")];
        yield return [Payload("[null]")];
        yield return [Payload("[{}]")];
        yield return [Payload("[{\"domain\":\"example.com\",\"action\":\"direct\",\"comment\":\"\",\"domain\":\"example.org\"}]")];
        yield return [Payload("[{\"domain\":\"example.com\",\"action\":\"direct\",\"comment\":\"\",\"future\":1}]")];
        yield return [Payload("[{\"domain\":\"example.com\",\"action\":null,\"comment\":\"\"}]")];
        yield return [Payload("[{\"domain\":\"example.com\",\"action\":\"direct\",\"comment\":\"\\uD800\"}]")];
        yield return [Payload() + " {}"];
        yield return [Payload().Replace("[]", "[/*comment*/]")];
        yield return [Payload().Replace("[]", "[[],]")];
        yield return [Payload().Replace("[]", "[[[[[[[[[[]]]]]]]]]]")];
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public void Parse_RejectsSignedMalformedOrAmbiguousPayloads_WithSanitizedErrors(string payload)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AssertInvalid(() => BusinessPolicy.Parse(Sign(key, payload), "corp-fixture", Keys(key)));
    }

    [Fact]
    public void Parse_RejectsInvalidEnvelopesAndNoncanonicalBase64()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Sign(key, Payload());
        var outer = JsonNode.Parse(valid)!.AsObject();
        var payload = outer["payload"]!.GetValue<string>();
        var signature = outer["signature"]!.GetValue<string>();
        var invalid = new[]
        {
            "[]", "{}", "not-json", "{\"payload\":null,\"signature\":\"x\"}",
            JsonSerializer.Serialize(new { payload, signature, algorithm = "none" }),
            "{\"payload\":" + JsonSerializer.Serialize(payload) + ",\"payload\":" + JsonSerializer.Serialize(payload) + ",\"signature\":" + JsonSerializer.Serialize(signature) + "}",
            JsonSerializer.Serialize(new { payload = " " + payload, signature }),
            JsonSerializer.Serialize(new { payload, signature = signature.TrimEnd('=') }),
            JsonSerializer.Serialize(new { payload, signature = Convert.ToBase64String(new byte[63]) }),
            JsonSerializer.Serialize(new { payload, signature = Convert.ToBase64String(new byte[65]) }),
            Encoding.UTF8.GetString(valid) + "{}"
        };
        foreach (var value in invalid)
            AssertInvalid(() => BusinessPolicy.Parse(Encoding.UTF8.GetBytes(value), "corp-fixture", Keys(key)));
        AssertInvalid(() => BusinessPolicy.Parse(new byte[] { 0xff, 0xfe }, "corp-fixture", Keys(key)));
        AssertInvalid(() => BusinessPolicy.Parse(Sign(key, new byte[] { 0xff }), "corp-fixture", Keys(key)));
    }

    [Fact]
    public void Parse_EnforcesResponsePayloadAndRuleCountLimits_AndAcceptsResponseBoundary()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = Sign(key, Payload());
        var boundary = new byte[BusinessPolicy.MaximumResponseBytes];
        valid.CopyTo(boundary, 0);
        Array.Fill(boundary, (byte)' ', valid.Length, boundary.Length - valid.Length);
        Assert.Empty(BusinessPolicy.Parse(boundary, "corp-fixture", Keys(key)).Rules);
        AssertInvalid(() => BusinessPolicy.Parse(new byte[BusinessPolicy.MaximumResponseBytes + 1], "corp-fixture", Keys(key)));
        AssertInvalid(() => BusinessPolicy.Parse(Sign(key, Payload() + new string(' ', BusinessPolicy.MaximumPayloadBytes)), "corp-fixture", Keys(key)));
        var rules = Enumerable.Range(0, 513).Select(i => new { domain = $"s{i}.example.com", action = "direct", comment = "" });
        AssertInvalid(() => BusinessPolicy.Parse(Sign(key, Payload(JsonSerializer.Serialize(rules))), "corp-fixture", Keys(key)));
        var maximumList = BusinessPolicy.Parse(Sign(key, Payload(JsonSerializer.Serialize(rules.Take(512)))), "corp-fixture", Keys(key));
        Assert.Equal(512, maximumList.Rules.Count);
    }

    [Fact]
    public void Parse_MaximumRevisionAndPublicationTime_AreExactIntegersWithoutClockDependence()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload().Replace("\"revision\":7", "\"revision\":9007199254740991")
            .Replace("\"published_at\":1794657600", "\"published_at\":253402300799");
        var result = BusinessPolicy.Parse(Sign(key, payload), "corp-fixture", Keys(key));
        Assert.Equal(9007199254740991, result.Revision);
        Assert.Equal(253402300799, result.PublishedAt);
    }

    [Fact]
    public void Snapshot_CopiesRuleCollection_AndDoesNotExposeMutableList()
    {
        var rules = new List<BusinessPolicyRule> { new("example.com", "direct", "") };
        var snapshot = new BusinessPolicySnapshot("corp-fixture", "fixture-key", 1, 1, false, rules, "digest");
        rules.Clear();
        Assert.Single(snapshot.Rules);
        Assert.Throws<NotSupportedException>(() => ((IList<BusinessPolicyRule>)snapshot.Rules).Clear());
    }

    private static string Payload(string rules = "[]") =>
        "{\"schema\":1,\"policy_id\":\"corp-fixture\",\"key_id\":\"fixture-key\",\"revision\":7,\"published_at\":1794657600,\"apply\":\"next_connection\",\"rules\":" + rules + "}";

    private static Dictionary<string, string> Keys(ECDsa key) => new()
    { ["fixture-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) };

    private static byte[] Sign(ECDsa key, string payload) => Sign(key, Encoding.UTF8.GetBytes(payload));
    private static byte[] Sign(ECDsa key, byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });

    private static void AssertInvalid(Action action)
    {
        var error = Assert.Throws<InvalidDataException>(action);
        Assert.Equal("Invalid Business policy.", error.Message);
        Assert.Null(error.InnerException);
    }
}
