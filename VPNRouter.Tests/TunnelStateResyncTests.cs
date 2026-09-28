#nullable enable
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class TunnelStateResyncTests
{
    [Theory]
    [InlineData(true,  false, false, true,  false)]
    [InlineData(true,  false, true,  true,  false)]
    [InlineData(true,  true,  false, true,  false)]
    [InlineData(true,  true,  true,  false, true)]
    [InlineData(false, true,  true,  false, false)]
    [InlineData(false, true,  false, false, false)]
    [InlineData(false, false, false, false, false)]
    public void Resolve(bool intended, bool live, bool vpnActive, bool expectAct, bool expectCorrected)
    {
        var act = TunnelStateResync.TryResolveOnResume(intended, live, vpnActive, out var corrected);
        Assert.Equal(expectAct, act);
        Assert.Equal(expectCorrected, corrected);
    }

    [Fact]
    public void SilentTunDeath_IsTheRegressionGuard()
    {
        var act = TunnelStateResync.TryResolveOnResume(
            intendedConnected: true, serviceTunnelLive: true, vpnTransportActive: false, out var corrected);
        Assert.True(act);
        Assert.False(corrected);
    }
}
