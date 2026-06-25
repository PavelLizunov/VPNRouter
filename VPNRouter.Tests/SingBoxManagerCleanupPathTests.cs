// B1 (v2.36 SingBoxManager lifecycle hardening) — ProcessExit dual-hook
// consolidation pins.
//
// Why this exists:
//   Pre-B1 audit (Agent B, post v2.35.3-r1) found that SingBoxManager had
//   TWO independent paths for `_tunLock` cleanup:
//     1. The public Dispose() method — graceful user-initiated tear-down
//     2. AppDomain.ProcessExit handler — fallback for abrupt termination
//        (Environment.Exit, Ctrl+C without graceful shutdown, OOM-kill)
//
//   On a normal Dispose() → process-exit sequence, BOTH paths fired,
//   calling _tunLock.Dispose() twice. TunOwnershipLock.Dispose is
//   idempotent (guarded by an internal _disposed flag) so the second
//   call was a no-op — no crash. But the "two paths, both run" pattern
//   was fragile: a future refactor of TunOwnershipLock that dropped the
//   idempotency guard would silently introduce a double-dispose bug.
//
// B1 fix (plans/singbox-lifecycle-hardening-v2.36.md):
//   - Widened `_disposed` from bool to int so Dispose can use
//     Interlocked.CompareExchange for atomic single-execution.
//   - ProcessExit handler now reads `_disposed` via Volatile.Read and
//     no-ops if Dispose already ran.
//   - Dispose() explicitly calls _tunLock.Dispose() in the normal path
//     (was previously only via the ProcessExit fallback).
//
// What this file pins:
//   1. Source-string pins — the Interlocked.CompareExchange in Dispose
//      AND the Volatile.Read gate in the ProcessExit lambda MUST stay.
//      A refactor that switches back to `bool _disposed` (or removes
//      the gate entirely) trips these tests as a signal to re-pin the
//      audit invariant.
//   2. Behavioural pin — concurrent Dispose() calls from multiple
//      threads complete cleanly. Only ONE thread executes the body;
//      others see the post-CompareExchange value and return early.
//   3. Cleanup-completeness pin — Dispose() leaves the SingBoxManager
//      in a fully-disposed state (no double-free risk via subsequent
//      ProcessExit fallback).
//
// Cross-platform: tests run on every platform.

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
/// Pins the B1 ProcessExit dual-hook consolidation added in v2.36
/// SingBoxManager lifecycle hardening. See file-header comment.
/// </summary>
public sealed class SingBoxManagerCleanupPathTests
{
    private static SingBoxSettings BuildIdleSettings()
    {
        return new SingBoxSettings
        {
            ExecutablePath = Path.Combine(Path.GetTempPath(), "nonexistent-sing-box-for-b1-test.exe"),
        };
    }

    [Fact]
    public void Source_Dispose_ContainsInterlockedCompareExchange()
    {
        // B1 source pin: Dispose() MUST use atomic CompareExchange on
        // `_disposed` (not the old `if (_disposed) return; _disposed = true;`
        // unprotected pattern). If anyone refactors back to the pre-B1
        // pattern this fires as a signal to re-pin the audit invariant.

        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.True(File.Exists(sourcePath),
            $"SingBoxManager.cs source not found. Tried: {sourcePath}");

        var source = SingBoxSourceText.ReadAll(sourcePath);

        // The expected CompareExchange call shape on the _disposed field.
        Assert.Contains("Interlocked.CompareExchange(ref _disposed, 1, 0)", source);
    }

    [Fact]
    public void Source_ProcessExit_GatedByVolatileRead()
    {
        // B1 source pin: ProcessExit handler MUST read _disposed via
        // Volatile.Read and conditionally invoke _tunLock.Dispose().
        // A refactor that drops the gate would reintroduce the dual-
        // path cleanup pattern.

        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        // Both the read and the conditional gate must be present.
        Assert.Contains("Volatile.Read(ref _disposed)", source);
        // The gate emits _tunLock.Dispose() only if the read returns 0
        // (alive). Use a multiline-friendly match — the lambda body
        // formatting is consistent in the production source.
        Assert.Contains("if (Volatile.Read(ref _disposed) == 0)", source);
    }

    [Fact]
    public void Source_Dispose_ExplicitlyDisposesTunLock()
    {
        // B1 cleanup-completeness pin: Dispose() must call
        // _tunLock.Dispose() directly in its body, not rely on the
        // ProcessExit fallback. This ensures the normal cleanup path
        // is self-contained.

        var sourcePath = FindRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        var source = SingBoxSourceText.ReadAll(sourcePath);

        // The Dispose body emits _tunLock.Dispose() — find the call
        // and verify it's inside the Dispose method (after the
        // CompareExchange guard).
        var disposeMethodStart = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeMethodStart >= 0, "Dispose method not found");

        // The next public method after Dispose is LinuxStopEscalationChain
        // (private — actually). Just look at the next ~50 lines after
        // Dispose's opening for the _tunLock.Dispose() call.
        // Take enough context to span Dispose's body fully — comments
        // describing the B1 fix make the body ~1500 chars long. Use a
        // 2500-char window which is small enough to stay inside Dispose
        // (and not bleed into the next method) but large enough to
        // cover the body including the new B1 commentary.
        var disposeBodyApprox = source.Substring(disposeMethodStart, Math.Min(2500, source.Length - disposeMethodStart));
        Assert.Contains("_tunLock.Dispose()", disposeBodyApprox);
    }

    [Fact]
    public async Task ConcurrentDispose_ManyThreads_NoExceptionThrown()
    {
        // B1 behavioural pin: 50 concurrent Dispose() calls from
        // different threads complete without exception. The
        // CompareExchange guard ensures only the first thread runs the
        // cleanup body; others observe _disposed=1 and return early.
        //
        // Pre-B1 (bool flag, unprotected check-then-set) had a
        // theoretical race window between the check and the set where
        // two threads could both enter the body. The widening to int +
        // CompareExchange closes this.

        var mgr = new SingBoxManager(BuildIdleSettings());

        const int threadCount = 50;
        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                mgr.Dispose();
            });
        }

        await Task.WhenAll(tasks);

        // Subsequent Dispose() calls also no-op (post-disposal idempotency).
        mgr.Dispose();
    }

    [Fact]
    public void Dispose_LeavesManagerInTerminalState()
    {
        // B1 cleanup-completeness check. After Dispose(), the manager
        // should be in Stopped state and any subsequent Dispose() no-ops.

        var mgr = new SingBoxManager(BuildIdleSettings());
        mgr.Dispose();

        // State stays Stopped (was Stopped on construct, Dispose calls
        // Stop() which keeps it Stopped).
        Assert.Equal(SingBoxState.Stopped, mgr.State);

        // Subsequent Dispose is a no-op — no exception.
        mgr.Dispose();
        mgr.Dispose();
        mgr.Dispose();
    }

    private static string FindRepoFile(params string[] segments)
    {
        var thisAssembly = typeof(SingBoxManager).Assembly;
        var coreDir = Path.GetDirectoryName(thisAssembly.Location)!;

        var dir = new DirectoryInfo(coreDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
    }
}
