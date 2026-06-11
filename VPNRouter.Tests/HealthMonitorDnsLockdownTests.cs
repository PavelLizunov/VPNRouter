using System;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// HealthMonitor → DnsLeakLockdown "Auto" (fail-open) wiring (v2.42.0)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pins that the HealthMonitor drives <see cref="IWindowsDnsHardening.ReconcileLockdownForHealth"/>
/// with the live tunnel-serving signal so "Block DNS outside VPN" fails OPEN the
/// moment the tunnel stops routing (sing-box crash + restart backoff, dead/slow
/// server) and is RE-ARMED only once the tunnel is confirmed serving again.
///
/// <para>This is the wiring half of the v2.42.0 fix; the decision matrix itself
/// is in <see cref="DnsLockdownPolicyTests"/>. Background: the surito/germany
/// diagnostics (2026-06-11) where the lockdown stayed pinned for the whole
/// session and stranded the user offline ("no internet / endless loading") when
/// the tunnel died mid-session.</para>
///
/// <para>Both seams are exercised against a HealthMonitor whose sing-box never
/// starts (so <c>IsHealthy()</c> is false) and whose Clash API points at a dead
/// port (so <c>ClashApiResponds()</c> is false) — i.e. "tunnel not serving" — and
/// a <see cref="NullWindowsDnsHardening"/> capture stub injected via the new
/// optional ctor seam.</para>
/// </summary>
public class HealthMonitorDnsLockdownTests
{
    private sealed class StubProcessScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(System.Collections.Generic.IEnumerable<string> processNames) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static HealthMonitor BuildHm(NullWindowsDnsHardening dns)
    {
        // Dead Clash port → ClashApiResponds() false; sing-box never launched
        // → IsHealthy() false. Together: "tunnel not serving".
        var sb = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:65535" });
        var monSettings = new MonitoringSettings
        {
            HealthCheckInterval = 3600,    // keep the periodic timer dormant during the test
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
            // _vpnWasRunning false avoids the original-branch AttemptRestart noise;
            // _shouldBeRunning recovery branch still fires but that's orthogonal.
            SetField(hm, "_vpnWasRunning", false);

            InvokeOnHealthTick(hm);

            // The lockdown reconcile fired, and because the tunnel is NOT serving
            // (sing-box dead) it was driven with serving=false → fail open.
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

            // Feature off → zero cost, the reconciler is never even consulted from
            // the tick (the netsh side would no-op too, but we don't want the
            // extra Clash probe / call when the user hasn't opted in).
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

            // The crash hook lifts the lockdown immediately (serving=false) rather
            // than waiting for the next periodic tick, so the user isn't stranded
            // during the crash → restart-backoff window.
            Assert.Contains(dns.ReconcileCalls, c => c.TunnelServing == false);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }
}
