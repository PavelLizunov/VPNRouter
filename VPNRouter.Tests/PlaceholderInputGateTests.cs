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
}
