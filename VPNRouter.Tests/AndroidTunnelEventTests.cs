using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class AndroidTunnelEventTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task LatePreparationCannotDispatchOrPaintAfterAProfileChange(bool fail, bool connectB, bool live)
    {
        var a = new AndroidTunnelEvent("A", "state-1", live ? "existing-tun" : null);
        var current = a;
        var feedbackCommand = "A";
        var ui = "Connecting A";
        var dispatched = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> PrepareA()
        {
            await release.Task;
            return a.CompletePreparation(() => current, owner =>
            {
                if (fail) owner.PaintIfCurrent(() => current, () => ui = "A error");
                else dispatched.Add("fixture-profile-A");
            });
        }
        var late = PrepareA();
        var imported = a with { SelectionSuperseded = true };
        current = imported;
        if (connectB)
        {
            current = a with { Command = "B" };
            feedbackCommand = "B";
            ui = "Connecting B";
        }
        // Delayed import notification and an already queued error use the event they observed.
        bool reconciled = imported.ReconcileIfCurrent(() => current, feedbackCommand,
            () => ui = live ? "Connected" : "Off");
        Assert.Equal(!connectB, reconciled);
        a.PaintIfCurrent(() => current, () => ui = "A error");
        release.SetResult();
        Assert.False(await late);
        Assert.Empty(dispatched);
        Assert.Equal(connectB ? "Connecting B" : live ? "Connected" : "Off", ui);
        if (connectB)
        {
            var originB = current;
            current = current with { StateEvent = "prior-tun-down", Session = null };
            Assert.True(originB.CompletePreparation(() => current, _ => dispatched.Add("fixture-profile-B")));
            Assert.Equal(new[] { "fixture-profile-B" }, dispatched);
        }
    }

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
