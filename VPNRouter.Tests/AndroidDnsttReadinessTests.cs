using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Tests;

public sealed class AndroidDnsttReadinessTests
{
    [Theory]
    [InlineData("DNSTT readiness: command=test-requested outbounds=proxy-pending positiveCallback=false elapsedMs=25001")]
    [InlineData("DNSTT readiness: command=test-requested outbounds=proxy-available positiveCallback=true elapsedMs=2062")]
    public void AcceptsTheNativeReadinessSummary(string summary) =>
        Assert.Equal(summary, AndroidDnsttReadiness.Validated(summary));

    [Theory]
    [InlineData(null)]
    [InlineData("DNSTT readiness: command=example.invalid outbounds=proxy-pending positiveCallback=false elapsedMs=25001")]
    [InlineData("DNSTT readiness: command=test-requested outbounds=proxy-pending positiveCallback=false elapsedMs=25001\nextra")]
    public void RejectsUnexpectedData(string? summary) =>
        Assert.Null(AndroidDnsttReadiness.Validated(summary));
}
