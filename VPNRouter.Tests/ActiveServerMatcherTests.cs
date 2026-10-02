using VPNRouter.App.ViewModels;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ActiveServerMatcherTests
{
    private static readonly (string Name, string Host)[] Servers =
    {
        ("Sweden VLESS ~ninitux", "155.4.244.204"),
        ("Iceland New HY2 ~ninitux", "89.126.249.149"),
        ("edge.example.net", "Edge.Example.net"),
    };

    [Fact]
    public void ConnectedAddress_NamesTheSubscriptionServerThatOwnsIt()
    {
        Assert.Equal("Sweden VLESS ~ninitux", ActiveServerMatcher.FindName("155.4.244.204", Servers));
    }

    [Fact]
    public void HostNames_AreComparedIgnoringCase()
    {
        Assert.Equal("edge.example.net", ActiveServerMatcher.FindName("EDGE.example.NET", Servers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("203.0.113.9")]
    public void UnknownOrMissingAddress_NamesNothing(string? address)
    {
        Assert.Null(ActiveServerMatcher.FindName(address, Servers));
    }
}
