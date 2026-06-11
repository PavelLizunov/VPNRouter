// M-9 (Windows perf audit 2026-06-11): HealthMonitor's AttemptRestart
// continuation checked the stop/cancel gate only ONCE at entry. Between that
// check and the actual sing-box revival it does a process scan, a config
// regen, and (on a TUN-orphan crash) a netsh disable with a 500ms sleep —
// 1-3s of real work. If the user pressed Stop in that window, sing-box was
// RESTARTED after the Stop: the UI showed "disconnected" while the tunnel
// stayed live. The fix re-checks `_isStopping` (now volatile so the threadpool
// continuation sees the UI-thread write) immediately before reviving sing-box.
//
// Behaviour is timing-dependent (a real race window), so this pins the fix at
// the source level — a refactor that drops the re-check or de-volatiles the
// flag trips here.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class HealthMonitorStopVsRestartRaceTests
{
    [Fact]
    public void Source_AttemptRestart_RechecksStopGateBeforeRevival()
    {
        var src = ReadHealthMonitorSource();

        // The stop flag must be volatile so the threadpool restart continuation
        // observes the UI-thread Stop() write without tearing.
        Assert.Contains("volatile bool _isStopping", src);

        // A second stop/cancel gate must exist between the orphan-recovery step
        // and the sing-box revival, not just at the continuation entry. The
        // "aborting sing-box revival" log marker is UNIQUE to that re-check, so
        // its presence pins the fix; a refactor that drops the re-check removes
        // the marker and trips here. (Positional pinning is avoided because
        // RunTunOrphanRecoveryCleanup / TryHotReloadViaApi each appear as both a
        // method definition and a call, so IndexOf would match the wrong one.)
        Assert.Contains("aborting sing-box revival", src);

        // The re-check predicate itself must consult the stop gate.
        var markerIdx = src.IndexOf("aborting sing-box revival", StringComparison.Ordinal);
        var window = src.Substring(Math.Max(0, markerIdx - 200), Math.Min(220, src.Length - Math.Max(0, markerIdx - 200)));
        Assert.Contains("_isStopping", window);
    }

    private static string ReadHealthMonitorSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "HealthMonitor.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("HealthMonitor.cs not reachable from test base dir");
    }
}
