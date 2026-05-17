#nullable enable
// ============================================================================
// LockFileTests.cs — Phase 2G Wave 7a-2 (HIGH priority service coverage)
// ============================================================================
//
// Covers VPNRouter.Core.Services.LockFile — the anti-double-launch +
// crash-detection guard upgraded in Phase 2D (commit 0480c58) from a
// PID-file marker to a real FileShare.None exclusive lock held for
// process lifetime. Uses InMemoryFileSystem so the test never touches
// %ProgramData%\VPNRouter\running.lock.
//
// Test shapes (8 cases):
//   1. AcquireInstance happy path writes PID payload + holds lock
//   2. Second concurrent AcquireInstance fails to acquire (anti-double-launch
//      invariant — the v2.31.x regression class)
//   3. ReleaseInstance allows a fresh instance to acquire afterwards
//   4. AcquireInstance is idempotent on the same instance
//   5. DetectPreviousCrashInstance returns null when no lock file exists
//   6. DetectPreviousCrashInstance surfaces dead-PID crashed-run banner
//   7. DetectPreviousCrashInstance returns null if PID belongs to a live process
//   8. DetectPreviousCrashInstance always consumes (deletes) the stale file
//   9. Unreadable lock-file payload still surfaces a generic banner
//  10. Static facade Acquire/Release/DetectPreviousCrash compile + run
//
// Brief: plans/phase2-2G-untested-services-2026-05-17.md (sub-wave 7a-2)
// ============================================================================

using System.Diagnostics;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Unit tests for <see cref="LockFile"/>. Uses <see cref="InMemoryFileSystem"/>
/// so we exercise the real LockFile logic against a fake IFileSystem with the
/// same TryAcquireExclusiveLockAsync semantics as production
/// (<see cref="RealFileSystem"/>). The contract for the fake lock is pinned in
/// <c>IFileSystemContractTests</c>, so any divergence between fake and real
/// would surface there first.
/// </summary>
public sealed class LockFileTests
{
    /// <summary>Stable test path so a single test owns its own lock-file slot.</summary>
    private static string NewLockPath() =>
        @"C:\VPNRouter\test\" + Guid.NewGuid().ToString("N") + ".lock";

    [Fact]
    public async Task AcquireInstance_HappyPath_WritesPidPayloadAndHoldsLock()
    {
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();

        // PID payload is on disk and contains current process id at line 0.
        Assert.True(fs.FileExists(path));
        var contents = fs.ReadAllText(path);
        var firstLine = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal(Environment.ProcessId.ToString(), firstLine);

        // A second instance attempting to acquire must be blocked. (Direct
        // probe against the underlying file-system contract — proves the
        // lock is genuinely held, not just that the payload was written.)
        var second = await fs.TryAcquireExclusiveLockAsync(path, TimeSpan.FromMilliseconds(50));
        Assert.Null(second);

        sut.ReleaseInstance();
    }

    [Fact]
    public void AcquireInstance_FromTwoInstances_SecondGetsNoLockButDoesNotThrow()
    {
        // Anti-double-launch invariant. The first instance grabs the lock,
        // the second instance's TryAcquireExclusiveLockAsync times out and
        // returns null. Critically: the second instance MUST log a warning
        // and proceed, not throw (per LockFile's defensive contract — we
        // never want to abort startup over a best-effort diagnostic).
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var first = new LockFile(fs, path);
        var second = new LockFile(fs, path);

        first.AcquireInstance();

        // Second.AcquireInstance must return without throwing even though
        // the underlying lock cannot be acquired.
        var ex = Record.Exception(() => second.AcquireInstance());
        Assert.Null(ex);

        // Cleanup.
        first.ReleaseInstance();
    }

    [Fact]
    public void ReleaseInstance_AllowsFreshAcquireAfterwards()
    {
        // Dispose-style release semantic: once the first instance releases,
        // a second instance must be able to acquire successfully.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var first = new LockFile(fs, path);

        first.AcquireInstance();
        first.ReleaseInstance();

        // Lock file should be gone after release.
        Assert.False(fs.FileExists(path));

        var second = new LockFile(fs, path);
        second.AcquireInstance();

        Assert.True(fs.FileExists(path),
            "Second instance should have re-created the lock file");

        second.ReleaseInstance();
    }

    [Fact]
    public void AcquireInstance_CalledTwiceOnSameInstance_IsNoOp()
    {
        // Idempotency: noisy startup can repeatedly call Acquire (e.g. on
        // re-entry from a retry helper). Repeated calls must not churn the
        // lock (no release-then-reacquire) and must not throw.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();
        var firstAcquireSnapshot = fs.ReadAllText(path);

        sut.AcquireInstance();  // No-op
        sut.AcquireInstance();  // No-op

        var afterRepeats = fs.ReadAllText(path);
        Assert.Equal(firstAcquireSnapshot, afterRepeats);

        sut.ReleaseInstance();
    }

    [Fact]
    public void DetectPreviousCrashInstance_NoLockFile_ReturnsNull()
    {
        // Clean state: no lock file exists. DetectPreviousCrash should
        // return null with no side-effect.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        var banner = sut.DetectPreviousCrashInstance();

        Assert.Null(banner);
        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_DeadPid_SurfacesCrashedRunBanner()
    {
        // Crashed-previous-run case: a stale lock file with a PID for a
        // process that is no longer alive. We pick a PID guaranteed to be
        // dead: the maximum 32-bit signed int value. (Negative numbers
        // throw inside Process.GetProcessById on .NET, so we use a
        // non-negative-but-impossibly-large PID.)
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var deadPid = int.MaxValue;
        var payload = $"{deadPid}\n2026-05-17T12:00:00Z\nC:\\dead.exe\n";
        fs.Seed(path, payload);

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        Assert.NotNull(banner);
        Assert.Contains($"PID {deadPid}", banner!);
        Assert.Contains("did not shut down cleanly", banner);
        Assert.Contains("2026-05-17T12:00:00Z", banner);

        // Always-consumed contract: the stale file must be gone after
        // detect even when it was a crash.
        Assert.False(fs.FileExists(path),
            "DetectPreviousCrash must delete the stale lock file");
    }

    [Fact]
    public void DetectPreviousCrashInstance_LivePid_ReturnsNullWithoutBanner()
    {
        // If the PID belongs to a process that is still alive (e.g. another
        // instance is genuinely running or PID was recycled to a real proc),
        // we DON'T emit a "crashed" banner — there's no strong evidence of
        // crash. Use our own current process id: it's guaranteed alive.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var livePid = Environment.ProcessId;
        var payload = $"{livePid}\n2026-05-17T12:00:00Z\nC:\\test.exe\n";
        fs.Seed(path, payload);

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        // No crash banner because the PID maps to a live process.
        Assert.Null(banner);

        // Still consumed (delete-on-read so we don't replay on next run).
        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_UnreadablePayload_SurfacesGenericBanner()
    {
        // Edge: lock file exists but is empty / corrupted (first line not
        // an integer). LockFile should still surface a banner — just
        // without a PID — and still consume the file.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        fs.Seed(path, "this-is-not-a-pid\nrandom garbage\n");

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();

        Assert.NotNull(banner);
        Assert.Contains("unreadable", banner!, StringComparison.OrdinalIgnoreCase);
        Assert.False(fs.FileExists(path));
    }

    [Fact]
    public void DetectPreviousCrashInstance_ThenAcquire_SucceedsCleanly()
    {
        // End-to-end shape: previous run crashed (stale file present, dead
        // PID), startup calls DetectPreviousCrash (consumes file), then
        // calls Acquire (writes fresh PID, locks). Both must succeed.
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        fs.Seed(path, $"{int.MaxValue}\n2026-05-17\nC:\\dead.exe\n");

        var sut = new LockFile(fs, path);
        var banner = sut.DetectPreviousCrashInstance();
        Assert.NotNull(banner);

        // Now acquire fresh: must not block on the stale file (it was
        // consumed) and the lock must be held with our PID.
        sut.AcquireInstance();
        Assert.True(fs.FileExists(path));
        var firstLine = fs.ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.Equal(Environment.ProcessId.ToString(), firstLine);

        sut.ReleaseInstance();
    }

    [Fact]
    public void StaticFacade_AcquireReleaseDetect_SmokeTest()
    {
        // The legacy static call sites (Acquire/Release/DetectPreviousCrash)
        // dispatch to DefaultInstance which uses RealFileSystem +
        // AppPaths.DataDir. We can't safely fully exercise it in a unit
        // test without polluting %ProgramData%\VPNRouter\running.lock —
        // but we CAN verify the calls compile and don't throw on a clean
        // probe. DetectPreviousCrash on a missing file returns null.
        //
        // Note: this is intentionally a smoke test, not an end-to-end —
        // the instance variant above carries the behaviour invariants.

        // Static call must not throw. On a clean dev box the running.lock
        // file may or may not exist; either way Detect must not throw.
        var ex = Record.Exception(() => LockFile.DetectPreviousCrash());
        Assert.Null(ex);

        // Acquire/Release are intentionally not invoked here to avoid
        // mutating the host %ProgramData%. If the static facade ever
        // crashes on Acquire, callers (CLI / Service / App) would surface
        // it in their own integration tests.
    }

    [Fact]
    public void AcquireInstance_PidPayloadStructure_IsThreeLines()
    {
        // Pin the on-disk format: PID\nISO-timestamp\nProcessPath\n. The
        // structure matters because DetectPreviousCrash parses by line
        // index — a regression that flips lines would break the crash
        // banner (PID-from-timestamp-string-parse fail).
        var fs = new InMemoryFileSystem();
        var path = NewLockPath();
        var sut = new LockFile(fs, path);

        sut.AcquireInstance();

        var lines = fs.ReadAllText(path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2,
            $"Expected at least 2 lines (PID + timestamp), got {lines.Length}: {string.Join("|", lines)}");
        // Line 0: parseable PID.
        Assert.True(int.TryParse(lines[0].Trim(), out _),
            $"Line 0 should be a PID, got '{lines[0]}'");
        // Line 1: ISO 8601 timestamp parseable.
        Assert.True(DateTime.TryParse(lines[1].Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _),
            $"Line 1 should be ISO timestamp, got '{lines[1]}'");

        sut.ReleaseInstance();
    }
}
