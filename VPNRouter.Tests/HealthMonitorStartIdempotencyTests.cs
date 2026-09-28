#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthMonitorStartIdempotencyTests
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
        var sb = new SingBoxManager(new SingBoxSettings { ClashApi = "127.0.0.1:65535" });
        var mon = new MonitoringSettings
        {
            HealthCheckInterval = 3600,
            MaxRestartAttempts = 5,
            RestartOnFailure = true,
        };
        return new HealthMonitor(sb, new StubProcessScanner(), new StubFirewallManager(), mon);
    }

    private static T GetField<T>(object obj, string name)
    {
        var f = obj.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)f.GetValue(obj)!;
    }

    [Fact]
    public void DoubleStart_TearsDownPriorRun_NotOrphaned()
    {
        var hm = BuildHm();
        try
        {
            hm.Start(new Profile { Name = "idempotency-test" }, new AppSettings());
            var firstListener = GetField<object?>(hm, "_powerListener");
            var firstTimer = GetField<object?>(hm, "_healthTimer");
            Assert.NotNull(firstListener);
            Assert.NotNull(firstTimer);

            hm.Start(new Profile { Name = "idempotency-test" }, new AppSettings());

            Assert.True(GetField<bool>(firstListener!, "_disposed"),
                "Prior PowerEventListener was not disposed on re-Start — orphaned SystemEvents subscription leak.");
            var secondListener = GetField<object?>(hm, "_powerListener");
            Assert.NotNull(secondListener);
            Assert.NotSame(firstListener, secondListener);
        }
        finally
        {
            hm.Stop();
        }

        Assert.Null(GetField<object?>(hm, "_healthTimer"));
        Assert.Null(GetField<object?>(hm, "_powerListener"));
    }

    [Fact]
    public void Source_Start_GuardsAgainstAlreadyRunning()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "HealthMonitor.cs");
        Assert.True(File.Exists(sourcePath), $"HealthMonitor.cs not found at {sourcePath}");
        var source = File.ReadAllText(sourcePath);
        Assert.Contains("if (_healthTimer != null || _powerListener != null)", source);
    }

    private static string FindRepoFile(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, segments.Last());
    }
}
