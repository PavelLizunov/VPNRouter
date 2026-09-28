#nullable enable

using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class DnsLockdownPolicyTests
{
    [Theory]
    [InlineData(false, false, false, DnsLockdownAction.None)]
    [InlineData(false, true,  false, DnsLockdownAction.None)]
    [InlineData(false, false, true,  DnsLockdownAction.Disable)]
    [InlineData(false, true,  true,  DnsLockdownAction.Disable)]
    [InlineData(true,  false, false, DnsLockdownAction.None)]
    [InlineData(true,  true,  false, DnsLockdownAction.Enable)]
    [InlineData(true,  false, true,  DnsLockdownAction.Disable)]
    [InlineData(true,  true,  true,  DnsLockdownAction.None)]
    public void Decide_FullTruthTable(bool settingEnabled, bool tunnelServing, bool effective, DnsLockdownAction expected)
    {
        Assert.Equal(expected, DnsLockdownPolicy.Decide(settingEnabled, tunnelServing, effective));
    }

    [Fact]
    public void TunnelDies_WhileArmed_LiftsLockdown()
    {
        var action = DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: false, currentlyEffective: true);
        Assert.Equal(DnsLockdownAction.Disable, action);
    }

    [Fact]
    public void TunnelRecovers_ReArmsLockdown()
    {
        var action = DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: true, currentlyEffective: false);
        Assert.Equal(DnsLockdownAction.Enable, action);
    }

    [Fact]
    public void SteadyServing_IsNoOp()
    {
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: true, currentlyEffective: true));
    }

    [Fact]
    public void SteadyDownAndOpen_IsNoOp()
    {
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: false, currentlyEffective: false));
    }

    [Fact]
    public void SettingOff_WhileArmed_TearsDownOnce()
    {
        Assert.Equal(DnsLockdownAction.Disable,
            DnsLockdownPolicy.Decide(settingEnabled: false, tunnelServing: true, currentlyEffective: true));
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: false, tunnelServing: true, currentlyEffective: false));
    }
}
