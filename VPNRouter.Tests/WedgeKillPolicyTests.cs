using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// W0.1 (true-split): the wedge-kill decision (latch + streak). A Windows hang = sing-box
/// alive but Clash API not serving for N consecutive ticks, but ONLY after serving was
/// ever confirmed (the latch avoids false kills during TUN warm-up / a non-default
/// clash_api port). Pure logic — the HealthMonitor wiring is left untested by design
/// (mirrors DnsLockdownPolicy / DnsLockdownPolicyTests).
/// </summary>
public class WedgeKillPolicyTests
{
    private const int T = 2;

    [Fact]
    public void NeverServed_NeverKills_NoMatterHowLong()
    {
        bool confirmed = false; int streak = 0;
        for (int i = 0; i < 20; i++)
            Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));
        Assert.False(confirmed);   // latch never armed
    }

    [Fact]
    public void Serving_ArmsLatch_AndResetsStreak()
    {
        bool confirmed = false; int streak = 5;
        Assert.False(WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T));
        Assert.True(confirmed);
        Assert.Equal(0, streak);
    }

    [Fact]
    public void AfterServing_ThresholdConsecutiveNotServing_Kills()
    {
        bool confirmed = false; int streak = 0;
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);           // arm
        Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T)); // 1st
        Assert.True(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));  // 2nd → kill
    }

    [Fact]
    public void ServingMidStreak_ResetsStreak_NoKill()
    {
        bool confirmed = false; int streak = 0;
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);   // arm
        WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T);  // streak 1
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);   // recovered → reset
        Assert.Equal(0, streak);
        Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T)); // back to 1, no kill
    }
}
