using System;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class PlaceholderInputGateTests
{
    private const string PlaceholderPubkey = "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU";
    private const string CleanPubkey = "abcDEFghi1234567890realPubkeyValueXYZ-_pq";

    private static string BuildVlessUri(string pubkey, string server = "1.2.3.4")
    {
        return $"vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@{server}:443" +
               $"?security=reality&sni=yahoo.com&fp=firefox&pbk={pubkey}" +
               $"&sid=abcd1234&type=tcp&flow=xtls-rprx-vision#test-server";
    }

    [Fact]
    public void VlessUriParser_PlaceholderPubkey_Throws()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => VlessUriParser.Parse(uri));
        Assert.Equal("reality.public_key", ex.OffendingField);
        Assert.Equal(PlaceholderPubkey, ex.OffendingValue);
    }

    [Fact]
    public void VlessUriParser_TryParse_PlaceholderPubkey_ReturnsNull()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var result = VlessUriParser.TryParse(uri);

        Assert.Null(result);
    }

    [Fact]
    public void VlessUriParser_CleanUrl_Parses()
    {
        var uri = BuildVlessUri(CleanPubkey);

        var entry = VlessUriParser.Parse(uri);

        Assert.NotNull(entry);
        Assert.Equal(CleanPubkey, entry.Reality.PublicKey);
        Assert.Equal("1.2.3.4", entry.Server);
        Assert.Equal(443, entry.Port);
        Assert.Equal("test-server", entry.Name);
    }

    [Fact]
    public void ServerUriParser_PlaceholderViaVlessDispatch_Throws()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => ServerUriParser.Parse(uri));
        Assert.Equal("reality.public_key", ex.OffendingField);
    }

    [Fact]
    public void ServerUriParser_ParseMultiple_DropsPlaceholder()
    {
        var clean = BuildVlessUri(CleanPubkey, server: "8.8.8.8");
        var poisoned = BuildVlessUri(PlaceholderPubkey, server: "1.1.1.1");
        var garbage = "vless://this-is-not-a-valid-uri";

        var blob = string.Join("\n", clean, poisoned, garbage);

        var entries = ServerUriParser.ParseMultiple(blob);

        Assert.Single(entries);
        Assert.Equal("8.8.8.8", entries[0].Server);
        Assert.Equal(CleanPubkey, entries[0].Reality.PublicKey);
    }

    [Fact]
    public void PlaceholderConfigException_ExposesField()
    {
        var uri = BuildVlessUri(PlaceholderPubkey);

        var ex = Assert.Throws<PlaceholderConfigException>(() => VlessUriParser.Parse(uri));

        Assert.Equal("reality.public_key", ex.OffendingField);
        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
