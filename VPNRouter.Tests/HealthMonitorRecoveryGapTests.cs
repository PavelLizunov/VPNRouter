using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// HealthMonitor recovery gap (v2.31.5-r2 user-reported VPN-loss bug)
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Pre-fix scenario reproduced from User #1 logs (2026-05-03):
/// <list type="number">
///   <item>sing-box dies with exit code 1073807364 (STATUS_CONTROL_C_EXIT —
///     Windows console event from sleep/wake/logoff/shutdown).
///     <c>OnSingBoxCrashed</c> enables firewall block rules and schedules
///     <c>AttemptRestart</c> via <c>Task.Delay(5000).ContinueWith(...)</c>.</item>
///   <item>The continuation never fires (laptop slept across the 5 s
///     deadline; App quit between schedule and fire; <c>_isStopping</c>
///     was set during a Stop racing the crash event). No log beyond
///     "Restarting sing-box (attempt 1/5) in 5000ms".</item>
///   <item>The periodic health tick is the obvious safety net but its
///     check was <c>!isHealthy &amp;&amp; _vpnWasRunning</c>.
///     <c>_vpnWasRunning</c> had been reset to <c>false</c> by
///     <c>OnSingBoxCrashed</c>, so the check never matched after the crash.
///     User stranded — VPN dead, traffic blocked by firewall, no
///     auto-recovery without manual reconnect.</item>
/// </list>
///
/// <para>Fix: introduce <c>_shouldBeRunning</c> intent flag (set true in
/// <c>Start</c>, false in <c>Stop</c>). <c>OnHealthTick</c> gets a second
/// branch that triggers <c>AttemptRestart</c> whenever sing-box is dead
/// AND user wants VPN up AND we're not stopping.</para>
/// </summary>
public class HealthMonitorRecoveryGapTests
{
    private sealed class StubProcessScanner : VPNRouter.Core.Interfaces.IProcessScanner
    {
        public ScanResult ScanForProfile(Profile profile) => new();
    }

    private sealed class StubFirewallManager : VPNRouter.Core.Interfaces.IFirewallManager
    {
        public void CreateBlockRules(IEnumerable<string> processNames, bool isFullTunnel = true) { }
        public void EnableBlockRules() { }
        public void DisableBlockRules() { }
        public void DeleteAllRules() { }
        public void Dispose() { }
    }

    private static HealthMonitor BuildHm()
    {
        var sbSettings = new SingBoxSettings { ClashApi = "127.0.0.1:65535" };
        var sb = new SingBoxManager(sbSettings);
        var scanner = new StubProcessScanner();
        var fw = new StubFirewallManager();
        var monSettings = new MonitoringSettings
        {
            HealthCheckInterval = 3600,    // 1h — keeps Start's Timer dormant during the test
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        return new HealthMonitor(sb, scanner, fw, monSettings);
    }

    private static T GetField<T>(object obj, string name)
    {
        var f = obj.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (T)f.GetValue(obj)!;
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

    [Fact]
    public void Start_SetsShouldBeRunningTrue()
    {
        var hm = BuildHm();
        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            Assert.True(GetField<bool>(hm, "_shouldBeRunning"));
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void Stop_SetsShouldBeRunningFalse()
    {
        var hm = BuildHm();
        hm.Start(new Profile { Name = "test" }, new AppSettings());
        hm.Stop();
        Assert.False(GetField<bool>(hm, "_shouldBeRunning"));
        hm.Dispose();
    }

    [Fact]
    public void OnHealthTick_AfterCrash_TriggersRecoveryRestartAttempt()
    {
        // Reproduces the exact post-OnSingBoxCrashed state from User #1
        // log line 290-292:
        //   - sing-box never started in this test → IsHealthy() = false
        //   - _vpnWasRunning forced to false (OnSingBoxCrashed sets this)
        //   - _shouldBeRunning = true (Start sets this)
        //   - _isStopping = false (Stop not called)
        //
        // Pre-fix the OnHealthTick branch `!isHealthy && _vpnWasRunning`
        // didn't match because _vpnWasRunning had been reset. The new
        // branch `!isHealthy && _shouldBeRunning && !_isStopping` matches
        // and fires AttemptRestart, which raises RestartAttempted=1
        // synchronously.
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            SetField(hm, "_vpnWasRunning", false);

            InvokeOnHealthTick(hm);

            Assert.Equal(1, attempts);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    [Fact]
    public void OnHealthTick_AfterUserStop_DoesNotTriggerRecovery()
    {
        // User explicitly disconnected → Stop() was called → _shouldBeRunning
        // false. Even though sing-box is dead and _isStopping has been
        // reset by Stop's caller (or never re-armed), we must NOT
        // auto-restart — the user opted out.
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        hm.Start(new Profile { Name = "test" }, new AppSettings());
        hm.Stop();
        SetField(hm, "_vpnWasRunning", false);

        InvokeOnHealthTick(hm);

        Assert.Equal(0, attempts);
        hm.Dispose();
    }

    [Fact]
    public void OnHealthTick_OriginalBranch_RestartsAfterTwoConsecutiveFailures()
    {
        // Realtime UDP games drop on any TUN bounce. A single failed health
        // probe can be transient, so the original "sing-box stuck but alive"
        // path waits for two consecutive probe failures before restarting.
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            SetField(hm, "_vpnWasRunning", true);

            InvokeOnHealthTick(hm);
            Assert.Equal(0, attempts);

            InvokeOnHealthTick(hm);

            Assert.Equal(1, attempts);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    /// <summary>
    /// v2.31.6-r10 (Phase D): ProbeNow runs the same OnHealthTick body
    /// directly. Verify the public method exists, dispatches to the
    /// recovery branch when armed, and is a no-op when stopped.
    /// </summary>
    [Fact]
    public void ProbeNow_AfterStart_RunsOnHealthTickRecoveryBranch()
    {
        // Same setup as OnHealthTick_AfterCrash_TriggersRecoveryRestartAttempt
        // but calls the new public ProbeNow instead of the private
        // OnHealthTick. Equivalent firing path with the wake-event
        // entrypoint.
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        try
        {
            hm.Start(new Profile { Name = "test" }, new AppSettings());
            SetField(hm, "_vpnWasRunning", false);

            hm.ProbeNow();

            Assert.Equal(1, attempts);
        }
        finally { hm.Stop(); hm.Dispose(); }
    }

    /// <summary>
    /// v2.31.6-r10 (Phase D): ProbeNow after Stop is a no-op — the
    /// _isStopping guard at the top of ProbeNow short-circuits the
    /// dispatch so a SystemEvents callback racing teardown can't run
    /// the body against half-disposed state.
    /// </summary>
    [Fact]
    public void ProbeNow_AfterStop_IsNoOp()
    {
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        hm.Start(new Profile { Name = "test" }, new AppSettings());
        hm.Stop();

        // Force a state that WOULD trigger the recovery branch if
        // ProbeNow ran the body — and verify it doesn't.
        SetField(hm, "_vpnWasRunning", false);
        // _isStopping is true (set by Stop), so ProbeNow's early-return
        // guard fires before reaching OnHealthTick.

        hm.ProbeNow();

        Assert.Equal(0, attempts);
        hm.Dispose();
    }

    [Fact]
    public void Source_AttemptRestart_DoesNotHotReloadOrphanSingBox()
    {
        var src = File.ReadAllText(FindRepoFile("VPNRouter.Core", "Services", "HealthMonitor.cs"));

        Assert.Contains("var managedSingBoxRunning = _singBox.IsRunning();", src);
        Assert.Contains("OrphanCleanup.KillOrphans(_logger, respectTunLock: false)", src);
        Assert.Contains("var hotReloaded = managedSingBoxRunning && TryHotReloadViaApi(configJson);", src);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
