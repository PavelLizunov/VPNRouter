using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class FirewallManagerTunAllowTests
{
    [Theory]
    [InlineData("172.19.0.1/30",   "172.19.0.0/30")]
    [InlineData("172.19.0.0/30",   "172.19.0.0/30")]
    [InlineData("172.19.0.2/30",   "172.19.0.0/30")]
    [InlineData("172.20.0.1/24",   "172.20.0.0/24")]
    [InlineData("10.8.0.1/24",     "10.8.0.0/24")]
    [InlineData("192.168.5.5/16",  "192.168.0.0/16")]
    public void NormalizeTunAllowIp_ProducesNetworkCidr(string input, string expected)
    {
        var result = FirewallManager.NormalizeTunAllowIp(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("172.19.0.1/30", "0.0.0.0-172.18.255.255,172.19.0.4-255.255.255.255")]
    [InlineData("10.8.0.1/24",   "0.0.0.0-10.7.255.255,10.8.1.0-255.255.255.255")]
    [InlineData("192.168.5.5/16", "0.0.0.0-192.167.255.255,192.169.0.0-255.255.255.255")]
    public void ComputeBlockExclusionRange_ProducesCorrectComplement(string tunCidr, string expected)
    {
        var result = FirewallManager.ComputeBlockExclusionRange(tunCidr);
        Assert.Equal(expected, result);
    }
}
