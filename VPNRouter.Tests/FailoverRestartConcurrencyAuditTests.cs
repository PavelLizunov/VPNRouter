// v2.44.3-r2 — regression pins for the two HIGH findings from the adversarial
// concurrency audit of the v2.44.3 failover-restart fix (workflow
// failover-concurrency-audit, 2026-06-25). The audit explicitly noted "no
// regression test covers this interleaving"; this file closes that gap.
//
// Finding 1 (state-consistency, real): a HealthMonitor AttemptRestart
// continuation could relaunch sing-box on a manager that TeardownInternal
// already disposed — the lifecycle gate that serialises the failover restart
// does NOT extend to that threadpool continuation, so two sing-box processes
// could contend for the wintun adapter (orphan TUN). Fix: a _disposed guard at
// the top of SingBoxManager.Restart() AND ReloadConfigJson() — a disposed
// manager never relaunches.
//
// Finding 2 (state-consistency, real): ExecuteProbeFailoverRestartAsync caught
// only OperationCanceled/ObjectDisposed; a generic bring-up failure (the
// swapped-in candidate never reaching IsRunning) left a live-but-undisposed
// _singBox that Dispose's IsRunning==false branch never reaps (leaked
// SingBoxManager + TUN lock + ProcessExit subscription). Fix: a catch(Exception)
// that runs TeardownInternal() then rethrows.
//
// Cross-platform: the behavioural test drives public SingBoxManager on an idle
// instance (no real spawn — ExecutablePath points at a missing file); the
// source pins read the committed source. No Assert.SkipUnless needed.

#nullable enable

using System;
using System.IO;
using System.Linq;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the two concurrency fixes landed in v2.44.3-r2 after the adversarial
/// failover-restart audit. See file-header comment for the findings.
/// </summary>
public sealed class FailoverRestartConcurrencyAuditTests
{
    private static SingBoxSettings BuildIdleSettings() => new()
    {
        // Missing path: if a test ever accidentally triggers a real spawn it
        // fails loudly rather than launching something. The r2 guard means a
        // disposed manager never reaches LaunchProcess at all.
        ExecutablePath = Path.Combine(
            Path.GetTempPath(), "nonexistent-sing-box-failover-audit-test.exe"),
    };

    // ─── Finding 1: a disposed manager must never relaunch sing-box ───────

    [Fact]
    public void Restart_OnDisposedManager_IsNoOp_DoesNotRelaunch()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        // With the r2 _disposed guard, Restart() returns before the
        // State=Restarting flip and before LaunchProcess. Without the guard, a
        // stale HealthMonitor continuation would spawn a second sing-box on this
        // already-disposed manager.
        var ex = Record.Exception(() => mgr.Restart());

        Assert.Null(ex);
        Assert.NotEqual(SingBoxState.Restarting, mgr.State);
    }

    [Fact]
    public void ReloadConfigJson_ForceRestart_OnDisposedManager_IsNoOp()
    {
        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        var ex = Record.Exception(() => mgr.ReloadConfigJson("{}", forceRestart: true));

        Assert.Null(ex);
        Assert.NotEqual(SingBoxState.Restarting, mgr.State);
    }

    [Fact]
    public void Source_SingBoxManager_RestartAndReload_HaveDisposedGuard()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        // Unique log markers from the three r2 guards — pins that Restart() and
        // ReloadConfigJson() bail out on entry AND that LaunchProcess re-checks
        // at the spawn chokepoint (closing the residual TOCTOU where a Dispose
        // races during Restart's StopInternal + 750ms settle). A refactor that
        // drops any guard trips this test as a signal to re-pin the invariant.
        Assert.Contains("Restart ignored — manager already disposed", source);
        Assert.Contains("ReloadConfigJson ignored — manager already disposed", source);
        Assert.Contains("LaunchProcess aborted — manager disposed before spawn", source);
        Assert.Contains("Volatile.Read(ref _disposed) != 0", source);
    }

    // ─── Finding 2: a failed failover bring-up tears down (no leak) ────────

    [Fact]
    public void Source_ExecuteProbeFailoverRestart_TearsDownOnGenericThrow()
    {
        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "VpnEngine.cs");
        var source = File.ReadAllText(sourcePath);

        // The r2 catch(Exception) in ExecuteProbeFailoverRestartAsync must run
        // TeardownInternal before rethrowing — otherwise a non-cancellation
        // bring-up failure leaks the SingBoxManager (TUN lock + ProcessExit
        // subscription) until process exit. The unique log marker pins it.
        Assert.Contains(
            "Failover restart failed to bring up replacement — tearing down partial state",
            source);
    }

    private static string FindRepoFile(params string[] segments)
    {
        var startDir = Path.GetDirectoryName(typeof(SingBoxManager).Assembly.Location)!;
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(new[] { Environment.CurrentDirectory }.Concat(segments).ToArray());
    }
}
