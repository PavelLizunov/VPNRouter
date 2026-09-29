using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class SubscriptionFetcherPlaceholderTests
{
    private const string PlaceholderPubkey =
        "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";

    private const string CleanVless1 =
        "vless://uuid1@server1.example:443?security=tls&type=tcp&flow=xtls-rprx-vision#clean1";
    private const string CleanVless2 =
        "vless://uuid2@server2.example:443?security=tls&type=tcp#clean2";
    private const string CleanVless3 =
        "vless://uuid3@server3.example:443?security=tls&type=tcp#clean3";

    private static string PlaceholderVless(int i) =>
        $"vless://uuid-bad-{i}@bad{i}.example:443?security=reality&sni=yahoo.com&fp=firefox" +
        $"&pbk={PlaceholderPubkey}&sid=78ca7952&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#bad{i}";

    private static string Base64(string s) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void ParseBody_OneCleanOnePlaceholder_DropsPlaceholder()
    {
        var body = $"{CleanVless1}\n{PlaceholderVless(1)}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Single(result);
        Assert.Equal("server1.example", result[0].Server);
    }

    [Fact]
    public void ParseBody_AllPlaceholders_ReturnsEmptyList()
    {
        var body =
            $"{PlaceholderVless(1)}\n" +
            $"{PlaceholderVless(2)}\n" +
            $"{PlaceholderVless(3)}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBody_MixedSchemes_PlaceholderInVless_DropsOnlyThat()
    {
        var body =
            $"{PlaceholderVless(1)}\n" +
            "hysteria2://pass@hy.example:9443/?sni=foo.example&insecure=0#hy-clean\n" +
            "tuic://u-uid:pw@tuic.example:443?sni=foo.example&congestion_control=cubic#tuic-clean\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Server == "hy.example");
        Assert.Contains(result, e => e.Server == "tuic.example");
        Assert.DoesNotContain(result, e => e.Server == "bad1.example");
    }

    [Fact]
    public void ParseBody_CleanList_AllPass()
    {
        var body =
            $"{CleanVless1}\n" +
            $"{CleanVless2}\n" +
            $"{CleanVless3}\n";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(3, result.Count);
        Assert.Equal("server1.example", result[0].Server);
        Assert.Equal("server2.example", result[1].Server);
        Assert.Equal("server3.example", result[2].Server);
    }

    [Fact]
    public void ParseBody_JsonWrapper_PlaceholderInBase64Content_DropsPlaceholder()
    {
        var inner = Base64($"{CleanVless1}\n{PlaceholderVless(1)}\n");
        var body = $@"{{""config"":""{inner}""}}";

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Single(result);
        Assert.Equal("server1.example", result[0].Server);
    }

    [Fact]
    public void ParseBody_OutParam_ReportsDroppedCount()
    {
        var body =
            $"{CleanVless1}\n" +
            $"{PlaceholderVless(1)}\n" +
            $"{PlaceholderVless(2)}\n" +
            $"{CleanVless2}\n" +
            $"{PlaceholderVless(3)}\n";

        var result = SubscriptionFetcher.ParseBody(body, out var droppedCount);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, droppedCount);
    }
}
