// Task #53 (2026-05-21) — behavioural pin for the SingBoxManager.Restart
// TUN-lock preservation bug.
//
// Why this exists (TDD-first):
//   The existing Restart_PreservesTunLock_SourcePin in
//   SingBoxManagerStateMachineTests pins the source-string shape
//   `StopInternal(releaseLock: false)` inside Restart's body. That pin
//   catches a refactor that drops the parameter wire-up — but it does
//   NOT catch the bug that PROMPTED Task #53: only 3 of 4 paths through
//   StopInternal honour the `releaseLock` parameter. Path 4 (Windows
//   graceful Kill, `_handle != null && !_handle.HasExited`) had a
//   bare `_tunLock.Release()` in its `finally` block at line 405,
//   ignoring the parameter.
//
//   Result: Restart() called `StopInternal(releaseLock: false)` (the
//   intended "preserve the lock across the Stop→LaunchProcess window"
//   path) but the lock was still released — because path 4 fired and
//   ignored the parameter. The next process spawn in LaunchProcess did
//   NOT re-acquire the lock (acquisition lives only in StartWithJson,
//   not LaunchProcess), so the singleton TunOwnershipLock stayed
//   `_owned = false` for the rest of the Restart sequence.
//
// What this file pins (behavioural, not source-string):
//   1. Restart() must leave the TUN lock OWNED. Pre-fix this fails.
//   2. StopInternal(releaseLock: ...) — the parameter ACTUALLY GATES
//      the lock release in path 4. Defence matrix pin.
//   3. The public Stop() entry point must STILL release the lock —
//      so the bug fix doesn't accidentally leak the lock across user-
//      initiated stops. Belt-and-braces regression pin.
//
// Cross-platform:
//   All 3 tests are Windows-only via Assert.SkipUnless. Same reasoning
//   as Task #49's lifecycle suites: SingBoxManager's Linux path runs
//   through pkexec / sudo / getcap (LinuxStopEscalationChain + the
//   HasNetCapability probe), which is external-process-heavy and not
//   routed through the IProcessRunner seam used by sing-box itself.
//   The bug being fixed lives entirely in the Windows graceful path
//   (StopInternal line 405 — `_handle != null && !_handle.HasExited`
//   branch), so Windows-only coverage is the right fit.
//
// Brief: plans/task53-singboxmanager-restart-tunlock-2026-05-21.md.

#nullable enable

using System;
using System.IO;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Behavioural pin for the Task #53 bug: <see cref="SingBoxManager.Restart"/>
/// MUST NOT release the <see cref="TunOwnershipLock"/> in the brief
/// window between <c>StopInternal</c> and <c>LaunchProcess</c>.
///
/// <para>Pre-Task-#53 production code at <c>SingBoxManager.cs:405</c>
/// (Windows graceful Kill path's <c>finally</c> block) called
/// <c>_tunLock.Release()</c> UNCONDITIONALLY, ignoring the
/// <c>releaseLock</c> parameter that <see cref="SingBoxManager.Restart"/>
/// explicitly passes as <c>false</c>. The other 3 paths through
/// <see cref="SingBoxManager"/>'s <c>StopInternal</c> already had the
/// <c>if (releaseLock) _tunLock.Release()</c> gate; only path 4 was
/// missing it. Fix: add the gate.</para>
///
/// <para><strong>Test strategy.</strong> The
/// <see cref="TunOwnershipLock"/> singleton's <c>_owned</c> field is the
/// authoritative source of truth — reading it via reflection bypasses
/// any need to spin up a real named-semaphore observer. The fake
/// <see cref="IProcessRunner"/> seam suppresses the real sing-box
/// spawn, and the static <c>TunAdapterDiagnostics.Runner</c> seam
/// suppresses the netsh / PowerShell shell-outs that LaunchProcess
/// triggers on Windows. The actual race window is captured by reading
/// <c>_owned</c> on the singleton AFTER Restart returns — pre-fix it
/// would be false, post-fix true.</para>
/// </summary>
public sealed class SingBoxManagerRestartTunLockTests : IDisposable
{
    // ─── Fixture: shared seam wiring for the 3 tests ────────────────────

    private readonly IProcessRunner? _savedTunDiagRunner;
    private readonly string _savedDataDir;
    private readonly string _testDataDir;

    public SingBoxManagerRestartTunLockTests()
    {
        _savedDataDir = VPNRouter.Core.AppPaths.DataDir;
        _testDataDir = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-restart-lock-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(_testDataDir);
        VPNRouter.Core.AppPaths.EnsureDirectories();

        // Snapshot the static TunAdapterDiagnostics.Runner so we can
        // restore it on Dispose — other tests in the suite rely on the
        // production ProcessRunner. We swap in a permissive fake for the
        // duration of each test so LaunchProcess's PreStartCleanupAsync
        // call doesn't shell out to netsh / PowerShell on Windows.
        //
        // Reflection rather than direct access because Runner is
        // internal and this file is in the test assembly (which has
        // InternalsVisibleTo) — using the property directly would work,
        // but explicit reflection makes the seam swap self-documenting
        // and resilient to a future visibility change.
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner",
            BindingFlags.NonPublic | BindingFlags.Static);
        _savedTunDiagRunner = runnerProp?.GetValue(null) as IProcessRunner;

        // Permissive fake: any netsh / PowerShell call → exit 0 with
        // empty stdout/stderr, instant duration. PreStartCleanupAsync
        // walks several enumeration + delete branches; each one should
        // return success so the helper completes the synchronous
        // GetAwaiter().GetResult() in LaunchProcess without throwing.
        var fakeDiagRunner = new FakeProcessRunner()
            .OnRun(_ => true, new ProcessResult(
                ExitCode: 0,
                Stdout: string.Empty,
                Stderr: string.Empty,
                Duration: TimeSpan.Zero,
                TimedOut: false));
        runnerProp?.SetValue(null, fakeDiagRunner);
    }

    public void Dispose()
    {
        // Restore the shared TunAdapterDiagnostics.Runner so this test
        // class doesn't poison the suite-wide state. Same property
        // access pattern as the ctor.
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (_savedTunDiagRunner != null)
            runnerProp?.SetValue(null, _savedTunDiagRunner);

        // Release the singleton TUN lock so the next test class
        // (and the cross-invocation testhost successor) sees a clean
        // slate. The named semaphore is process-wide; leaking it
        // would block any other test that tries to acquire it.
        ReleaseSingletonTunLockBestEffort();
        VPNRouter.Core.AppPaths.OverrideDataDir(_savedDataDir);
        try { Directory.Delete(_testDataDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─── Test 1: behavioural pin — Restart preserves the lock ───────────

    [Fact]
    public void Restart_PreservesTunLock_BehaviourTest()
    {
        // The TDD-first failing pin. Pre-fix this test FAILS on
        // production code: the singleton's `_owned` flag goes false
        // after Restart because StopInternal path 4 (Windows graceful)
        // hits the bare `_tunLock.Release()` at line 405.
        //
        // Post-fix this test PASSES because the release is gated by
        // the `releaseLock` parameter (false in Restart's call).
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — Linux/macOS paths go through pkexec / sudo " +
            "escalation chains not routed through IProcessRunner.");

        // Pre-create the config directory so WriteJsonToDisk inside
        // LaunchProcess doesn't fail on a fresh CI checkout.
        EnsureConfigDir();

        // Hand SingBoxManager its own FakeProcessRunner via the ctor.
        // Match any ProcessRequest — the spawn is for sing-box, and we
        // don't care about the argv shape here, just that the new
        // handle reports alive after LaunchProcess returns.
        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: NewFakePid()));

        var settings = DefaultSettings();
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        // Seed _handle non-null + HasExited=false so StopInternal lands
        // in path 4 (Windows graceful Kill). We're simulating the
        // "Restart called on a live engine" production shape — the
        // exact branch the bug lives in.
        var initialHandle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", initialHandle);
        SetField(manager, "_currentConfigPath",
            Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json"));

        // Acquire the singleton TUN lock — this mirrors what
        // StartWithJson does in production. The singleton is the same
        // instance SingBoxManager holds in its private `_tunLock`
        // field (set in ctor via TunOwnershipLock.Instance(_logger)).
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        Assert.True(IsLockOwned(lockInstance),
            "Test setup could not seed the isolated lock as owned.");

        // Act: invoke Restart. The production path is:
        //   Restart() → StopInternal(releaseLock: false)
        //              → Windows graceful path 4 (Kill + finally)
        //              → buggy: _tunLock.Release() unconditional
        //   Restart() → Thread.Sleep(750) Windows wintun settle
        //   Restart() → LaunchProcess(exePath)
        //              → new FakeProcessHandle from runner
        // After Restart returns: the new sing-box is "alive", and the
        // lock state IS the only thing the test pins.
        manager.Restart();

        // Pin: the singleton lock is STILL owned. Pre-fix this assert
        // fails. Post-fix it passes — the path-4 finally now gates the
        // release on `releaseLock`, which Restart passed as false.
        Assert.True(IsLockOwned(lockInstance),
            "BUG: Restart() released the TUN ownership lock during the " +
            "Stop→LaunchProcess window. Path 4 (Windows graceful Kill) " +
            "in SingBoxManager.StopInternal's finally block must honour " +
            "the `releaseLock` parameter — Restart passes false to " +
            "preserve the lock for the new process. See Task #53 brief " +
            "plans/task53-singboxmanager-restart-tunlock-2026-05-21.md.");

        // Belt-and-braces: the new sing-box handle should be alive,
        // proving the Restart actually completed LaunchProcess. Without
        // this we'd be pinning a no-op as success.
        var newHandle = GetField(manager, "_handle") as IProcessHandle;
        Assert.NotNull(newHandle);
        Assert.False(newHandle.HasExited,
            "Restart didn't actually spawn a new process — pin Restart " +
            "ran end-to-end before checking lock state.");
        Assert.NotEqual(initialHandle.Pid, newHandle.Pid);

        // Clean-up: clear the handle BEFORE the using-block dispose so
        // Stop() doesn't try to interact with the fake on teardown.
        SetField(manager, "_handle", null);
        initialHandle.Dispose();
        newHandle.Dispose();
    }

    // ─── Test 2: parameter gating matrix ─────────────────────────────────

    [Fact]
    public void Restart_StopInternalReleasesLockOnlyWhenAsked()
    {
        // Pin the parameter contract in path 4 (Windows graceful Kill):
        //   StopInternal(releaseLock: true)  → lock released ✓
        //   StopInternal(releaseLock: false) → lock preserved ✓
        // The other 3 paths already honour the parameter in pre-Task-#53
        // code — this test verifies path 4 joins the family post-fix.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — path 4 (graceful Kill) is the Windows branch.");

        EnsureConfigDir();

        // --- Case A: releaseLock=true ---
        // Build a fresh manager. The handle must be alive going in so
        // StopInternal lands in path 4 (not the early "process exited"
        // branch). After StopInternal(releaseLock: true), the lock
        // MUST be released — matches public Stop() behaviour.
        var runnerA = new FakeProcessRunner();
        using (var managerA = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runnerA))
        {
            var lockInstance = TunOwnershipLock.Instance(null);
            // Acquire before each case so we're starting from a known
            // owned state. Other tests in the class may have left it
            // released (Test 1's Restart did not release; Test 3
            // explicitly tests release).
            SetLockOwnedForTest(lockInstance, managerA);

            var handleA = new FakeProcessHandle(pid: NewFakePid());
            SetField(managerA, "_handle", handleA);

            InvokePrivate(managerA, "StopInternal", new object[] { true });

            Assert.False(IsLockOwned(lockInstance),
                "Case A (releaseLock=true): the lock MUST be released. " +
                "This mirrors public Stop()'s behaviour — pre-fix this " +
                "was already the case, post-fix it stays the case.");

            SetField(managerA, "_handle", null);
            handleA.Dispose();
        }

        // --- Case B: releaseLock=false ---
        // Same setup, opposite parameter. After
        // StopInternal(releaseLock: false), the lock MUST stay owned.
        // Pre-fix this fails — path 4's finally ignores the param.
        // Post-fix it passes — the gate now holds.
        var runnerB = new FakeProcessRunner();
        using (var managerB = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runnerB))
        {
            var lockInstance = TunOwnershipLock.Instance(null);
            SetLockOwnedForTest(lockInstance, managerB);

            var handleB = new FakeProcessHandle(pid: NewFakePid());
            SetField(managerB, "_handle", handleB);

            InvokePrivate(managerB, "StopInternal", new object[] { false });

            Assert.True(IsLockOwned(lockInstance),
                "Case B (releaseLock=false): the lock MUST stay owned. " +
                "Pre-fix this assertion FAILED because path 4's finally " +
                "block at SingBoxManager.cs:405 ignored the parameter. " +
                "The fix gates the Release with `if (releaseLock)`.");

            SetField(managerB, "_handle", null);
            handleB.Dispose();
        }
    }

    // ─── Test 3: regression pin — public Stop still releases ────────────

    [Fact]
    public void Stop_PublicEntryPoint_ReleasesLockNormally_RegressionPin()
    {
        // Pin: the bug fix MUST NOT silently introduce a lock-leak on
        // the user-initiated Stop path. `Stop()` calls
        // `StopInternal(releaseLock: true)` which, in path 4, MUST
        // still release the lock. Without this pin, an over-zealous
        // "always preserve" refactor would leave the lock owned across
        // user Stops, blocking the next start.
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — path 4 (graceful Kill) is the Windows branch.");

        EnsureConfigDir();

        var runner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runner);

        // Seed the lock as owned + handle as alive so we land in path 4.
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        var handle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", handle);

        // Act: public Stop() — the user-initiated path.
        manager.Stop();

        // Pin: lock released. This is what makes the user's NEXT
        // Start succeed (acquisition in StartWithJson would otherwise
        // throw TunOwnershipException).
        Assert.False(IsLockOwned(lockInstance),
            "REGRESSION: Stop() left the TUN lock owned. The bug fix " +
            "must only change path 4's behaviour when releaseLock=false " +
            "(the Restart path) — Stop()'s releaseLock=true path must " +
            "still release the lock. See Task #53 brief.");

        SetField(manager, "_handle", null);
        handle.Dispose();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void Stop_LinuxCapabilityMode_ReleasesLockNormally_RegressionPin()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        using var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        var handle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", handle);

        manager.Stop();

        Assert.False(IsLockOwned(lockInstance),
            "Linux capability-mode Stop must release the TUN ownership lock " +
            "so the next Connect in the same process can acquire it.");

        SetField(manager, "_handle", null);
        handle.Dispose();
    }

    [Fact]
    public void Stop_LinuxCapabilityMode_FailedExactStop_PreservesLockAndReportsFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        using var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        var handle = new StubbornProcessHandle(NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            manager.Stop();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.True(IsLockOwned(lockInstance),
                "A failed exact stop must preserve TUN ownership.");
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.Equal(1, handle.KillCallCount);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    [Fact]
    public void RejectedManagerDispose_CannotClearAnotherManagersFailedStopLease()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        var managerA = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var runnerB = new FakeProcessRunner();
        var managerB = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runnerB);
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, managerA);
        var handle = new StubbornProcessHandle(NewFakePid());
        SetField(managerA, "_handle", handle);

        try
        {
            managerA.Stop();
            Assert.Throws<TunOwnershipException>(() => managerB.StartWithJson("{}"));
            managerB.Restart();
            Assert.Empty(runnerB.StartCalls);

            managerB.Dispose();

            Assert.True(IsLockOwned(lockInstance));
            Assert.False((bool)GetField(managerB, "_ownsTunLock")!);
            Assert.Same(handle, GetField(managerA, "_handle"));
        }
        finally
        {
            SetField(managerA, "_handle", null);
            SetField(managerA, "_ownsTunLock", false);
            SetField(managerA, "_exactStopUnconfirmed", false);
            managerA.Dispose();
            managerB.Dispose();
            lockInstance.Release();
        }
    }

    [Fact]
    public void Start_UnconfirmedExistingLease_IsRejectedBeforeReplacementLaunch()
    {
        var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        SetField(manager, "_exactStopUnconfirmed", true);

        try
        {
            Assert.Throws<TunOwnershipException>(() => manager.StartWithJson("{}"));
            Assert.True(IsLockOwned(lockInstance));
        }
        finally
        {
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            manager.Dispose();
            lockInstance.Release();
        }
    }

    [Fact]
    public async Task Stop_LinuxCapabilityMode_SerializesConcurrentStart()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        var handle = new BlockingExitProcessHandle(NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            var stopTask = Task.Run(manager.Stop);
            Assert.True(handle.WaitEntered.Wait(TimeSpan.FromSeconds(2)));

            using var startInvoked = new ManualResetEventSlim();
            var startTask = Task.Run(() =>
            {
                startInvoked.Set();
                return Record.Exception(() => manager.StartWithJson("{}"));
            });
            Assert.True(startInvoked.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(startTask.Wait(TimeSpan.FromMilliseconds(100)),
                "Start must wait behind the in-flight exact Stop lifecycle gate.");
            Assert.Same(handle, GetField(manager, "_handle"));

            handle.AllowExit.Set();
            await stopTask;
            Assert.IsType<FileNotFoundException>(await startTask);
        }
        finally
        {
            handle.AllowExit.Set();
            manager.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    [Fact]
    public void Restart_LinuxCapabilityMode_FailedExactStop_DoesNotLaunchReplacement()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        var runner = new FakeProcessRunner();
        var manager = new SingBoxManager(DefaultSettings(), null, new FakeHttpClient(), runner);
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        var handle = new StubbornProcessHandle(NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            manager.Restart();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Empty(runner.StartCalls);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.True(IsLockOwned(lockInstance));
        }
        finally
        {
            manager.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    [Fact]
    public void Dispose_LinuxCapabilityMode_FailedExactStop_PreservesLock()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), new FakeProcessRunner());
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        var handle = new StubbornProcessHandle(NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            manager.Dispose();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.True(IsLockOwned(lockInstance),
                "Dispose must not release ownership while the exact process may remain alive.");
            Assert.False(handle.DisposeCalled,
                "Dispose must retain the exact capability handle as retry authority after failed stop.");
        }
        finally
        {
            if (IsLockOwned(lockInstance)) lockInstance.Release();
            handle.Dispose();
        }
    }

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090"
    };

    /// <summary>
    /// Read <see cref="TunOwnershipLock._owned"/> via reflection. This
    /// field is the authoritative "do I own the named semaphore?"
    /// indicator. Cheaper + more deterministic than spinning up a
    /// second observer process to probe the semaphore.
    /// </summary>
    private static bool IsLockOwned(TunOwnershipLock lockInstance)
    {
        var f = typeof(TunOwnershipLock).GetField("_owned",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (bool)f!.GetValue(lockInstance)!;
    }

    private static void SetLockOwnedForTest(
        TunOwnershipLock lockInstance,
        SingBoxManager manager)
    {
        SetField(manager, "_ownsTunLock", true);
        SetField(manager, "_exactStopUnconfirmed", false);
        lockInstance.Release();

        var semaphoreField = typeof(TunOwnershipLock).GetField(
            "_semaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        var ownedField = typeof(TunOwnershipLock).GetField(
            "_owned", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(semaphoreField);
        Assert.NotNull(ownedField);

        (semaphoreField!.GetValue(lockInstance) as IDisposable)?.Dispose();
        // Count zero models a held semaphore without touching the system-wide
        // Global\VPNRouter-SingBox-Owner name used by the installed app.
        semaphoreField.SetValue(lockInstance, new Semaphore(0, 1));
        ownedField!.SetValue(lockInstance, true);
    }

    private static void ReleaseSingletonTunLockBestEffort()
    {
        try
        {
            var lockInstance = TunOwnershipLock.Instance(null);
            // Release ownership, then dispose/reset the process-wide singleton.
            // Failed-stop production paths intentionally preserve it, but this
            // fixture replaces its semaphore and must not leak that test double.
            lockInstance.Release();
            lockInstance.Dispose();
        }
        catch
        {
            // Best-effort teardown; never throw out of Dispose.
        }
    }

    private static int _fakePidCounter = 10000;

    /// <summary>Generate a unique PID for each FakeProcessHandle so tests
    /// can distinguish initial-vs-restart handles by .Pid comparison.</summary>
    private static int NewFakePid() => System.Threading.Interlocked.Increment(ref _fakePidCounter);

    private static void EnsureConfigDir()
    {
        // LaunchProcess writes the current.json. WriteJsonToDisk creates
        // the dir if missing; this is a defence-in-depth pre-create so
        // a directory-permission flake doesn't masquerade as a fix
        // regression.
        try
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);
        }
        catch { /* best-effort — Write will surface a clearer error */ }
    }

    private static object? GetField(SingBoxManager m, string fieldName)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        return f.GetValue(m);
    }

    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no field '{fieldName}'");
        f.SetValue(m, value);
    }

    private sealed class BlockingExitProcessHandle(int pid) : IProcessHandle
    {
        private volatile bool _hasExited;
        public int Pid { get; } = pid;
        public bool HasExited => _hasExited;
        public ManualResetEventSlim WaitEntered { get; } = new();
        public ManualResetEventSlim AllowExit { get; } = new();
        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }

        public Task<int> WaitForExitAsync(CancellationToken ct)
        {
            WaitEntered.Set();
            AllowExit.Wait(ct);
            return Task.FromResult(-1);
        }

        public void Kill(bool entireProcessTree = true) => _hasExited = true;
        public void SuppressExitedEvent() { }
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() { }
    }

    private sealed class StubbornProcessHandle(int pid) : IProcessHandle
    {
        public int Pid { get; } = pid;
        public bool HasExited => false;
        public int KillCallCount { get; private set; }
        public bool DisposeCalled { get; private set; }
        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }

        public Task<int> WaitForExitAsync(CancellationToken ct)
            => Task.FromException<int>(new OperationCanceledException(ct));

        public void Kill(bool entireProcessTree = true) => KillCallCount++;
        public void SuppressExitedEvent() { }
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() => DisposeCalled = true;
    }

    private static void InvokePrivate(SingBoxManager m, string method, object?[] args)
    {
        var mi = typeof(SingBoxManager).GetMethod(method,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                $"SingBoxManager has no method '{method}'");
        try
        {
            mi.Invoke(m, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }
}
