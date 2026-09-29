using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;
public class FirewallManagerLocalizedNetshTests
{
    [Fact]
    public void FindRulesByPrefixes_MatchesMultiplePrefixesInSinglePass()
    {
        var simulated = @"
Rule Name:                            VPNRouter_Block_Discord
Description:                          Block rule

Rule Name:                            VPNRouter_Allow_Direct
Description:                          Allow rule

Rule Name:                            VPNRouter-DnsLockdown-Rule1
Description:                          DNS rule

Rule Name:                            UnrelatedRule
Description:                          Ignore me
".Replace("\r\n", "\n");

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "netsh.exe",
            new ProcessResult(0, simulated, "", TimeSpan.FromMilliseconds(10), false));

        var sut = new FirewallManager(logger: null, runner: fake);
        var prefixes = new[] { "VPNRouter_Block_", "VPNRouter_Allow_", "VPNRouter-DnsLockdown-" };

        var matches = sut.FindRulesByPrefixes(prefixes);

        Assert.Equal(3, matches.Count);
        Assert.Contains("VPNRouter_Block_Discord", matches);
        Assert.Contains("VPNRouter_Allow_Direct", matches);
        Assert.Contains("VPNRouter-DnsLockdown-Rule1", matches);
        Assert.DoesNotContain("UnrelatedRule", matches);
        Assert.Single(fake.RunCalls);
    }
}
