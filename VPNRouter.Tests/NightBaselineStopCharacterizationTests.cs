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
/// Baseline-compatible characterization test for NIGHT-08 Windows exact-stop retention.
/// Verifies that when Kill throws and process exit cannot be confirmed, Windows Stop
/// preserves the exact process handle, retains the TUN ownership lease, and sets State=Failed.
/// Under baseline, Stop nulls/disposes the handle and releases TUN ownership (expected RED).
/// </summary>
[Collection(SafeModeStateCollection.Name)]
public sealed class NightBaselineStopCharacterizationTests
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

    [Fact]
    public void WindowsExactStop_WhenKillThrows_RetainsOwnedHandleAndLease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — exercises Windows graceful process stop branch.");

        var savedAppPathsDataDir = GetAppPathsDataDir();
        var testDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-baseline-stop-char-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(testDataDir);
        Directory.CreateDirectory(testDataDir);
        Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);

        TunOwnershipLock? savedTunOwnershipLockInstance;
        lock (s_tunOwnershipLockGate)
        {
            savedTunOwnershipLockInstance = (TunOwnershipLock?)s_tunOwnershipLockInstanceField?.GetValue(null);
            s_tunOwnershipLockInstanceField?.SetValue(null, null);
        }

        var savedPendingTunRemoval = s_pendingTunRemovalField?.GetValue(null);
        var savedRemoveNetAdapterMissing = s_removeNetAdapterMissingField?.GetValue(null);
        var savedActionableModuleMissingLogged = s_actionableModuleMissingLoggedField?.GetValue(null);
        var savedNetAdapterModuleAvailable = s_netAdapterModuleAvailableField?.GetValue(null);

        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(false);

        var savedResolveNativePnpDeviceIds = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        var nativeLookupCount = 0;
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = _ =>
        {
            Interlocked.Increment(ref nativeLookupCount);
            return new NativePnpLookupResult(Success: true, InstanceIds: Array.Empty<string>(), Error: null);
        };

        var savedTunDiagRunner = TunAdapterDiagnostics.Runner;
        var fakeDiagRunner = new FakeProcessRunner()
            .OnRun(_ => true, new ProcessResult(ExitCode: 0, Stdout: string.Empty, Stderr: string.Empty, Duration: TimeSpan.Zero, TimedOut: false));
        TunAdapterDiagnostics.Runner = fakeDiagRunner;

        var savedSingBoxRunner = SingBoxManager.Runner;
        SingBoxManager.Runner = new FakeProcessRunner();

        SingBoxManager? manager = null;
        TestProcessHandle? handle = null;
        try
        {
            var fakeRunner = new FakeProcessRunner();
            using var fakeHttp = new FakeHttpClient();
            manager = new SingBoxManager(DefaultSettings(), logger: null, http: fakeHttp, runner: fakeRunner);

            var lockInstance = TunOwnershipLock.Instance(null);
            SetLockOwnedForTest(lockInstance, manager);

            handle = new TestProcessHandle(42000);
            SetField(manager, "_handle", handle);
            SetState(manager, SingBoxState.Running);

            manager.Stop();

            Assert.Equal(1, handle.KillCallCount);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.True((bool)GetField(manager, "_ownsTunLock")!);
            Assert.Equal(0, handle.DisposeCallCount);
            Assert.Equal(0, nativeLookupCount);
            Assert.Empty(fakeDiagRunner.RunCalls);
        }
        finally
        {
            if (manager != null)
            {
                SetField(manager, "_handle", null);
                SetField(manager, "_ownsTunLock", false);
                manager.Dispose();
            }
            handle?.Dispose();

            WaitForPendingTunRemoval();

            lock (s_tunOwnershipLockGate)
            {
                var currentTestInstance = (TunOwnershipLock?)s_tunOwnershipLockInstanceField?.GetValue(null);
                if (currentTestInstance != null && !ReferenceEquals(currentTestInstance, savedTunOwnershipLockInstance))
                {
                    try { currentTestInstance.Dispose(); }
                    catch { /* best-effort teardown of test singleton */ }
                }
                s_tunOwnershipLockInstanceField?.SetValue(null, savedTunOwnershipLockInstance);
            }

            s_pendingTunRemovalField?.SetValue(null, savedPendingTunRemoval);
            s_removeNetAdapterMissingField?.SetValue(null, savedRemoveNetAdapterMissing);
            s_actionableModuleMissingLoggedField?.SetValue(null, savedActionableModuleMissingLogged);
            s_netAdapterModuleAvailableField?.SetValue(null, savedNetAdapterModuleAvailable);

            TunAdapterDiagnostics.ResolveNativePnpDeviceIds = savedResolveNativePnpDeviceIds;
            TunAdapterDiagnostics.Runner = savedTunDiagRunner;
            SingBoxManager.Runner = savedSingBoxRunner;

            RestoreAppPathsDataDir(savedAppPathsDataDir);
            try
            {
                if (Directory.Exists(testDataDir)) Directory.Delete(testDataDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    private static SingBoxSettings DefaultSettings() => new() { ExecutablePath = @"C:\nonexistent\sing-box.exe", ClashApi = "127.0.0.1:9090" };

    private static string? GetAppPathsDataDir() =>
        (string?)typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);

    private static void RestoreAppPathsDataDir(string? prior) =>
        typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, prior);

    private static object? GetField(SingBoxManager m, string name) =>
        typeof(SingBoxManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(m);

    private static void SetField(SingBoxManager m, string name, object? val) =>
        typeof(SingBoxManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(m, val);

    private static void SetState(SingBoxManager m, SingBoxState state) =>
        typeof(SingBoxManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(m, state);

    private static void WaitForPendingTunRemoval()
    {
        try
        {
            if (s_pendingTunRemovalField?.GetValue(null) is Task pendingTask) pendingTask.GetAwaiter().GetResult();
        }
        catch { /* best-effort */ }
    }

    private static void SetLockOwnedForTest(TunOwnershipLock lockInstance, SingBoxManager manager)
    {
        SetField(manager, "_ownsTunLock", true);
        lockInstance.Release();

        var semaphoreField = typeof(TunOwnershipLock).GetField("_semaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        var ownedField = typeof(TunOwnershipLock).GetField("_owned", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(semaphoreField);
        Assert.NotNull(ownedField);

        (semaphoreField!.GetValue(lockInstance) as IDisposable)?.Dispose();
        semaphoreField.SetValue(lockInstance, new Semaphore(0, 1));
        ownedField!.SetValue(lockInstance, true);
    }

    private sealed class TestProcessHandle : IProcessHandle
    {
        public TestProcessHandle(int pid) => Pid = pid;
        public int Pid { get; }
        public bool HasExited => false;
        public int KillCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(0);
        public void Kill(bool entireProcessTree = true)
        {
            KillCallCount++;
            throw new InvalidOperationException("Simulated Kill failure.");
        }
        public void SuppressExitedEvent() { }
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() => DisposeCallCount++;
    }
}
