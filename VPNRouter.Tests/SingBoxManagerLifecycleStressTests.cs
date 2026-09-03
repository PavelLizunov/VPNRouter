// W4 audit (2026-05-30) — sustained concurrency STRESS for the SingBoxManager
// lifecycle (B1-B4 hardening, plans/singbox-lifecycle-hardening-v2.36.md +
// plans/critical-audit-targets.md W4).
//
// Why this exists (vs the existing focused tests):
//   SingBoxManagerConcurrentStopTests (B2) fires ONE 100-thread Stop() storm on
//   an IDLE manager (no lock acquired, no live handle). RestartTunLockTests (B3)
//   pins single Restart/Stop lock-release cases. SuppressExitedEventTests +
//   RestartInProgressSuppressionTests (B1) pin single suppress scenarios. Each
//   exercises ONE interleaving. None hammer the lock-ownership + B2 guard under
//   SUSTAINED, REPEATED concurrent contention on a manager that actually OWNS
//   the singleton TUN lock and has a live handle — the production "running
//   engine being torn down by several callers at once, over and over" shape.
//
//   This stress re-arms the manager (acquire lock + seed a live FakeProcessHandle)
//   and fires a concurrent Stop() storm REPEATEDLY (120 rounds × 8 threads),
//   asserting after EVERY round that the singleton TUN lock is balanced — i.e.
//   exactly one effective release happened, so the next round's TryAcquire
//   succeeds. A leak (a concurrent Stop failing to release) trips the next
//   TryAcquire; a double-release (SemaphoreFullException escaping the _owned
//   guard) trips the storm's WaitAll. Either regression in the B2 guard or the
//   four releaseLock-gated release sites surfaces here, where the single-storm
//   test wouldn't.
//
// Correctness / non-flakiness:
//   No timing-dependent assertions — only "the storm completed" (10 s deadlock
//   guard) and the DETERMINISTIC post-storm lock-balance + terminal State. No
//   real sing-box spawn (FakeProcessRunner) and no netsh/PowerShell shell-out
//   (TunAdapterDiagnostics.Runner swapped to a permissive fake, same as
//   SingBoxManagerRestartTunLockTests). Windows-only — the teardown paths
//   (graceful Kill + TUN-orphan cleanup) are the Windows branch.

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Sustained-concurrency stress for SingBoxManager teardown: repeated concurrent
/// Stop() storms on an acquired-lock + live-handle manager, asserting the
/// singleton <see cref="TunOwnershipLock"/> stays balanced across all rounds.
/// </summary>
public sealed class SingBoxManagerLifecycleStressTests : IDisposable
{
    private readonly IProcessRunner? _savedTunDiagRunner;

    public SingBoxManagerLifecycleStressTests()
    {
        // Swap TunAdapterDiagnostics.Runner to a permissive fake so the
        // TUN-orphan cleanup inside StopInternal (DisableOrphanedAdapter +
        // async TryRemoveAdapter) doesn't shell out to netsh / PowerShell for
        // every one of the 120×8 Stop() calls. Same pattern as
        // SingBoxManagerRestartTunLockTests.
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner", BindingFlags.NonPublic | BindingFlags.Static);
        _savedTunDiagRunner = runnerProp?.GetValue(null) as IProcessRunner;

        var fakeDiagRunner = new FakeProcessRunner()
            .OnRun(_ => true, new ProcessResult(
                ExitCode: 0, Stdout: string.Empty, Stderr: string.Empty,
                Duration: TimeSpan.Zero, TimedOut: false));
        runnerProp?.SetValue(null, fakeDiagRunner);
    }

    public void Dispose()
    {
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner", BindingFlags.NonPublic | BindingFlags.Static);
        if (_savedTunDiagRunner != null)
            runnerProp?.SetValue(null, _savedTunDiagRunner);

        // Reset the process-wide singleton after the fixture's direct lock use.
        try
        {
            var tunLock = TunOwnershipLock.Instance(null);
            tunLock.Release();
            tunLock.Dispose();
        }
        catch { }
    }

    [Fact]
    public void Stress_RepeatedConcurrentStopStorms_TunLockStaysBalanced()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — the graceful-Kill + TUN-orphan teardown paths are " +
            "the Windows branch; Linux/macOS go through pkexec/sudo not routed " +
            "through the IProcessRunner seam.");

        EnsureConfigDir();

        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(NewFakePid()));
        using var mgr = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: runner);

        var lockInstance = TunOwnershipLock.Instance(null);
        if (IsLockOwned(lockInstance)) lockInstance.Release();

        const int storms = 120;
        const int threadsPerStorm = 8;

        for (int s = 0; s < storms; s++)
        {
            // Re-arm: simulate a "running engine" — own the TUN lock + a live
            // handle. A leak from the PREVIOUS storm's concurrent Stop trips
            // this TryAcquire (the singleton would already be owned).
            Assert.True(lockInstance.TryAcquire(),
                $"storm {s}: could not acquire the singleton TUN lock — the " +
                $"previous storm's concurrent Stop() leaked it (failed to " +
                $"release under contention). B2 guard or a releaseLock-gated " +
                $"release site regressed.");
            SetField(mgr, "_ownsTunLock", true);

            var handle = new FakeProcessHandle(NewFakePid());
            SetField(mgr, "_handle", handle);

            using var barrier = new Barrier(threadsPerStorm);
            var tasks = Enumerable.Range(0, threadsPerStorm)
                .Select(_ => Task.Run(() =>
                {
                    barrier.SignalAndWait();   // maximise the concurrent-entry window
                    mgr.Stop();
                }))
                .ToArray();

            // WaitAll surfaces any thread exception (e.g. SemaphoreFullException
            // from a double-release escaping the _owned guard) as an
            // AggregateException → test fails. `false` = timeout = deadlock.
            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)),
                $"storm {s}: concurrent Stop() storm did not complete within 10s — deadlock.");

            // Deterministic post-conditions: exactly-one effective release →
            // lock free; terminal state Stopped.
            Assert.False(IsLockOwned(lockInstance),
                $"storm {s}: the TUN lock is still owned after a concurrent " +
                $"Stop() storm — no thread released it (or all releases were " +
                $"swallowed). Public Stop() must release (releaseLock=true).");
            Assert.Equal(SingBoxState.Stopped, mgr.State);

            // StopInternal disposed the handle; drop our reference so the
            // using-dispose at the end doesn't double-touch it.
            SetField(mgr, "_handle", null);
        }
    }

    // ─── Helpers (mirrors SingBoxManagerRestartTunLockTests) ─────────────────

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090",
    };

    private static bool IsLockOwned(TunOwnershipLock lockInstance)
    {
        var f = typeof(TunOwnershipLock).GetField("_owned",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (bool)f!.GetValue(lockInstance)!;
    }

    private static int _fakePidCounter = 90000;
    private static int NewFakePid() => Interlocked.Increment(ref _fakePidCounter);

    private static void EnsureConfigDir()
    {
        try { Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir); } catch { }
    }

    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"SingBoxManager has no field '{fieldName}'");
        f.SetValue(m, value);
    }
}
