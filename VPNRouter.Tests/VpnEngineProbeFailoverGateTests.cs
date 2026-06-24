using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.44.2 (P0) — pins <see cref="VpnEngine.ShouldAutoFailoverAfterProbe"/>,
/// the post-start probe failover gate that fixes the false-positive teardown
/// of a WORKING connection (diag 20260624-235243).
///
/// <para>Regression: in v2.44.0/.1 the post-start Clash delay-test probe
/// (<see cref="ConfigSanityCheck.ProbeAsync"/>) triggered
/// <see cref="AutoFailoverEngine"/> whenever it returned dead — commonly a
/// Clash-API HTTP 503 "An error occurred in the delay test" (a known
/// sing-box urltest-group / transient quirk) — even though the TUN warmup
/// probe had already fetched gstatic THROUGH the tunnel ~13s earlier,
/// proving the outbound reachable. AutoFailover then tore down the user's
/// working server and (via a second bug) failed to restart the replacement.
/// The gate now suppresses failover once warmup has confirmed connectivity;
/// the periodic HealthMonitor still covers genuine sing-box crashes.</para>
/// </summary>
public sealed class VpnEngineProbeFailoverGateTests
{
    [Fact]
    public void DeadProbe_NoWarmupConfirm_FailsOver()
        => Assert.True(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: false));

    // THE regression fix: warmup already proved the tunnel works, so a later
    // dead delay-test must NOT fail over a working connection.
    [Fact]
    public void DeadProbe_WarmupConfirmed_DoesNotFailOver()
        => Assert.False(VpnEngine.ShouldAutoFailoverAfterProbe(
            probeIsDead: true, probeCancelled: false, warmupConfirmed: true));

    // Stop() cancels the probe token mid-flight — never fail over on a
    // disconnect race ("ghost failover after manual disconnect").
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
