#nullable enable

using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class StrictDnsFailoverPolicyTests
{
    [Theory]
    [InlineData(false, false, false, StrictDnsAction.None)]
    [InlineData(false, true,  false, StrictDnsAction.None)]
    [InlineData(false, false, true,  StrictDnsAction.ReArm)]
    [InlineData(false, true,  true,  StrictDnsAction.ReArm)]
    [InlineData(true,  true,  false, StrictDnsAction.None)]
    [InlineData(true,  false, false, StrictDnsAction.FailOpen)]
    [InlineData(true,  true,  true,  StrictDnsAction.ReArm)]
    [InlineData(true,  false, true,  StrictDnsAction.None)]
    public void Decide_FullTruthTable(bool soleDriver, bool proxyHealthy, bool failedOver, StrictDnsAction expected)
    {
        Assert.Equal(expected, StrictDnsFailoverPolicy.Decide(soleDriver, proxyHealthy, failedOver));
    }
}
