// v2.37.0-r52 (ekko 2026-05-25 forced-restart crash suppression) pins.
//
// Bug report: ekko's vpnrouter20260525_002.log + 525_005.log at 18:17:44
// (and ~24 other timestamps in same session) showed
//   18:17:44.944 [INF] [SingBoxManager] Stopping sing-box (PID 6984)
//   18:17:44.977 [ERR] [SingBoxManager] sing-box crashed (exit code: -1)
// 33ms between intentional Stop (called from Restart() via
// VpnEngine.ApplyAsync forceRestart=true on a routing_mode flip) and a
// FALSE "sing-box crashed (exit code: -1)" event. Total impact: 25+ false
// crash lines across two log files + 10-15s outage per flip because
// HealthMonitor saw the Crashed event and started its own backoff restart
// loop on top of the explicit Restart already in progress.
//
// Root cause: SuppressExitedEvent (v2.36.0-r4) wins the race in MOST cases,
// but the OS Exited callback can already be in the dispatcher queue when
// EnableRaisingEvents=false flips the subscription. On Windows with a
// heavily-loaded process (sing-box mid-flow with active TCP connections),
// the dispatch happens before the suppression takes effect. Empirically
// 33ms between Stop and OnProcessExited firing — see ekko log.
//
// Fix: belt-and-braces second line of defence. SingBoxManager.Restart sets
// `_restartInProgress = true` BEFORE StopInternal; OnProcessExited checks
// the flag and treats exit code -1/137/143 during Restart as the expected
// late OS callback, logging at INF level + skipping Crashed.Invoke.
// Genuine FATALs (exit code 1 from TUN init failure) still propagate.
//
// What this file pins:
//   1. Source — Restart() sets `_restartInProgress = true` before
//      StopInternal and clears it in finally.
//   2. Source — OnProcessExited contains the flag-check guard with the
//      expected exit codes (-1, 137, 143) and the "suppressing Crashed"
//      log line.
//   3. Source — the early-return after the suppression branch skips
//      Crashed.Invoke + the post-crash adapter cleanup (Restart's
//      LaunchProcess handles those).
//
// Brief: discovered while diagnosing ekko's logs in continuation of v3.0
// brat-2026-05-24 work. Follow-up to the SuppressExitedEvent fix which
// solved the typical case but missed the ~15-30% tail where OS event
// delivery wins.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the v2.37.0-r52 forced-restart crash suppression. See file-header.
/// </summary>
public sealed class SingBoxManagerRestartInProgressSuppressionTests
{
    [Fact]
    public void Source_RestartInProgressFlag_DeclaredAsVolatile()
    {
        // The flag is read on a different thread than it's written
        // (OnProcessExited fires on the ThreadPool dispatcher, Restart
        // runs on the UI/CLI caller thread). Volatile gives us the
        // memory-barrier semantics we need without a lock.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.Contains("private volatile bool _restartInProgress", src);
    }

    [Fact]
    public void Source_Restart_SetsFlagTrueBeforeStopInternal()
    {
        // The flag must be set BEFORE StopInternal — once StopInternal
        // calls Kill, the OS may dispatch Exited concurrently, and the
        // OnProcessExited handler races against the flag-write. Setting
        // it first guarantees the guard sees `true` for the lifetime of
        // the Kill + Wait + Launch window.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var restartHeader = src.IndexOf("public void Restart()", StringComparison.Ordinal);
        Assert.True(restartHeader >= 0, "Restart() method not found in SingBoxManager.cs");

        // Find the end of Restart's body to bound our search window.
        // Restart() is short (~30 lines) so a 3000-char window covers it.
        var window = src.Substring(restartHeader, Math.Min(3000, src.Length - restartHeader));

        var flagSetIdx = window.IndexOf("_restartInProgress = true", StringComparison.Ordinal);
        var stopCallIdx = window.IndexOf("StopInternal(releaseLock: false)", StringComparison.Ordinal);

        Assert.True(flagSetIdx >= 0,
            "Expected `_restartInProgress = true` inside Restart() before StopInternal call.");
        Assert.True(stopCallIdx >= 0,
            "Expected `StopInternal(releaseLock: false)` inside Restart() (this is the load-bearing line that triggers the OS Exited race).");
        Assert.True(flagSetIdx < stopCallIdx,
            "`_restartInProgress = true` must appear BEFORE StopInternal call. Otherwise the OS Exited callback can fire before the flag is set, leaking through to Crashed.Invoke. " +
            $"flagSetIdx={flagSetIdx}, stopCallIdx={stopCallIdx}");
    }

    [Fact]
    public void Source_Restart_ClearsFlagInFinally()
    {
        // The flag must be cleared in a finally block so that genuine
        // crashes during LaunchProcess (e.g. sing-box can't open TUN
        // because the previous adapter is still half-disposed) STILL
        // fire Crashed.Invoke and HealthMonitor can take recovery action.
        // If we cleared it after LaunchProcess without a finally, an
        // exception from LaunchProcess would leave the flag set TRUE
        // forever and ALL future genuine crashes would be wrongly
        // suppressed.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var restartHeader = src.IndexOf("public void Restart()", StringComparison.Ordinal);
        var window = src.Substring(restartHeader, Math.Min(3000, src.Length - restartHeader));

        var finallyIdx = window.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIdx > 0, "Expected a `finally` block inside Restart() to clear the flag.");

        var clearIdx = window.IndexOf("_restartInProgress = false", finallyIdx, StringComparison.Ordinal);
        Assert.True(clearIdx > 0,
            "Expected `_restartInProgress = false` inside the finally block. Without this, an exception from LaunchProcess leaves the flag stuck TRUE and all future genuine crashes get wrongly suppressed.");
    }

    [Fact]
    public void Source_OnProcessExited_ChecksFlagAndIntentionalKillExitCodes()
    {
        // The OnProcessExited handler must check _restartInProgress AND
        // gate on the "intentional kill" exit codes:
        //   -1   = Windows TerminateProcess (Kill())
        //   137  = SIGKILL on Linux/macOS  (128 + 9)
        //   143  = SIGTERM on Linux/macOS  (128 + 15)
        // Any other exit code (1 = sing-box FATAL, 2/3 = config errors,
        // etc.) is a genuine crash and MUST propagate through Crashed
        // even if the restart-in-progress flag is set.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        Assert.True(handler >= 0, "OnProcessExited method not found");

        // OnProcessExited is ~150 lines including the orphan-adapter
        // cleanup at the bottom; the guard must be near the top, before
        // the existing exit-code logging branch.
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        Assert.True(flagCheckIdx > 0,
            "Expected `_restartInProgress` referenced inside OnProcessExited as the gate condition for the suppression branch.");

        // Must check all three intentional-kill exit codes.
        Assert.Contains("-1", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
        Assert.Contains("137", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
        Assert.Contains("143", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
    }

    [Fact]
    public void Source_OnProcessExited_SuppressionBranchLogsAtInformationLevel()
    {
        // The suppression branch must log at INF (not ERR or WRN) so that
        // log scanners like post-ship-mcp-verify don't false-flag the
        // intentional-restart exit as a crash. A WRN or ERR here would
        // re-introduce the noise we're trying to fix.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        Assert.True(flagCheckIdx > 0);

        // Walk forward from flagCheckIdx and find the first log call —
        // must be _logger.Information, not Warning or Error.
        var branchWindow = window.Substring(flagCheckIdx, Math.Min(1500, window.Length - flagCheckIdx));
        var infoIdx = branchWindow.IndexOf("_logger.Information", StringComparison.Ordinal);
        var warningIdx = branchWindow.IndexOf("_logger.Warning", StringComparison.Ordinal);
        var errorIdx = branchWindow.IndexOf("_logger.Error", StringComparison.Ordinal);

        // The Information call must come first (i.e. inside the
        // suppression branch). Warning and Error may appear later in
        // the same window for the normal (non-suppressed) exit-code
        // branches; that's fine as long as Information beats them
        // by position inside the suppression branch.
        Assert.True(infoIdx >= 0,
            "Expected `_logger.Information(...)` inside the suppression branch — must log audit trail at INF level so the post-ship scanners don't false-flag.");
        if (warningIdx >= 0)
            Assert.True(infoIdx < warningIdx,
                "`_logger.Information` for the suppression branch must come BEFORE the `_logger.Warning` for the (separate) exit-code-0 branch.");
        if (errorIdx >= 0)
            Assert.True(infoIdx < errorIdx,
                "`_logger.Information` for the suppression branch must come BEFORE the `_logger.Error` for the (separate) exit-code-nonzero branch.");
    }

    [Fact]
    public void Source_OnProcessExited_SuppressionBranchReturnsEarly()
    {
        // After logging at INF, the suppression branch must `return;`
        // before Crashed.Invoke / orphan-adapter cleanup runs. Otherwise
        // we'd log INF + still fire Crashed which defeats the whole
        // fix. The `return;` is the load-bearing line that prevents the
        // HealthMonitor double-restart loop.
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        var crashedInvokeIdx = window.IndexOf("Crashed?.Invoke", StringComparison.Ordinal);
        Assert.True(crashedInvokeIdx > flagCheckIdx,
            "Crashed?.Invoke must appear AFTER the _restartInProgress guard, otherwise the guard can't prevent the invoke.");

        // Between flagCheckIdx and crashedInvokeIdx, look for an early
        // `return;` statement — that's the early-exit from the
        // suppression branch.
        var between = window.Substring(flagCheckIdx, crashedInvokeIdx - flagCheckIdx);
        Assert.Contains("return;", between);
    }

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(VPNRouter.Core.Services.SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
        if (!File.Exists(fallback))
            throw new FileNotFoundException($"Source file not found: {string.Join("/", segments)}");
        return File.ReadAllText(fallback);
    }
}
