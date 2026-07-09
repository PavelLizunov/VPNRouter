#nullable enable

using System.Net;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins <see cref="ProviderKey"/> (urltest R3): the OFFLINE provider/subnet
/// grouping key — /24 for v4, /48 for v6, opaque "net:" strings, never throws
/// on garbage, literal-IP fast path with zero DNS.
/// </summary>
public class ProviderKeyTests
{
    [Theory]
    [InlineData("104.194.156.93", "net:104.194.156.0/24")]
    [InlineData("104.194.156.1",  "net:104.194.156.0/24")]   // same /24 → same key
    [InlineData("104.194.157.93", "net:104.194.157.0/24")]   // next /24 → different key
    [InlineData("8.8.8.8",        "net:8.8.8.0/24")]
    public void V4_GroupsBysSlash24(string ip, string expected)
        => Assert.Equal(expected, ProviderKey.ForIp(ip));

    [Fact]
    public void V6_GroupsBySlash48()
    {
        var a = ProviderKey.For(IPAddress.Parse("2a01:4f8:c2c:1234::1"));
        var b = ProviderKey.For(IPAddress.Parse("2a01:4f8:c2c:ffff::2"));   // same /48
        var c = ProviderKey.For(IPAddress.Parse("2a01:4f8:c2d::1"));         // different /48
        Assert.Equal("net:2a01:04f8:0c2c::/48", a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("cdn.ninitux.top")]   // hostname → null on the literal fast path
    public void ForIp_NonLiterals_ReturnNull(string? input)
        => Assert.Null(ProviderKey.ForIp(input));

    [Fact]
    public async Task ResolveAsync_LiteralIp_NeedsNoDns()
    {
        var key = await ProviderKey.ResolveAsync("10.1.2.3", TestContext.Current.CancellationToken);
        Assert.Equal("net:10.1.2.0/24", key);
    }

    [Fact]
    public async Task ResolveAsync_Garbage_IsNullNotThrow()
    {
        var key = await ProviderKey.ResolveAsync(
            "definitely-not-a-real-host-4f7a1.invalid", TestContext.Current.CancellationToken);
        Assert.Null(key);
    }
}
