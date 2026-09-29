using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SubscriptionFetcherParserTests
{
    private const string Uri1 = "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#one";
    private const string Uri2 = "vless://uuid2@server2.example:443?security=tls&type=tcp#two";

    private static string Base64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void ParseBody_PlainVlessUris_ReturnsAllEntries()
    {
        var body = $"{Uri1}\n{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
        Assert.Equal("server1.example", result[0].Server);
        Assert.Equal("server2.example", result[1].Server);
    }

    [Fact]
    public void ParseBody_RawBase64_DecodesAndParses()
    {
        var body = Base64($"{Uri1}\n{Uri2}");

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_JsonWrapperWithConfig_DecodesBase64Inner()
    {
        var inner = Base64($"{Uri1}\n{Uri2}");
        var body = $@"{{""config"":""{inner}""}}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_JsonWithoutConfigField_ReturnsEmpty()
    {
        var body = @"{""servers"":[]}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_MalformedJson_FallsBackToTrimmedBodyThenEmpty()
    {
        var body = "{not-json";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_DuplicateUri_DeduplicatedByServerPortUuidFlow()
    {
        var body = $"{Uri1}\n{Uri1}\n{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_UnsupportedSchemes_FilteredOut()
    {
        var body =
            $"{Uri1}\n" +
            "http://not-a-vpn.example/\n" +
            "# this is a comment line\n" +
            "random-garbage-no-scheme\n" +
            $"{Uri2}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBody_EmptyBody_ReturnsEmpty()
    {
        Assert.Empty(SubscriptionFetcher.ParseBody(""));
        Assert.Empty(SubscriptionFetcher.ParseBody("   \n\t  \n"));
    }
}
