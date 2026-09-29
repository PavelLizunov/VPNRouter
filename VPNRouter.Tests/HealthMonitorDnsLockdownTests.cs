using System;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public class HealthMonitorDnsLockdownTests
{
    private sealed class StubProcessScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(System.Collections.Generic.IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static HealthMonitor BuildHm(NullWindowsDnsHardening dns)
    {
        var sb = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:65535" });
        var monSettings = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        return new HealthMonitor(sb, new StubProcessScanner(), new StubFirewallManager(),
            monSettings, dnsHardening: dns);
    }

    private static AppSettings SettingsWithLockdown(bool enabled)
    {
        var s = new AppSettings();
        s.App.DnsLeakLockdown = enabled;
        return s;
    }

    private static void SetField(object obj, string name, object value)
    {
        var f = obj.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        f.SetValue(obj, value);
    }

    private static void InvokeOnHealthTick(HealthMonitor hm)
    {
        var m = hm.GetType().GetMethod("OnHealthTick",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(hm, new object?[] { null });
    }

    private static void InvokeOnSingBoxCrashed(HealthMonitor hm)
    {
        var m = hm.GetType().GetMethod("OnSingBoxCrashed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(hm, new object?[] { null, EventArgs.Empty });
    }

    [Fact]
    public void HealthTick_WithLockdownOn_TunnelNotServing_ReconcilesFailOpen()
    {
        var dns = new NullWindowsDnsHardening();
        var hm = BuildHm(dns);
        try
        {
            hm.Start(new Profile { Name = "test" }, SettingsWithLockdown(true));
            SetField(hm, "_vpnWasRunning", false);

            InvokeOnHealthTick(hm);

            Assert.True(dns.ReconcileCount >= 1);
            Assert.Equal(false, dns.LastReconcileServing);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void HealthTick_WithLockdownOff_DoesNotReconcile()
    {
        var dns = new NullWindowsDnsHardening();
        var hm = BuildHm(dns);
        try
        {
            hm.Start(new Profile { Name = "test" }, SettingsWithLockdown(false));
            SetField(hm, "_vpnWasRunning", false);

            InvokeOnHealthTick(hm);

            Assert.Equal(0, dns.ReconcileCount);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void SingBoxCrash_LiftsLockdownImmediately()
    {
        var dns = new NullWindowsDnsHardening();
        var hm = BuildHm(dns);
        try
        {
            hm.Start(new Profile { Name = "test" }, SettingsWithLockdown(true));

            InvokeOnSingBoxCrashed(hm);

            Assert.Contains(dns.ReconcileCalls, c => c.TunnelServing == false);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }
}
