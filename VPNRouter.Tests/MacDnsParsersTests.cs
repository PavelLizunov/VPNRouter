using VPNRouter.Core.Platform.Unix;
using Xunit;

namespace VPNRouter.Tests;

public class MacDnsParsersTests
{
    [Theory]
    [InlineData("172.19.0.1/30", "172.19.0.1")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("  192.168.255.254/24  ", "192.168.255.254")]
    public void DeriveDnsTarget_strips_prefix(string cidr, string expected)
    {
        Assert.Equal(expected, MacDnsParsers.DeriveDnsTarget(cidr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.999")]
    public void DeriveDnsTarget_rejects_malformed(string? cidr)
    {
        Assert.Null(MacDnsParsers.DeriveDnsTarget(cidr));
    }

    [Fact]
    public void ParseGetDnsServers_empty_when_none_set()
    {
        var result = MacDnsParsers.ParseGetDnsServers("There aren't any DNS Servers set on Wi-Fi.");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseGetDnsServers_reads_ip_list()
    {
        var result = MacDnsParsers.ParseGetDnsServers("8.8.8.8\n1.1.1.1\n");
        Assert.Equal(new[] { "8.8.8.8", "1.1.1.1" }, result);
    }

    [Fact]
    public void ParseGetDnsServers_skips_prose_keeps_ipv6()
    {
        var result = MacDnsParsers.ParseGetDnsServers("2001:4860:4860::8888\nsome noise line\n9.9.9.9");
        Assert.Equal(new[] { "2001:4860:4860::8888", "9.9.9.9" }, result);
    }

    [Fact]
    public void ParseGetDnsServers_null_is_empty()
    {
        Assert.Empty(MacDnsParsers.ParseGetDnsServers(null));
    }

    [Fact]
    public void ParseDefaultRouteDevice_reads_interface()
    {
        const string routeOut =
            "   route to: default\n" +
            "destination: default\n" +
            "       mask: default\n" +
            "    gateway: 192.168.0.1\n" +
            "  interface: en0\n" +
            "      flags: <UP,GATEWAY,DONE,STATIC>\n";
        Assert.Equal("en0", MacDnsParsers.ParseDefaultRouteDevice(routeOut));
    }

    [Fact]
    public void ParseDefaultRouteDevice_null_when_no_default_route()
    {
        Assert.Null(MacDnsParsers.ParseDefaultRouteDevice("destination: default\n   gateway: 1.2.3.4\n"));
    }

    private const string ListOrder =
        "An asterisk (*) denotes that a network service is disabled.\n" +
        "(1) Ethernet\n" +
        "(Hardware Port: Ethernet, Device: en1)\n" +
        "\n" +
        "(2) Wi-Fi\n" +
        "(Hardware Port: Wi-Fi, Device: en0)\n" +
        "\n" +
        "(3) iPhone USB\n" +
        "(Hardware Port: iPhone USB, Device: en5)\n";

    [Theory]
    [InlineData("en0", "Wi-Fi")]
    [InlineData("en1", "Ethernet")]
    [InlineData("en5", "iPhone USB")]
    public void ParseServiceForDevice_maps_device_to_service(string device, string expected)
    {
        Assert.Equal(expected, MacDnsParsers.ParseServiceForDevice(ListOrder, device));
    }

    [Theory]
    [InlineData("en99")]
    [InlineData(null)]
    [InlineData("")]
    public void ParseServiceForDevice_null_when_absent(string? device)
    {
        Assert.Null(MacDnsParsers.ParseServiceForDevice(ListOrder, device));
    }
}
