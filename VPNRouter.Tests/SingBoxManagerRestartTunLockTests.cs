#nullable enable

using System;
using System.IO;
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

[Collection(SafeModeStateCollection.Name)]
public sealed class SingBoxManagerRestartTunLockTests : IDisposable
{
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

        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner",
            BindingFlags.NonPublic | BindingFlags.Static);
        _savedTunDiagRunner = runnerProp?.GetValue(null) as IProcessRunner;

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
        var runnerProp = typeof(TunAdapterDiagnostics).GetProperty(
            "Runner",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (_savedTunDiagRunner != null)
            runnerProp?.SetValue(null, _savedTunDiagRunner);

        ReleaseSingletonTunLockBestEffort();
        VPNRouter.Core.AppPaths.OverrideDataDir(_savedDataDir);
        try { Directory.Delete(_testDataDir, recursive: true); } catch { }
    }

    [Fact]
    public void Restart_PreservesTunLock_BehaviourTest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — Linux/macOS paths go through pkexec / sudo " +
            "escalation chains not routed through IProcessRunner.");

        EnsureConfigDir();

        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: NewFakePid()));

        var settings = DefaultSettings();
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);

        var initialHandle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", initialHandle);
        SetField(manager, "_currentConfigPath",
            Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json"));

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        Assert.True(IsLockOwned(lockInstance),
            "Test setup could not seed the isolated lock as owned.");

        manager.Restart();

        Assert.True(IsLockOwned(lockInstance),
            "BUG: Restart() released the TUN ownership lock during the " +
            "Stop→LaunchProcess window. Path 4 (Windows graceful Kill) " +
            "in SingBoxManager.StopInternal's finally block must honour " +
            "the `releaseLock` parameter — Restart passes false to " +
            "preserve the lock for the new process. See Task #53 brief " +
            "plans/task53-singboxmanager-restart-tunlock-2026-05-21.md.");

        var newHandle = GetField(manager, "_handle") as IProcessHandle;
        Assert.NotNull(newHandle);
        Assert.False(newHandle.HasExited,
            "Restart didn't actually spawn a new process — pin Restart " +
            "ran end-to-end before checking lock state.");
        Assert.NotEqual(initialHandle.Pid, newHandle.Pid);

        SetField(manager, "_handle", null);
        initialHandle.Dispose();
        newHandle.Dispose();
    }

    [Fact]
    public void StartWithJson_ThrowingStartedSubscriber_KillsProcessAndReleasesLease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only - other platforms launch through pkexec or sudo.");

        EnsureConfigDir();
        var exe = Path.Combine(_testDataDir, "sing-box-stub.exe");
        File.WriteAllText(exe, "stub");

        var handle = new FakeProcessHandle(pid: NewFakePid());
        var runner = new FakeProcessRunner().OnStart(_ => true, _ => handle);
        var settings = new SingBoxSettings { ExecutablePath = exe, ClashApi = "127.0.0.1:9090" };
        using var manager = new SingBoxManager(
            settings, logger: null, http: new FakeHttpClient(), runner: runner);
        manager.Started += _ => throw new InvalidOperationException("subscriber failed");

        Assert.Throws<InvalidOperationException>(() => manager.StartWithJson("{}"));

        Assert.Equal(1, handle.KillCallCount);
        Assert.True(handle.HasExited);
        Assert.False(IsLockOwned(TunOwnershipLock.Instance(null)),
            "The TUN lease must be released after the failed start.");

        SetField(manager, "_handle", null);
        handle.Dispose();
    }

    [Fact]
    public void Restart_StopInternalReleasesLockOnlyWhenAsked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — path 4 (graceful Kill) is the Windows branch.");

        EnsureConfigDir();

        var runnerA = new FakeProcessRunner();
        using (var managerA = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runnerA))
        {
            var lockInstance = TunOwnershipLock.Instance(null);
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

    [Fact]
    public void Stop_PublicEntryPoint_ReleasesLockNormally_RegressionPin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — path 4 (graceful Kill) is the Windows branch.");

        EnsureConfigDir();

        var runner = new FakeProcessRunner();
        using var manager = new SingBoxManager(
            DefaultSettings(), null, new FakeHttpClient(), runner);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        var handle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", handle);

        manager.Stop();

        Assert.False(IsLockOwned(lockInstance),
            "REGRESSION: Stop() left the TUN lock owned. The bug fix " +
            "must only change path 4's behaviour when releaseLock=false " +
            "(the Restart path) — Stop()'s releaseLock=true path must " +
            "still release the lock. See Task #53 brief.");

        SetField(manager, "_handle", null);
        handle.Dispose();
    }

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

    [Fact]
    public void ReloadConfigJsonWithResult_LinuxCapabilityMode_FailedExactStop_ReturnsFalseWithRetainedHandleAndLock()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(),
            "Linux-only — exercises the capability-mode Stop branch.");

        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-failed-stop-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        EnsureConfigDir();

        var runner = new FakeProcessRunner();
        var manager = new SingBoxManager(DefaultSettings(), null, new FakeHttpClient(), runner);
        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);
        var handle = new StubbornProcessHandle(NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            var result = manager.ReloadConfigJsonWithResult("{}", forceRestart: true);

            Assert.False(result, "ReloadConfigJsonWithResult must return false when exact stop is unconfirmed.");
            Assert.Equal(SingBoxState.Failed, manager.State);
            Assert.Empty(runner.StartCalls);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.True((bool)GetField(manager, "_exactStopUnconfirmed")!);
            Assert.True(IsLockOwned(lockInstance));
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            manager.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReloadConfigJsonWithResult_DisposedOrNoLease_ReturnsFalseWithoutDiskWriteOrHttp()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-reload-guard-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        VPNRouter.Core.AppPaths.EnsureDirectories();

        var fakeHttp = new FakeHttpClient();
        var runner = new FakeProcessRunner();
        var manager = new SingBoxManager(DefaultSettings(), null, fakeHttp, runner);

        try
        {
            SetField(manager, "_ownsTunLock", false);
            var resultNoLease = manager.ReloadConfigJsonWithResult("{\"case\":\"no-lease\"}");
            var tryResultNoLease = manager.TryReloadConfigJson("{\"case\":\"try-no-lease\"}");

            Assert.False(resultNoLease, "ReloadConfigJsonWithResult must return false without lease.");
            Assert.False(tryResultNoLease, "TryReloadConfigJson must return false without lease.");

            var configPath = VPNRouter.Core.AppPaths.CurrentConfigPath;
            Assert.False(File.Exists(configPath), "Must not write config to disk when lease is not owned.");
            Assert.Empty(fakeHttp.SentRequests);

            SetField(manager, "_ownsTunLock", true);
            SetField(manager, "_disposed", 1);
            var resultDisposed = manager.ReloadConfigJsonWithResult("{\"case\":\"disposed\"}");
            var tryResultDisposed = manager.TryReloadConfigJson("{\"case\":\"try-disposed\"}");

            Assert.False(resultDisposed, "ReloadConfigJsonWithResult must return false when disposed.");
            Assert.False(tryResultDisposed, "TryReloadConfigJson must return false when disposed.");
            Assert.False(File.Exists(configPath), "Must not write config to disk when disposed.");
            Assert.Empty(fakeHttp.SentRequests);
        }
        finally
        {
            SetField(manager, "_disposed", 0);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            manager.Dispose();

            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReloadConfigJsonWithResult_SuccessfulRestart_ReturnsTrue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(),
            "Windows-only — LaunchProcess on Windows routes through IProcessRunner without pkexec.");

        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-reload-restart-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        EnsureConfigDir();

        var initialHandle = new FakeProcessHandle(pid: NewFakePid());
        var replacementHandle = new FakeProcessHandle(pid: NewFakePid());

        var runner = new FakeProcessRunner()
            .OnStart(_ => true, _ => replacementHandle);

        var settings = DefaultSettings();
        var manager = new SingBoxManager(settings, null, new FakeHttpClient(), runner);

        SetField(manager, "_handle", initialHandle);
        SetField(manager, "_currentConfigPath",
            Path.Combine(VPNRouter.Core.AppPaths.ConfigDir, "current.json"));

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        try
        {
            var result = manager.ReloadConfigJsonWithResult("{}", forceRestart: true);

            Assert.True(result, "ReloadConfigJsonWithResult must return true upon successful restart.");
            Assert.True(initialHandle.HasExited, "Old process handle must have exited.");
            Assert.Same(replacementHandle, GetField(manager, "_handle"));
            Assert.False(replacementHandle.HasExited, "New process handle must be alive.");
            Assert.Equal(SingBoxState.Running, manager.State);
            Assert.True(IsLockOwned(lockInstance), "TUN ownership lock must remain owned across successful restart.");
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            manager.Dispose();
            initialHandle.Dispose();
            replacementHandle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReloadConfigJsonWithResult_HotReloadSuccess_ReturnsTrue()
    {
        var priorDataDir = GetAppPathsDataDir();
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-hot-reload-{Guid.NewGuid():N}");
        VPNRouter.Core.AppPaths.OverrideDataDir(tempDir);
        EnsureConfigDir();

        var fakeHttp = new FakeHttpClient().Setup("/configs", "", statusCode: 204);
        var runner = new FakeProcessRunner();
        var manager = new SingBoxManager(DefaultSettings(), null, fakeHttp, runner);

        var lockInstance = TunOwnershipLock.Instance(null);
        SetLockOwnedForTest(lockInstance, manager);

        var handle = new FakeProcessHandle(pid: NewFakePid());
        SetField(manager, "_handle", handle);

        try
        {
            var result = manager.ReloadConfigJsonWithResult("{\"dns\":{}}", forceRestart: false);

            Assert.True(result, "ReloadConfigJsonWithResult must return true when Clash API hot-reload succeeds.");
            Assert.Empty(runner.StartCalls);
            Assert.Same(handle, GetField(manager, "_handle"));
            Assert.Single(fakeHttp.SentRequests);
        }
        finally
        {
            SetField(manager, "_handle", null);
            SetField(manager, "_ownsTunLock", false);
            SetField(manager, "_exactStopUnconfirmed", false);
            manager.Dispose();
            handle.Dispose();
            if (IsLockOwned(lockInstance)) lockInstance.Release();
            RestoreAppPathsDataDir(priorDataDir);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static SingBoxSettings DefaultSettings() => new()
    {
        ExecutablePath = @"C:\nonexistent\sing-box.exe",
        ClashApi = "127.0.0.1:9090"
    };

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
        semaphoreField.SetValue(lockInstance, new Semaphore(0, 1));
        ownedField!.SetValue(lockInstance, true);
    }

    private static void ReleaseSingletonTunLockBestEffort()
    {
        try
        {
            var lockInstance = TunOwnershipLock.Instance(null);
            lockInstance.Release();
            lockInstance.Dispose();
        }
        catch
        {
        }
    }

    private static int _fakePidCounter = 10000;

    private static int NewFakePid() => System.Threading.Interlocked.Increment(ref _fakePidCounter);

    private static void EnsureConfigDir()
    {
        try
        {
            Directory.CreateDirectory(VPNRouter.Core.AppPaths.ConfigDir);
        }
        catch { }
    }

    private static string? GetAppPathsDataDir()
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        return (string?)f?.GetValue(null);
    }

    private static void RestoreAppPathsDataDir(string? priorDataDir)
    {
        var f = typeof(VPNRouter.Core.AppPaths).GetField("_dataDir", BindingFlags.Static | BindingFlags.NonPublic)
             ?? typeof(VPNRouter.Core.AppPaths).GetField("_dataDirOverride", BindingFlags.Static | BindingFlags.NonPublic);
        f?.SetValue(null, priorDataDir);
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
        private volatile bool _hasExited;
        public int Pid { get; } = pid;
        public bool HasExited => _hasExited;
        public int KillCallCount { get; private set; }
        public bool DisposeCalled { get; private set; }
        public event EventHandler<string>? OutputLine { add { } remove { } }
        public event EventHandler<string>? ErrorLine { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }

        public Task<int> WaitForExitAsync(CancellationToken ct)
            => _hasExited
                ? Task.FromResult(0)
                : Task.FromException<int>(new OperationCanceledException(ct));

        public void Kill(bool entireProcessTree = true) => KillCallCount++;
        public void SuppressExitedEvent() { }
        public ProcessSnapshot? TryGetSnapshot() => null;
        public void Dispose() => DisposeCalled = true;
        public void SignalExit() => _hasExited = true;
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
