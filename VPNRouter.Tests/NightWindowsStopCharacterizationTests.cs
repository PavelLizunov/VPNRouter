#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization regression suite for Windows sing-box process termination and
/// exact-stop unconfirmed lifecycle invariants (NIGHT-08 Windows stop fix).
/// <para>
/// Invariants verified:
/// 1. Kill throws / wait times out / process alive / HasExited throws leaves
///    State=Failed, _exactStopUnconfirmed=true, preserves _handle, retains TUN lease,
///    and queues NO PnP adapter removal or diagnostics runner calls.
/// 2. Gated parameter contract: failure preserves TUN lock for BOTH releaseLock=false
///    and releaseLock=true callers.
/// 3. Later retry on an exited handle cleanly confirms stop, disposes the retained
///    handle, nulls _handle, clears the unconfirmed guard, and releases TUN lease once.
/// 4. Early HasExited safe probe: an exception during exit probe is caught and treated
///    as unconfirmed stop without throwing unhandled exceptions.
/// 5. Positive Windows restart: confirms fresh fake handle creation and TUN lock retention.
/// 6. RestartCore: fails closed with old handle retained when exact stop is unconfirmed.
/// 7. ReloadConfigJsonWithResult: cannot write candidate config to disk before unconfirmed
///    stop is settled.
/// 8. Dispose: retries are not terminal when exact stop is unconfirmed; subsequent confirmed
///    stop settles terminal state.
/// </para>
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class NightWindowsStopCharacterizationTests : IDisposable
{
    private static readonly object s_tunOwnershipLockGate =
        typeof(TunOwnershipLock).GetField("InstanceGate", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
        ?? typeof(TunOwnershipLock);
    private static readonly FieldInfo? s_tunOwnershipLockInstanceField =
        typeof(TunOwnershipLock).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo? s_pendingTunRemovalField =
        typeof(SingBoxManager).GetField("s_pendingTunRemoval", BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly FieldInfo? s_removeNetAdapterMissingField =
        typeof(TunAdapterDiagnostics).GetField("s_removeNetAdapterMissing", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? s_actionableModuleMissingLoggedField =
        typeof(TunAdapterDiagnostics).GetField("s_actionableModuleMissingLogged", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? s_netAdapterModuleAvailableField =
        typeof(TunAdapterDiagnostics).GetField("s_netAdapterModuleAvailable", BindingFlags.Static | BindingFlags.NonPublic);

    private readonly TunOwnershipLock? _savedTunOwnershipLockInstance;
    private readonly Func<string, NativePnpLookupResult> _savedResolveNativePnpDeviceIds;
    private int _nativeLookupCount;
    private readonly object? _savedPendingTunRemoval;
    private readonly object? _savedRemoveNetAdapterMissing;
    private readonly object? _savedActionableModuleMissingLogged;
    private readonly object? _savedNetAdapterModuleAvailable;

    private readonly IProcessRunner _savedTunDiagRunner;
    private readonly IProcessRunner _savedSingBoxRunner;
    private readonly string? _savedAppPathsDataDir;
    private readonly string _testDataDir;
    private readonly FakeProcessRunner _fakeDiagRunner;

    public NightWindowsStopCharacterizationTests()
    {
        _savedAppPathsDataDir = GetAppPathsDataDir();
        _testDataDir = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-win-stop-char-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(_testDataDir);
        Directory.CreateDirectory(_testDataDir);

        // Save original TunOwnershipLock._instance then set null before tests.
        // No release/dispose of original or semaphore.
        lock (s_tunOwnershipLockGate)
        {
            _savedTunOwnershipLockInstance = (TunOwnershipLock?)s_tunOwnershipLockInstanceField?.GetValue(null);
            s_tunOwnershipLockInstanceField?.SetValue(null, null);
        }

        // Save static SingBoxManager pending removal task and TunAdapterDiagnostics latches
        _savedPendingTunRemoval = s_pendingTunRemovalField?.GetValue(null);
        _savedRemoveNetAdapterMissing = s_removeNetAdapterMissingField?.GetValue(null);
        _savedActionableModuleMissingLogged = s_actionableModuleMissingLoggedField?.GetValue(null);
        _savedNetAdapterModuleAvailable = s_netAdapterModuleAvailableField?.GetValue(null);

        // Pre-set net adapter module availability to false so no PowerShell probe is spawned
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        // Save diagnostics Native resolver delegate exact actual property, replace returns emptyIDs explicitly
        _savedResolveNativePnpDeviceIds = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        _nativeLookupCount = 0;
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = _ =>
        {
            Interlocked.Increment(ref _nativeLookupCount);
            return new NativePnpLookupResult(
                Success: true,
                InstanceIds: Array.Empty<string>(),
                Error: null);
        };

        // Save and replace static TunAdapterDiagnostics.Runner
        _savedTunDiagRunner = TunAdapterDiagnostics.Runner;
        _fakeDiagRunner = new FakeProcessRunner()
            .OnRun(_ => true, new ProcessResult(
                ExitCode: 0,
                Stdout: string.Empty,
                Stderr: string.Empty,
                Duration: TimeSpan.Zero,
                TimedOut: false));
        TunAdapterDiagnostics.Runner = _fakeDiagRunner;

        // Save and replace static SingBoxManager.Runner
        _savedSingBoxRunner = SingBoxManager.Runner;
        SingBoxManager.Runner = new FakeProcessRunner();
    }

    public void Dispose()
    {
        // Await static SingBoxManager pendingremoval task before restoring fakeRunner/resolver/paths
        WaitForPendingTunRemoval();

        // Restore original task/latches field values not reset other's
        s_pendingTunRemovalField?.SetValue(null, _savedPendingTunRemoval);
        s_removeNetAdapterMissingField?.SetValue(null, _savedRemoveNetAdapterMissing);
        s_actionableModuleMissingLoggedField?.SetValue(null, _savedActionableModuleMissingLogged);
        s_netAdapterModuleAvailableField?.SetValue(null, _savedNetAdapterModuleAvailable);

        // Restore diagnostics Native resolver delegate
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = _savedResolveNativePnpDeviceIds;

        // Restore original runner exact nonnull no fallback new ProcessRunner
        TunAdapterDiagnostics.Runner = _savedTunDiagRunner;
        SingBoxManager.Runner = _savedSingBoxRunner;

        // Dispose ONLY test singleton at end and restore original ref; no release/dispose original or semaphore
        lock (s_tunOwnershipLockGate)
        {
            var currentTestInstance = (TunOwnershipLock?)s_tunOwnershipLockInstanceField?.GetValue(null);
            if (currentTestInstance != null && !ReferenceEquals(currentTestInstance, _savedTunOwnershipLockInstance))
            {
                try
                {
                    currentTestInstance.Dispose();
                }
                catch { /* best-effort teardown of test singleton */ }
            }
            s_tunOwnershipLockInstanceField?.SetValue(null, _savedTunOwnershipLockInstance);
        }

        RestoreAppPathsDataDir(_savedAppPathsDataDir);
        try
        {
            if (Directory.Exists(_testDataDir))
                Directory.Delete(_testDataDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ─── 1. Kill throws / process remains alive ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInternal_WhenKillThrows_Alive_FailsClosedPreservingHandleAndLock_ThenRetrySucceeds(bool releaseLock)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { KillThrows = true };
        SetField(manager, "_handle", handle);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        Assert.True(IsLockOwned(lockInstance));

        try
        {
            InvokeStopInternal(manager, releaseLock);
            if (!releaseLock)
                WaitForPendingTunRemoval();

            // Verify unconfirmed failure state
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled, "Handle must NOT be disposed when Kill throws and process remains alive.");
            Assert.True((bool)GetField(manager, "_ownsTunLock")!, "_ownsTunLock must be retained.");
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!, "_exactStopUnconfirmed must be set to true.");
            Assert.True(IsLockOwned(lockInstance), "TUN ownership lock must remain owned on failed stop.");
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Later retry: process signals exit
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Stop();
            if (!releaseLock)
                WaitForPendingTunRemoval();

            // Verify confirmed stop state
            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled, "Handle must be disposed on confirmed stop.");
            Assert.False((bool)GetField(manager, "_ownsTunLock")!, "_ownsTunLock must be cleared.");
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!, "_exactStopUnconfirmed must be cleared.");
            Assert.False(IsLockOwned(lockInstance), "TUN ownership lock must be released on confirmed Stop.");
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 2. WaitForExitAsync throws OperationCanceledException / alive ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInternal_WhenWaitThrowsOce_Alive_FailsClosedPreservingHandleAndLock_ThenRetrySucceeds(bool releaseLock)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { WaitThrowsOce = true };
        SetField(manager, "_handle", handle);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            InvokeStopInternal(manager, releaseLock);
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled, "Handle must NOT be disposed when wait times out and process remains alive.");
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Later retry: process signals exit
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Stop();
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled);
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.False(IsLockOwned(lockInstance));
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 3. WaitForExitAsync returns but process still alive ────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInternal_WhenWaitReturns_StillAlive_FailsClosedPreservingHandleAndLock_ThenRetrySucceeds(bool releaseLock)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { WaitReturnsAlive = true };
        SetField(manager, "_handle", handle);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            InvokeStopInternal(manager, releaseLock);
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled);
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Later retry: process signals exit
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Stop();
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled);
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.False(IsLockOwned(lockInstance));
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 4. HasExited throws during safe probe ──────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInternal_WhenEarlyHasExitedThrows_FailsClosedPreservingHandleAndLock_ThenRetrySucceeds(bool releaseLock)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { HasExitedThrows = true };
        SetField(manager, "_handle", handle);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            // Early safe probe must catch exception and treat as unconfirmed, never bubble up
            InvokeStopInternal(manager, releaseLock);
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled);
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Later retry: process recovers probe and signals exit
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Stop();
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled);
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.False(IsLockOwned(lockInstance));
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopInternal_WhenPostKillHasExitedThrows_FailsClosedPreservingHandleAndLock_ThenRetrySucceeds(bool releaseLock)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { HasExitedThrowsOnPostKill = true };
        SetField(manager, "_handle", handle);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            InvokeStopInternal(manager, releaseLock);
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled);
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Later retry
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Stop();
            if (!releaseLock)
                WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled);
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.False(IsLockOwned(lockInstance));
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 5. Windows positive Restart test ───────────────────────────────

    [Fact]
    public void WindowsPositiveRestart_ConfirmsNewFakeHandleAndTunLockRetention()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only positive restart characterization test.");

        EnsureConfigDir();

        var initialHandle = new StubbornWindowsProcessHandle(NewFakePid());
        var replacementHandle = new FakeProcessHandle(NewFakePid());

        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => replacementHandle);

        var fakeExe = Path.Combine(_testDataDir, "sing-box.exe");
        File.WriteAllBytes(fakeExe, Array.Empty<byte>());

        var settings = new SingBoxSettings
        {
            ExecutablePath = fakeExe,
            ClashApi = "127.0.0.1:9090"
        };
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        SetField(manager, "_handle", initialHandle);
        var configPath = Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json");
        File.WriteAllText(configPath, "{}");
        SetField(manager, "_currentConfigPath", configPath);

        // Ensure all adapter lookups are faked
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        Assert.True(IsLockOwned(lockInstance));

        try
        {
            manager.Restart();

            Assert.Equal(SingBoxState.Running, manager.State);
            Assert.True(initialHandle.HasExited, "Initial process handle must have exited.");
            Assert.True(initialHandle.DisposeCalled, "Initial process handle must be disposed.");

            var currentHandle = GetField(manager, "_handle") as IProcessHandle;
            Assert.NotNull(currentHandle);
            Assert.Same(replacementHandle, currentHandle);
            Assert.False(replacementHandle.HasExited, "Replacement process handle must be alive.");
            Assert.NotEqual(initialHandle.Pid, replacementHandle.Pid);

            Assert.True((bool)GetField(manager, "_ownsTunLock")!,
                "Manager must retain local _ownsTunLock across restart.");
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!,
                "_exactStopUnconfirmed must be false after successful restart.");
            Assert.True(IsLockOwned(lockInstance),
                "TUN ownership lock must remain owned across successful Windows restart.");
            Assert.True(_nativeLookupCount > 0);
            var request = Assert.Single(_fakeDiagRunner.RunCalls);
            Assert.Equal("netsh", request.ExecutablePath);
            Assert.Equal(new[] { "interface", "show", "interface" }, request.Arguments);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            initialHandle.Dispose();
            replacementHandle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 6. RestartCore checks failed state and old handle ───────────────

    [Fact]
    public void RestartCore_WhenExactStopFails_LeavesFailedStateWithOldHandleAndRetainsLock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows stop failure during Restart.");

        EnsureConfigDir();

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { KillThrows = true };
        var runner = new FakeProcessRunner();

        var settings = DefaultSettings();
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        SetField(manager, "_handle", handle);
        SetField(manager, "_currentConfigPath",
            Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json"));

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            manager.Restart();
            WaitForPendingTunRemoval();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Empty(runner.StartCalls);
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 7. ReloadConfigJsonWithResult cannot write candidate ───────────

    [Fact]
    public void ReloadConfigJsonWithResult_WhenExactStopUnconfirmed_CannotWriteCandidateBeforeConfirmedStop()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows unconfirmed stop during reload.");

        EnsureConfigDir();

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { KillThrows = true };
        var runner = new FakeProcessRunner();

        var settings = DefaultSettings();
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        SetField(manager, "_handle", handle);
        var configPath = Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json");
        File.WriteAllText(configPath, "{\"existing\":\"baseline\"}");
        SetField(manager, "_currentConfigPath", configPath);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            // Initial failed stop sets unconfirmed
            InvokeStopInternal(manager, releaseLock: false);
            WaitForPendingTunRemoval();
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Act: attempt reload with candidate config
            var result = manager.ReloadConfigJsonWithResult("{\"candidate\":\"forbidden-write\"}", forceRestart: true);

            Assert.False(result, "ReloadConfigJsonWithResult must return false when exact stop is unconfirmed.");
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Disk verification: candidate config was NEVER written to disk
            var onDisk = File.ReadAllText(configPath);
            Assert.DoesNotContain("forbidden-write", onDisk);
            Assert.Contains("existing", onDisk);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 8. Dispose retries not terminal if unconfirmed ─────────────────

    [Fact]
    public void Dispose_WhenExactStopUnconfirmed_RetriesAreNotTerminalAndSubsequentConfirmedStopReleasesLock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows unconfirmed Dispose retry.");

        EnsureConfigDir();

        var handle = new StubbornWindowsProcessHandle(NewFakePid()) { KillThrows = true };
        var runner = new FakeProcessRunner();

        var settings = DefaultSettings();
        var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        SetField(manager, "_handle", handle);
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            // First Dispose attempt: stop is unconfirmed
            manager.Dispose();

            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.False(handle.DisposeCalled, "Handle must NOT be disposed when exact stop is unconfirmed.");
            Assert.True(IsLockOwned(lockInstance), "TUN ownership lock must remain owned when stop is unconfirmed.");
            Assert.Equal(0, (int)GetField(manager, "_disposed")!);
            Assert.Equal(0, _nativeLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Second Dispose attempt: process has exited, retry succeeds
            var previousLookupCount = _nativeLookupCount;
            handle.SignalExit();
            manager.Dispose();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled, "Handle must be disposed on confirmed stop.");
            Assert.False(IsLockOwned(lockInstance), "TUN ownership lock must be released on confirmed stop.");
            Assert.Equal(1, (int)GetField(manager, "_disposed")!);
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);

            // Third Dispose attempt: terminal no-op
            manager.Dispose();
            Assert.Equal(1, (int)GetField(manager, "_disposed")!);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 9. Early already-exited clears guard and disposes handle ───────

    [Fact]
    public void StopInternal_EarlyAlreadyExited_DisposesHandleClearsGuardAndRunsCleanup()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises early exit path.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        var handle = new StubbornWindowsProcessHandle(NewFakePid());
        handle.SignalExit(); // already exited before StopInternal
        SetField(manager, "_handle", handle);
        SetField(manager, "_exactStopUnconfirmed", true); // simulate retained unconfirmed flag from earlier attempt

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            var previousLookupCount = _nativeLookupCount;
            manager.Stop();

            Assert.Equal(SingBoxState.Stopped, manager.State);
            Assert.Null(GetField(manager, "_handle"));
            Assert.True(handle.DisposeCalled, "Retained handle must be disposed by early exit path.");
            Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!, "Early already-exited must clear unconfirmed guard.");
            Assert.False((bool)GetField(manager, "_ownsTunLock")!);
            Assert.False(IsLockOwned(lockInstance), "Lock must be released.");
            Assert.True(_nativeLookupCount > previousLookupCount);
            Assert.Empty(_fakeDiagRunner.RunCalls);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
        }
    }

    // ─── 10. Idle manager Stop preserves expected cleanup ───────────────

    [Fact]
    public void StopInternal_IdleManager_NullHandle_PreservesExpectedCleanupWithoutThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises idle manager cleanup path.");

        EnsureConfigDir();

        var fakeRunner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), logger: null, http: new FakeHttpClient(), runner: fakeRunner);

        Assert.Null(GetField(manager, "_handle"));

        var previousLookupCount = _nativeLookupCount;
        manager.Stop();

        Assert.Equal(SingBoxState.Stopped, manager.State);
        Assert.False((bool)GetField(manager, "_exactStopUnconfirmed")!);
        Assert.True(_nativeLookupCount > previousLookupCount);
        Assert.Empty(_fakeDiagRunner.RunCalls);
    }

    // ─── Test Infrastructure Helpers ────────────────────────────────────

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090"
    };

    private static void EnsureConfigDir()
    {
        try
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);
        }
        catch { /* best-effort */ }
    }

    private static string? GetAppPathsDataDir()
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)f?.GetValue(null);
    }

    private static void RestoreAppPathsDataDir(string? priorDataDir)
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic);
        f?.SetValue(null, priorDataDir);
    }

    private static object? GetField(SingBoxManager m, string fieldName)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(f);
        return f!.GetValue(m);
    }

    private static void SetField(SingBoxManager m, string fieldName, object? value)
    {
        var f = typeof(SingBoxManager).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(f);
        f!.SetValue(m, value);
    }

    private static void InvokeStopInternal(SingBoxManager manager, bool releaseLock)
    {
        var method = typeof(SingBoxManager).GetMethod("StopInternal",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(manager, new object[] { releaseLock });
    }

    private static void WaitForPendingTunRemoval()
    {
        try
        {
            if (s_pendingTunRemovalField?.GetValue(null) is Task pendingTask)
            {
                pendingTask.GetAwaiter().GetResult();
            }
        }
        catch { /* best-effort */ }
    }

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

    private static int _fakePidCounter = 40000;
    private static int NewFakePid() => Interlocked.Increment(ref _fakePidCounter);

    private sealed class StubbornWindowsProcessHandle : IProcessHandle
    {
        private volatile bool _hasExited;
        private readonly int _pid;

        public StubbornWindowsProcessHandle(int pid)
        {
            _pid = pid;
        }

        public int Pid => _pid;
        public bool KillThrows { get; set; }
        public bool WaitThrowsOce { get; set; }
        public bool WaitReturnsAlive { get; set; }
        public bool HasExitedThrows { get; set; }
        public bool HasExitedThrowsOnPostKill { get; set; }

        public bool HasExited
        {
            get
            {
                if (HasExitedThrows)
                    throw new InvalidOperationException("Simulated HasExited failure.");
                if (HasExitedThrowsOnPostKill && KillCallCount > 0)
                    throw new InvalidOperationException("Simulated post-kill HasExited failure.");
                return _hasExited;
            }
        }

        public int KillCallCount { get; private set; }
        public int SuppressExitedEventCallCount { get; private set; }
        public bool DisposeCalled { get; private set; }

        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }

        public Task<int> WaitForExitAsync(CancellationToken ct)
        {
            if (WaitThrowsOce)
                return Task.FromException<int>(new OperationCanceledException(ct));

            if (WaitReturnsAlive)
                return Task.FromResult(0);

            return _hasExited
                ? Task.FromResult(0)
                : Task.FromException<int>(new OperationCanceledException(ct));
        }

        public void Kill(bool entireProcessTree = true)
        {
            KillCallCount++;
            if (KillThrows)
                throw new InvalidOperationException("Simulated Kill failure (e.g. Access Denied).");

            if (!WaitReturnsAlive && !WaitThrowsOce)
                _hasExited = true;
        }

        public void SuppressExitedEvent() => SuppressExitedEventCallCount++;
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() => DisposeCalled = true;

        public void SignalExit()
        {
            KillThrows = false;
            WaitThrowsOce = false;
            WaitReturnsAlive = false;
            HasExitedThrows = false;
            HasExitedThrowsOnPostKill = false;
            _hasExited = true;
        }
    }
}
