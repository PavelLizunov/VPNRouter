using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnEngineProbeFailoverGateTests
{
    [Fact]
    public void DeadProbe_NoWarmupConfirm_FailsOver()
        => Assert.True(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: false));
}
