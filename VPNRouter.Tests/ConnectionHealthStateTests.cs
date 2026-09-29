#nullable enable
using System;
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ConnectionHealthStateTests
{
    private static ConnLogEvent Ev(ConnHealthCategory c) => new(c, null, "proxy", null, null);

    private static ConnectionHealthState NewState(Func<DateTimeOffset> clock, int minSample = 20)
        => new(window: TimeSpan.FromMinutes(5), minSample: minSample, warnThreshold: 0.5, clock: clock);

    [Fact]
    public void CountsPerCategory()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        s.Record(Ev(ConnHealthCategory.ProxyStreamError));
        s.Record(Ev(ConnHealthCategory.LocalClose));
        s.Record(Ev(ConnHealthCategory.Other));

        var snap = s.Snapshot();
        Assert.Equal(2, snap.RelayOpenAttempts);
        Assert.Equal(1, snap.RelayOpenFails);
        Assert.Equal(1, snap.ProxyStreamErrors);
        Assert.Equal(1, snap.LocalCloses);
        Assert.Equal(1, snap.Other);
    }

    [Fact]
    public void FailureRate_IsFailsOverAttempts()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        for (int i = 0; i < 4; i++) s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(0.25, s.Snapshot().FailureRate, 3);
    }

    [Fact]
    public void FailureRate_NoAttempts_IsZero()
        => Assert.Equal(0.0, NewState(() => DateTimeOffset.UnixEpoch).Snapshot().FailureRate, 3);

    [Fact]
    public void FailureRate_ClampedToOne_WhenFailsExceedAttempts()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        for (int i = 0; i < 3; i++) s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        Assert.Equal(1.0, s.Snapshot().FailureRate, 3);
    }

    [Fact]
    public void LocalCloseFlood_DoesNotWarn()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        for (int i = 0; i < 1000; i++) s.Record(Ev(ConnHealthCategory.LocalClose));
        for (int i = 0; i < 5; i++) s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        var snap = s.Snapshot();
        Assert.Equal(0.0, snap.FailureRate, 3);
        Assert.False(snap.WouldWarn);
    }

    [Fact]
    public void HighFailureRate_AboveMinSample_WouldWarn()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        for (int i = 0; i < 30; i++) s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        for (int i = 0; i < 25; i++) s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        Assert.True(s.Snapshot().WouldWarn);
    }

    [Fact]
    public void HighFailureRate_BelowMinSample_DoesNotWarn()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        for (int i = 0; i < 5; i++) s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        for (int i = 0; i < 5; i++) s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        Assert.False(s.Snapshot().WouldWarn);
    }

    [Fact]
    public void EventsOutsideWindow_ArePruned()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        for (int i = 0; i < 30; i++) s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        now = now.AddMinutes(6);
        var snap = s.Snapshot();
        Assert.Equal(0, snap.RelayOpenFails);
        Assert.False(snap.WouldWarn);
    }

    [Fact]
    public void PerNode_AttributesSeparately()
    {
        var now = DateTimeOffset.UnixEpoch;
        var s = NewState(() => now);
        s.SetActiveNode("node-A");
        s.Record(Ev(ConnHealthCategory.RelayOpenFail));
        s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));
        s.SetActiveNode("node-B");
        s.Record(Ev(ConnHealthCategory.RelayOpenAttempt));

        var byNode = s.SnapshotByNode();
        Assert.Equal(2, byNode.Count);
        Assert.Equal(1, byNode.Single(n => n.Node == "node-A").RelayOpenFails);
        Assert.Equal(0, byNode.Single(n => n.Node == "node-B").RelayOpenFails);
        Assert.Equal(1, byNode.Single(n => n.Node == "node-B").RelayOpenAttempts);
    }
}
