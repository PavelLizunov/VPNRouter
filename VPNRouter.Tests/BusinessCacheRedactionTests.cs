using System.Text;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Tests;

public sealed class BusinessCacheRedactionTests
{
    private const string SyntheticCredential = "11111111-2222-4333-8444-555555555555";
    private const string SyntheticConnection = "vless://" + SyntheticCredential +
        "@192.0.2.1:443?security=tls&type=tcp#FixtureCache";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MalformedYaml_WrappedSubscriptionCache_IsOmittedWithoutExposingEncodedOrDecodedCredentials(string newline)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(SyntheticConnection));
        var chunks = encoded.Chunk(32).Select(chunk => new string(chunk)).ToArray();
        var wrapped = string.Join(newline, chunks);
        Assert.Equal(SyntheticCredential, Assert.Single(SubscriptionFetcher.ParseBody(wrapped)).Uuid);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 32));

        var malformed = "app:" + newline +
            "  business_subscription_cache: |" + newline +
            string.Join(newline, chunks.Select(chunk => "    " + chunk)) + newline +
            "unrelated: [unclosed" + newline;

        var result = DiagnosticsRedactor.RedactConfigYaml(malformed);

        Assert.Equal(DiagnosticsRedactor.OmittedOnParseFailure, result);
        Assert.DoesNotContain(encoded, result);
        Assert.DoesNotContain(SyntheticCredential, result);
        Assert.DoesNotContain(SyntheticConnection, result);
        Assert.All(chunks, chunk => Assert.DoesNotContain(chunk, result));
    }

    [Theory]
    [InlineData("|")]
    [InlineData(">-")]
    public void MalformedYaml_UnknownMultilineScalar_IsOmittedWithoutGuessingItsStructure(string scalarStyle)
    {
        const string first = "fixture-private-fragment-one";
        const string second = "fixture-private-fragment-two";
        var malformed = "app:\n" +
            "  opaque_provider_payload: " + scalarStyle + "\n" +
            "    " + first + "\n" +
            "    " + second + "\n" +
            "unrelated: [unclosed\n";

        var result = DiagnosticsRedactor.RedactConfigYaml(malformed);

        Assert.Equal(DiagnosticsRedactor.OmittedOnParseFailure, result);
        Assert.DoesNotContain(first, result);
        Assert.DoesNotContain(second, result);
    }
}
