using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class WedgeKillPolicyTests
{
    private const int T = 2;

    [Fact]
    public void NeverServed_NeverKills_NoMatterHowLong()
    {
        bool confirmed = false; int streak = 0;
        for (int i = 0; i < 20; i++)
            Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));
        Assert.False(confirmed);
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
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);
        Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));
        Assert.True(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));
    }

    [Fact]
    public void ServingMidStreak_ResetsStreak_NoKill()
    {
        bool confirmed = false; int streak = 0;
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);
        WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T);
        WedgeKillPolicy.ShouldKill(serving: true, ref confirmed, ref streak, T);
        Assert.Equal(0, streak);
        Assert.False(WedgeKillPolicy.ShouldKill(serving: false, ref confirmed, ref streak, T));
    }
}
