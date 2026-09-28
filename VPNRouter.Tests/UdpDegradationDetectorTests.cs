using System;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
        var d = new UdpDegradationDetector(minTimeouts: 30, cooldown: TimeSpan.FromMinutes(10));
        Assert.True(d.ShouldFailover(50, 0, T0));
        Assert.False(d.ShouldFailover(0, 200, T0.AddMinutes(2)));
        Assert.False(d.ShouldFailover(50, 0, T0.AddMinutes(5)));
    }
}
