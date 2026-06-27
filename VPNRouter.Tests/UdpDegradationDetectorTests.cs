using System;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// RB4 (2026-06-27): the UDP-path failover decision. Storm-safe by construction —
/// fires only on a fully-dead UDP path (timeouts ≥ threshold AND zero successes)
/// and at most once per cooldown.
/// </summary>
public class UdpDegradationDetectorTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BelowThreshold_DoesNotFire()
    {
        var d = new UdpDegradationDetector(minTimeouts: 30);
        Assert.False(d.ShouldFailover(udpTimeouts: 29, udpSuccesses: 0, T0));
    }

    [Fact]
    public void ThresholdMet_ButSomeSuccess_DoesNotFire()
    {
        // Any UDP success => path is flaky, not dead. Never churn a working session.
        var d = new UdpDegradationDetector(minTimeouts: 30);
        Assert.False(d.ShouldFailover(udpTimeouts: 100, udpSuccesses: 1, T0));
    }

    [Fact]
    public void FullyDead_Fires()
    {
        var d = new UdpDegradationDetector(minTimeouts: 30);
        Assert.True(d.ShouldFailover(udpTimeouts: 30, udpSuccesses: 0, T0));
    }

    [Fact]
    public void WithinCooldown_DoesNotFireAgain()
    {
        var d = new UdpDegradationDetector(minTimeouts: 30, cooldown: TimeSpan.FromMinutes(10));
        Assert.True(d.ShouldFailover(50, 0, T0));
        // 9 minutes later, still dead — but cooldown blocks a second failover (storm guard).
        Assert.False(d.ShouldFailover(50, 0, T0.AddMinutes(9)));
    }

    [Fact]
    public void AfterCooldown_FiresAgain()
    {
        var d = new UdpDegradationDetector(minTimeouts: 30, cooldown: TimeSpan.FromMinutes(10));
        Assert.True(d.ShouldFailover(50, 0, T0));
        Assert.True(d.ShouldFailover(50, 0, T0.AddMinutes(11)));
    }

    [Fact]
    public void Recovery_BetweenFires_StillRespectsCooldown()
    {
        // dead -> fire; then a healthy window (success) -> no fire; then dead again
        // within cooldown -> still blocked. Cooldown is wall-clock, not event-gated.
        var d = new UdpDegradationDetector(minTimeouts: 30, cooldown: TimeSpan.FromMinutes(10));
        Assert.True(d.ShouldFailover(50, 0, T0));
        Assert.False(d.ShouldFailover(0, 200, T0.AddMinutes(2)));   // healthy
        Assert.False(d.ShouldFailover(50, 0, T0.AddMinutes(5)));    // dead again, cooldown
    }
}
