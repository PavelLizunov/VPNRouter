// v2.40.0 audit P2 (plans/bug-responsiveness-memory-audit-targets-2026-06-02.md
// "HealthMonitor.Start() стоит проверить на повторный вызов").
//
// Pre-fix HealthMonitor.Start() assigned _healthTimer = new Timer(...) and
// _powerListener = new PowerEventListener(...) unconditionally. A second Start()
// without an intervening Stop() silently overwrote both — the old Timer leaked
// and, worse, the old PowerEventListener kept its Windows SystemEvents
// subscription alive for the life of the process.
//
// Fix: Start() now tears down a prior run (calls the idempotent Stop()) when
// _healthTimer or _powerListener is already non-null, before re-initialising.
// The normal first Start (both null) is unaffected.

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
            HealthCheckInterval = 3600, // 1h — keeps the periodic Timer dormant during the test
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

            // Second Start with no intervening Stop. Pre-fix this orphaned
            // firstListener (a leaked Windows SystemEvents subscription) and
            // firstTimer. The idempotency guard must tear them down first.
            hm.Start(new Profile { Name = "idempotency-test" }, new AppSettings());

            // The old PowerEventListener must be disposed (not orphaned) and a
            // fresh one installed.
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

        // After Stop the live handles are released.
        Assert.Null(GetField<object?>(hm, "_healthTimer"));
        Assert.Null(GetField<object?>(hm, "_powerListener"));
    }

    [Fact]
    public void Source_Start_GuardsAgainstAlreadyRunning()
    {
        // Deterministic backstop: Start() must guard on already-running state
        // before re-initialising. A refactor that drops the guard trips here.
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
