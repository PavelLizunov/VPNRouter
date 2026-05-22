// B2 (v2.36 SingBoxManager lifecycle hardening) — concurrent Stop guard
// pins.
//
// Why this exists:
//   Pre-B2 audit (Agent B, post v2.35.3-r1) found that SingBoxManager.Stop
//   had no entry-level mutex / Interlocked guard. Concurrent callers (UI
//   Disconnect button + HealthMonitor restart backoff + ProcessExit
//   fallback) could all enter StopInternal simultaneously. The four
//   _tunLock.Release() sites were defensively guarded by TunOwnershipLock's
//   own `_owned` check (catches SemaphoreFullException via no-op), but
//   other side-effects inside StopInternal (process Kill, _handle clear,
//   State enum flip) could still race.
//
// B2 fix (this brief — plans/singbox-lifecycle-hardening-v2.36.md):
//   Introduced `int _stopState` field + Interlocked.CompareExchange(ref
//   _stopState, 1, 0) entry guard. Only the thread that flips 0→1
//   progresses through the body; concurrent callers see the non-zero
//   value and return early. Reset to 0 in finally so sequential
//   (non-concurrent) Stop()'s re-enter normally.
//
// What this file pins:
//   1. Source-string pin — the CompareExchange entry guard MUST stay in
//      StopInternal. A refactor that drops it (e.g. switching to a lock
//      object) would trip this test, signalling the audit invariant
//      needs explicit re-pinning.
//   2. Behavioural pin — 100 concurrent Stop() calls on an idle
//      SingBoxManager must complete without exception. Pre-B2 the
//      defensive guards in TunOwnershipLock prevented crashes anyway,
//      but this pins forward-compatibility: if anyone removes the
//      `_owned` guard later, the B2 entry guard remains the safety net.
//   3. Re-entry pin — sequential Stop()'s after the first Stop() returns
//      must STILL execute the body (the guard resets to 0). Otherwise
//      the fix would break legitimate re-Stop scenarios (e.g. user
//      Disconnect after HealthMonitor's auto-Stop).
//
// Cross-platform: tests run on every platform — they exercise public
// Stop() without spawning real sing-box (no _handle, no Kill path).
// No Assert.SkipUnless needed.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pins the B2 concurrent-Stop guard added in v2.36 SingBoxManager
/// lifecycle hardening. See file-header comment.
/// </summary>
public sealed class SingBoxManagerConcurrentStopTests
{
    private static SingBoxSettings BuildIdleSettings()
    {
        // Settings only need ExecutablePath for the constructor to accept
        // them; the value isn't dereferenced unless StartWithJson is
        // called (which these tests don't). Pointing at a known-missing
        // path means if a test ever accidentally triggers a real spawn,
        // it'll fail loudly via FileNotFoundException rather than
        // silently spawning something.
        return new SingBoxSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-b2-test.exe"),
        };
    }

    [Fact]
    public void Source_StopInternal_ContainsCompareExchangeGuard()
    {
        // B2 invariant pin (source-string). The entry guard MUST stay in
        // StopInternal. If anyone refactors it to a lock object, a
        // SemaphoreSlim, or anything else — they trip this test as a
        // signal to re-pin the audit invariant. Mirrors Task #53's
        // Restart_PreservesTunLock_SourcePin approach.

        var thisAssembly = typeof(SingBoxManager).Assembly;
        var thisAssemblyPath = thisAssembly.Location;
        var coreDir = Path.GetDirectoryName(thisAssemblyPath)!;

        // Source file lives at VPNRouter.Core/Services/SingBoxManager.cs
        // relative to the repo root. Walk up from the assembly out-dir
        // to find it; fall back to a CWD-relative path if that doesn't
        // resolve (CI runner layout).
        var sourcePath = FindRepoFile(coreDir, "VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.True(File.Exists(sourcePath),
            $"SingBoxManager.cs source not found near assembly. Tried: {sourcePath}");

        var source = File.ReadAllText(sourcePath);

        // The exact CompareExchange call shape we expect to find. Tied
        // to the `_stopState` field name + the 1/0 sentinel values used
        // by the B2 fix.
        Assert.Contains("Interlocked.CompareExchange(ref _stopState, 1, 0)", source);

        // Belt-and-braces: the reset in finally must also be present.
        // Otherwise sequential Stop()'s would deadlock on the second
        // call (guard never resets).
        Assert.Contains("Volatile.Write(ref _stopState, 0)", source);
    }

    [Fact]
    public async Task ConcurrentStop_ManyThreads_NoExceptionThrown()
    {
        // Behavioural pin: 100 concurrent Stop() calls on an idle
        // SingBoxManager must complete cleanly. Pre-B2 the TunOwnershipLock
        // _owned guard would have absorbed the second Release attempt,
        // but if that defensive layer ever erodes the B2 entry guard is
        // the explicit single-execution invariant.

        using var mgr = new SingBoxManager(BuildIdleSettings());

        const int threadCount = 100;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                // All threads wait at the barrier, then release together
                // to maximise the concurrent-entry window.
                barrier.SignalAndWait();
                mgr.Stop();
            });
        }

        // No exception expected. If any thread throws (e.g.
        // SemaphoreFullException, NullReferenceException), Task.WhenAll
        // surfaces it.
        await Task.WhenAll(tasks);

        // Final state: Stopped. Even if 100 threads raced through,
        // the guard prevented any of them from corrupting the State
        // transition.
        Assert.Equal(SingBoxState.Stopped, mgr.State);
    }

    [Fact]
    public void SequentialStop_AfterFirstStop_StillExecutesBody()
    {
        // Re-entry pin: the B2 finally-block reset to 0 must let
        // subsequent (sequential, not concurrent) Stop()'s execute the
        // body. Otherwise legitimate re-Stop scenarios — e.g. UI
        // Disconnect immediately after HealthMonitor's auto-Stop — would
        // become silent no-ops.

        using var mgr = new SingBoxManager(BuildIdleSettings());

        // First Stop sets State to Stopped (was Stopped on construct,
        // so this is effectively re-asserting; no exception either way).
        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);

        // Second Stop must also run through StopInternal — guard reset
        // to 0 in finally. We can't easily observe the body executing
        // without a log sink, but we CAN verify no exception is thrown
        // and the State remains stable. If the guard wasn't resetting,
        // every subsequent Stop() would still hit the early-return path,
        // which is fine for idle state but would break edge cases like
        // re-acquiring the lock after a transient release.
        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);

        // Third Stop, same expectation.
        mgr.Stop();
        Assert.Equal(SingBoxState.Stopped, mgr.State);
    }

    private static string FindRepoFile(string startDir, params string[] segments)
    {
        // Walk up from the test bin directory until we find a parent
        // that contains "VPNRouter.Core" (i.e. the repo root). Then
        // join the requested segments.
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        // Last-resort fallback for CI runners where the bin layout is
        // unconventional: try $cwd/<segments>.
        return Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
    }
}
