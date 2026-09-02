using System;
using System.Collections.Generic;
using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// G4 (2026-06-27): when sing-box restarts hit the ceiling, HealthMonitor raises
/// <c>FailoverRequested</c> ONCE so the engine can swap to a healthy server
/// instead of silently giving up (the dial i/o-timeout restart-storm in user
/// diags). <see cref="MonitoringSettings.MaxRestartAttempts"/> = 0 makes the very
/// first AttemptRestart hit the ceiling, so the restart path (which writes
/// ProgramData) never runs — keeping the test hermetic.
/// </summary>
public class HealthMonitorFailoverTriggerTests
{
    private sealed class StubScanner : IProcessScanner
    {
        // Never reached at Max=0 (ceiling returns before the restart path).
        public ScanResult ScanForProfile(Profile profile) => throw new NotImplementedException();
    }

    private sealed class StubFirewall : IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static HealthMonitor BuildHm(int maxRestarts)
    {
        var exe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hm-fo-{Guid.NewGuid():N}.exe");
        var sb = new SingBoxManager(new SingBoxSettings { ExecutablePath = exe, ClashApi = "127.0.0.1:9090" });
        return new HealthMonitor(sb, new StubScanner(), new StubFirewall(),
            new MonitoringSettings { HealthCheckInterval = 3600, MaxRestartAttempts = maxRestarts, RestartOnFailure = true });
    }

    private static void InvokeAttemptRestart(HealthMonitor hm)
        => typeof(HealthMonitor)
            .GetMethod("AttemptRestart", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hm, null);

    [Fact]
    public void AtCeiling_WithSubscriber_RaisesFailoverRequestedOnce()
    {
        using var hm = BuildHm(maxRestarts: 0); // ceiling on the first AttemptRestart
        int raised = 0;
        string? reason = null;
        hm.FailoverRequested += (_, r) => { raised++; reason = r; };

        InvokeAttemptRestart(hm);
        InvokeAttemptRestart(hm); // latched — must NOT re-raise

        Assert.Equal(1, raised);
        Assert.Equal("max restart attempts reached", reason);
    }

    [Fact]
    public void AtCeiling_NoSubscriber_FallsBackToGiveUp_NoThrow()
    {
        using var hm = BuildHm(maxRestarts: 0);
        // No FailoverRequested subscriber → original "give up" path; must not throw.
        var ex = Record.Exception(() => InvokeAttemptRestart(hm));
        Assert.Null(ex);
    }

    [Fact]
    public void AtTwoMaxRestarts_ProgressesAttemptsAndTriggersFailover()
    {
        // SEC-1.3-01: verify that when MaxRestartAttempts = 2, the counter increments
        // across attempts rather than being prematurely reset, properly reaching the ceiling.
        using var hm = BuildHm(maxRestarts: 2);
        int raised = 0;
        hm.FailoverRequested += (_, _) => raised++;

        InvokeAttemptRestart(hm); // attempt 1 (counter: 0 -> 1)
        Assert.Equal(0, raised);

        InvokeAttemptRestart(hm); // attempt 2 (counter: 1 -> 2)
        Assert.Equal(0, raised);

        InvokeAttemptRestart(hm); // attempt 3 (counter: 2 >= 2 -> ceiling reached!)
        Assert.Equal(1, raised);
    }
}
