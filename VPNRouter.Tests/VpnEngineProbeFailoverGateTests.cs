using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnEngineProbeFailoverGateTests
{
    [Fact]
    public void DeadProbe_NoWarmupConfirm_FailsOver()
        => Assert.True(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: false));

    [Fact]
    public void DeadProbe_WarmupConfirmed_DoesNotFailOver()
        => Assert.False(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: true));

    [Fact]
    public void DeadProbe_Cancelled_DoesNotFailOver()
        => Assert.False(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: true, warmupConfirmed: false));

    [Fact]
    public void HealthyProbe_DoesNotFailOver()
        => Assert.False(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: false, probeCancelled: false, warmupConfirmed: false));

    [Fact]
    public void HealthyProbe_WarmupConfirmed_DoesNotFailOver()
        => Assert.False(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: false, probeCancelled: false, warmupConfirmed: true));
}
