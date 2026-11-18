using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AndroidTunnelEventTests
{
    [Fact]
    public void DelayedStatusErrorAndTrafficPaintsKeepTheirOriginalCommandAndState()
    {
        var first = new AndroidTunnelEvent("connect", "connected", "first-tunnel");
        var rejected = new AndroidTunnelEvent("switch", "connected", "first-tunnel");
        var replacement = new AndroidTunnelEvent("switch", "connected-again", "replacement-tunnel");
        var current = first;
        var painted = new List<string>();
        Action Queue(AndroidTunnelEvent origin, string value) =>
            () => origin.PaintIfCurrent(() => current, () => painted.Add(value));

        var oldStats = Queue(first, "old rate");
        var oldError = Queue(rejected, "old error");
        var oldStatus = Queue(rejected with { StateEvent = "connected-again" }, "wrong session");
        var freshStatus = Queue(replacement, "connected");
        current = rejected;
        oldStats();
        current = replacement;
        oldError();
        oldStatus();
        freshStatus();
        Assert.Equal(new[] { "connected" }, painted);
    }

    [Fact]
    public void DuplicateStatusAndRejectedSwitchPreserveRatesAndReplacementStartsANewBaseline()
    {
        var traffic = new AndroidTunnelTraffic();
        Assert.True(traffic.SetSession("first"));
        Assert.Null(traffic.Measure("first", 1000, 200, 100));
        Assert.False(traffic.SetSession("first"));
        Assert.Equal((100d, 50d), traffic.Measure("first", 3000, 400, 200));
        Assert.Null(traffic.Measure("first", 2500, 5000, 5000));
        Assert.Equal((100d, 50d), traffic.Measure("first", 5000, 600, 300));

        Assert.True(traffic.SetSession("second"));
        Assert.Null(traffic.Measure("first", 7000, 9000, 9000));
        Assert.Null(traffic.Measure("second", 6000, 0, 0));
        Assert.Equal((20d, 5d), traffic.Measure("second", 8000, 40, 10));
    }
}
