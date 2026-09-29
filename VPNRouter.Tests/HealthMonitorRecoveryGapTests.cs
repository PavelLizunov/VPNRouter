using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

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
            HealthCheckInterval = 3600,
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

    [Fact]
    public void ProbeNow_AfterStart_RunsOnHealthTickRecoveryBranch()
    {
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

    [Fact]
    public void ProbeNow_AfterStop_IsNoOp()
    {
        var hm = BuildHm();
        var attempts = 0;
        hm.RestartAttempted += (_, n) => attempts = n;

        hm.Start(new Profile { Name = "test" }, new AppSettings());
        hm.Stop();

        SetField(hm, "_vpnWasRunning", false);

        hm.ProbeNow();

        Assert.Equal(0, attempts);
        hm.Dispose();
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
