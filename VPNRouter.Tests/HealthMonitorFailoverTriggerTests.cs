using System;
using System.Collections.Generic;
using System.Reflection;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class HealthMonitorFailoverTriggerTests
{
    private sealed class StubScanner : IProcessScanner
    {
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
        using var hm = BuildHm(maxRestarts: 0);
        int raised = 0;
        string? reason = null;
        hm.FailoverRequested += (_, r) => { raised++; reason = r; };

        InvokeAttemptRestart(hm);
        InvokeAttemptRestart(hm);

        Assert.Equal(1, raised);
        Assert.Equal("max restart attempts reached", reason);
    }

    [Fact]
    public void AtCeiling_NoSubscriber_FallsBackToGiveUp_NoThrow()
    {
        using var hm = BuildHm(maxRestarts: 0);
        var ex = Record.Exception(() => InvokeAttemptRestart(hm));
        Assert.Null(ex);
    }

    [Fact]
    public void AtTwoMaxRestarts_ProgressesAttemptsAndTriggersFailover()
    {
        using var hm = BuildHm(maxRestarts: 2);
        int raised = 0;
        hm.FailoverRequested += (_, _) => raised++;

        InvokeAttemptRestart(hm);
        Assert.Equal(0, raised);

        InvokeAttemptRestart(hm);
        Assert.Equal(0, raised);

        InvokeAttemptRestart(hm);
        Assert.Equal(1, raised);
    }
}
