using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyConfigurationTests
{
    internal const string ReadToken = "synthetic-policy-reader-token-not-live";

    [Fact]
    public void Parse_ValidPolicy_SeparatesTrustedReadCredentialFromOtherServices()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = Configuration(key);
        Assert.Equal("fixture-policy", configuration.PolicyId);
        Assert.Equal("https://example.invalid/api/v1/corp/policy", configuration.Endpoint.AbsoluteUri);
        Assert.Equal(ReadToken, configuration.ReadToken);
        Assert.Equal(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), configuration.Keys["fixture-key"]);
        Assert.Equal(64, configuration.TrustId.Length);
        var bundle = Bundle(PolicyJson(key));
        BusinessProvisioningStore.ValidateBundle(bundle);
        Assert.NotNull(BusinessClientConfiguration.Parse(bundle).Subscription);
    }

    public static IEnumerable<object[]> InvalidPolicies()
    {
        yield return ["null"];
        yield return ["[]"];
        yield return ["{}"];
        yield return ["false"];
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = PolicyJson(key);
        yield return [valid.Replace("\"policy_id\":\"fixture-policy\"", "\"policy_id\":null")];
        yield return [valid.Replace("\"fixture-policy\"", "\"bad id\"")];
        yield return [valid.Replace("\"policy_id\":", "\"policy_id\":\"duplicate\",\"policy_id\":")];
        yield return [valid.Replace("\"endpoint\":", "\"extra\":1,\"endpoint\":")];
        yield return [valid.Replace("https://example.invalid", "http://example.invalid")];
        yield return [valid.Replace("https://example.invalid", "https://user:secret@example.invalid")];
        yield return [valid.Replace("/corp/policy", "/corp/policy?secret=value")];
        yield return [valid.Replace("/corp/policy", "/corp/policy#fragment")];
        yield return [valid.Replace(ReadToken, "short")];
        yield return [valid.Replace(ReadToken, "synthetic-policy-token=inside-invalid")];
        yield return [valid.Replace(ReadToken, "synthetic-policy-reader-token with-space")];
        yield return [valid.Replace("\"keys\":[", "\"keys\":[],\"keys\":[")];
        yield return [valid.Replace("\"id\":\"fixture-key\"", "\"id\":\"fixture-key\",\"id\":\"other\"")];
        yield return [PolicyJson(key, spki: "invalid-key")];
    }

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void Parse_InvalidOptionalPolicy_PreservesVpnProvisioning(string policy)
    {
        var bundle = Bundle(policy);
        BusinessProvisioningStore.ValidateBundle(bundle);
        var configuration = BusinessClientConfiguration.Parse(bundle);
        Assert.Null(configuration.Policy);
        Assert.NotNull(configuration.Subscription);
        Assert.Equal(3, BusinessDefaults.ParseConnection(bundle).Count);
    }

    [Fact]
    public void Parse_MissingOrDuplicateOptionalPolicy_DisablesOnlyPolicy()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var duplicate = Bundle(PolicyJson(key)).Replace("\"policy\":", "\"policy\":{},\"policy\":");
        BusinessProvisioningStore.ValidateBundle(duplicate);
        Assert.Null(BusinessClientConfiguration.Parse(duplicate).Policy);
        Assert.NotNull(BusinessClientConfiguration.Parse(duplicate).Subscription);
        Assert.Null(BusinessClientConfiguration.Parse("{\"business\":{}}").Policy);
    }

    [Fact]
    public void Parse_KeysAndReadTokenRotation_DoNotResetTheNamespace()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Configuration(first);
        var changed = Configuration(second, token: "another-synthetic-policy-reader-token");
        Assert.Equal(original.TrustId, changed.TrustId);
        Assert.NotEqual(original.TrustId, Configuration(first, policyId: "other-policy").TrustId);
        var anotherEndpoint = PolicyJson(first).Replace("/corp/policy", "/corp/other-policy");
        Assert.NotEqual(original.TrustId, BusinessClientConfiguration.Parse(Bundle(anotherEndpoint)).Policy!.TrustId);
    }

    [Fact]
    public void Parse_KeySet_MustBeBoundedUniqueAndP256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var keyJson = JsonSerializer.Serialize(new { id = "fixture-key", spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        foreach (var entries in new[] { "[]", "[" + keyJson + "," + keyJson + "]",
            "[" + string.Join(',', Enumerable.Range(0, 5).Select(i => keyJson.Replace("fixture-key", "key-" + i))) + "]" })
        {
            var policy = JsonSerializer.Serialize(new { policy_id = "fixture-policy", endpoint = "https://example.invalid/api/v1/corp/policy", read_token = ReadToken });
            policy = policy[..^1] + ",\"keys\":" + entries + "}";
            Assert.Null(BusinessClientConfiguration.Parse(Bundle(policy)).Policy);
        }
        Assert.Null(BusinessClientConfiguration.Parse(Bundle(PolicyJson(wrongCurve))).Policy);
    }

    internal static BusinessPolicyConfiguration Configuration(ECDsa key, string policyId = "fixture-policy", string token = ReadToken) =>
        BusinessClientConfiguration.Parse(Bundle(PolicyJson(key, policyId, token))).Policy
        ?? throw new InvalidOperationException("Synthetic policy configuration was rejected.");

    internal static string PolicyJson(ECDsa key, string policyId = "fixture-policy", string token = ReadToken,
        string? spki = null) =>
        JsonSerializer.Serialize(new
        {
            policy_id = policyId,
            endpoint = "https://example.invalid/api/v1/corp/policy",
            read_token = token,
            keys = new[] { new { id = "fixture-key", spki = spki ?? Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) } }
        });

    private static string Bundle(string policy) =>
        "{\"config\":" + JsonSerializer.Serialize(Convert.ToBase64String(Encoding.UTF8.GetBytes(BusinessClientConfigurationTests.Three))) +
        ",\"business\":{\"subscription_url\":\"https://example.invalid/subscription\",\"policy\":" + policy + "}}";
}
