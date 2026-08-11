#nullable enable
using System.Reflection;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class TunAdapterPnpSettleGateTests
{
    private const string InstanceId = @"ROOT\NET\VPNROUTER_R3";

    [Fact]
    public async Task Removal_WaitsForStableExactInstanceAbsence()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var samples = new Queue<bool>(new[]
        {
            true, false, false, true, false, false, false, false,
        });
        var queriedIds = new List<string>();
        var fake = NewTunRunner();

        await WithTunRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, "VPNRouter-TUN", "test.stable-removal");
            Assert.True(removed);
        }, nativeQuery: id =>
        {
            queriedIds.Add(id);
            var present = samples.Count > 0 && samples.Dequeue();
            return new NativePnpPresenceResult(
                present ? NativePnpPresence.Present : NativePnpPresence.Absent,
                present ? 0u : 0x0Du);
        });

        Assert.Equal(8, queriedIds.Count);
        Assert.All(queriedIds, id => Assert.Equal(InstanceId, id));
    }

    [Fact]
    public async Task Removal_DeviceNeverDisappears_FailsClosedAfterBoundedPolls()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var queryCount = 0;
        var fake = NewTunRunner();

        await WithTunRunnerAsync(fake, async () =>
        {
            await Assert.ThrowsAsync<TunAdapterNotReadyException>(() =>
                TunAdapterDiagnostics.TryRemoveAdapterAsync(
                    logger: null, "VPNRouter-TUN", "test.timeout"));
        }, nativeQuery: _ =>
        {
            queryCount++;
            return new NativePnpPresenceResult(NativePnpPresence.Present, 0);
        });

        Assert.Equal(40, queryCount);
    }

    [Fact]
    public async Task Removal_SimilarlyPrefixedInstance_DoesNotSatisfyExactIdQuery()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var queriedIds = new List<string>();
        var fake = NewTunRunner();

        await WithTunRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, "VPNRouter-TUN", "test.exact-id");
            Assert.True(removed);
        }, nativeQuery: id =>
        {
            queriedIds.Add(id);
            return new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D);
        });

        Assert.Equal(4, queriedIds.Count);
        Assert.All(queriedIds, id => Assert.Equal(InstanceId, id));
    }

    [Fact]
    public async Task LaunchProcess_PnpRescanFailure_DoesNotSpawnSingBox()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var tunRunner = NewTunRunner();
        var processRunner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: 49031));
        SingBoxManager? manager = null;

        await WithTunRunnerAsync(tunRunner, () =>
        {
            manager = NewManager(processRunner);
            var ex = Assert.Throws<TargetInvocationException>(() =>
                InvokeLaunch(manager));
            Assert.IsType<TunAdapterNotReadyException>(ex.InnerException);
            Assert.Empty(processRunner.StartCalls);
            return Task.CompletedTask;
        }, cleanup: () => DisposeAndDrain(manager),
        nativeQuery: _ => new NativePnpPresenceResult(NativePnpPresence.Error, 5));
    }

    [Fact]
    public async Task Restart_DoesNotSpawnUntilQueuedRemovalCompletes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var resolveCall = 0;
        var queuedRemovalEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQueuedRemoval = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var tunRunner = new FakeProcessRunner()
            .OnRun(IsNetshShow, Ok(string.Empty))
            .OnRun(IsNetshDisable, Ok());

        var nextPid = 49040;
        var processRunner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: nextPid++));
        var manager = NewManager(processRunner);

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeLaunch(manager);
            Assert.Single(processRunner.StartCalls);

            var restart = Task.Run(manager.Restart);
            await queuedRemovalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(900);

            Assert.Single(processRunner.StartCalls);

            releaseQueuedRemoval.TrySetResult(Ok(string.Empty));
            await restart.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, processRunner.StartCalls.Count);
        }, cleanup: () => DisposeAndDrain(manager), nativeLookup: _ =>
        {
            var call = Interlocked.Increment(ref resolveCall);
            if (call == 2)
            {
                queuedRemovalEntered.TrySetResult(true);
                releaseQueuedRemoval.Task.GetAwaiter().GetResult();
            }
            return new NativePnpLookupResult(true, Array.Empty<string>(), null);
        });
    }

    [Fact]
    public async Task NewManagerLaunch_WaitsForPriorManagerRemoval()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var resolveCall = 0;
        var oldRemovalEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRemoval = new TaskCompletionSource<ProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tunRunner = new FakeProcessRunner()
            .OnRun(IsNetshShow, Ok(string.Empty))
            .OnRun(IsNetshDisable, Ok());

        var oldProcessRunner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: 49051));
        var newProcessRunner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: 49052));
        var oldManager = NewManager(oldProcessRunner);
        var newManager = NewManager(newProcessRunner);

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeLaunch(oldManager);
            var oldStop = Task.Run(oldManager.Stop);
            await oldRemovalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var newLaunch = Task.Run(() => InvokeLaunch(newManager));
            await Task.Delay(900);
            Assert.Empty(newProcessRunner.StartCalls);

            releaseOldRemoval.TrySetResult(Ok(string.Empty));
            await oldStop.WaitAsync(TimeSpan.FromSeconds(5));
            await newLaunch.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(newProcessRunner.StartCalls);
        }, cleanup: () =>
        {
            DisposeAndDrain(oldManager);
            DisposeAndDrain(newManager);
        }, nativeLookup: _ =>
        {
            var call = Interlocked.Increment(ref resolveCall);
            if (call == 2)
            {
                oldRemovalEntered.TrySetResult(true);
                releaseOldRemoval.Task.GetAwaiter().GetResult();
            }
            return new NativePnpLookupResult(true, Array.Empty<string>(), null);
        });
    }

    [Fact]
    public async Task QueuedSettleFailure_IsNotClearedByLaterAlreadyAbsentResult()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var resolveCall = 0;
        var tunRunner = new FakeProcessRunner()
            .OnRun(IsNetshDisable, Ok());
        var manager = NewManager(new FakeProcessRunner());

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeQueue(manager, "test.queue.first");
            InvokeQueue(manager, "test.queue.second");

            var wait = typeof(SingBoxManager).GetMethod("WaitForQueuedTunAdapterRemoval",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var ex = await Task.Run(() => Assert.Throws<TargetInvocationException>(() =>
                wait.Invoke(manager, null)));
            Assert.IsType<TunAdapterNotReadyException>(ex.InnerException);
            Assert.Equal(2, resolveCall);
        }, cleanup: () => DisposeAndDrain(manager),
        nativeQuery: _ => new NativePnpPresenceResult(NativePnpPresence.Error, 5),
        nativeLookup: _ => new NativePnpLookupResult(
            true,
            Interlocked.Increment(ref resolveCall) == 1
                ? new[] { InstanceId }
                : Array.Empty<string>(),
            null));
    }

    [Fact]
    public async Task QueuedTransientNativeQueryFailure_RecoversOnlyAfterExactIdSettles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var queryCall = 0;
        var tunRunner = new FakeProcessRunner()
            .OnRun(IsNetshDisable, Ok())
            .OnRun(IsResolve, Ok(InstanceId + "\r\n"));
        var manager = NewManager(new FakeProcessRunner());

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeQueue(manager, "test.queue.transient");

            var wait = typeof(SingBoxManager).GetMethod("WaitForQueuedTunAdapterRemoval",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Task.Run(() => wait.Invoke(manager, null));

            Assert.Equal(5, queryCall);
        }, cleanup: () => DisposeAndDrain(manager),
        nativeQuery: _ => Interlocked.Increment(ref queryCall) == 1
            ? new NativePnpPresenceResult(NativePnpPresence.Error, 5)
            : new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D));
    }

    [Fact]
    public async Task QueuedNativeRemovalFailure_BlocksLaunchWithExactInstanceId()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var tunRunner = new FakeProcessRunner()
            .OnRun(IsNetshDisable, Ok());
        var processRunner = new FakeProcessRunner()
            .OnStart(_ => true, _ => new FakeProcessHandle(pid: 49053));
        var manager = NewManager(processRunner);

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeQueue(manager, "test.queue.remove-failed");

            var invocation = await Task.Run(() =>
                Assert.Throws<TargetInvocationException>(() => InvokeLaunch(manager)));
            var failure = Assert.IsType<TunAdapterNotReadyException>(invocation.InnerException);
            Assert.Equal(InstanceId, failure.InstanceId);
            Assert.Empty(processRunner.StartCalls);
        }, cleanup: () => DisposeAndDrain(manager),
        nativeRemove: _ => new NativePnpRemovalResult(false, false, 5));
    }

    private static SingBoxManager NewManager(IProcessRunner processRunner)
    {
        var manager = new SingBoxManager(
            new SingBoxSettings { ExecutablePath = @"C:\nonexistent\sing-box.exe" },
            http: new FakeHttpClient(),
            runner: processRunner);
        typeof(SingBoxManager).GetField("_currentConfigPath",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, Path.Combine(Path.GetTempPath(), "vpnrouter-r3-current.json"));
        return manager;
    }

    private static void InvokeLaunch(SingBoxManager manager) =>
        typeof(SingBoxManager).GetMethod("LaunchProcess",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, new object[] { @"C:\nonexistent\sing-box.exe" });

    private static void InvokeQueue(SingBoxManager manager, string context) =>
        typeof(SingBoxManager).GetMethod("QueueTunAdapterRemoval",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, new object[] { context });

    private static void DisposeAndDrain(SingBoxManager? manager)
    {
        if (manager == null) return;
        manager.Dispose();
        try
        {
            typeof(SingBoxManager).GetMethod("WaitForQueuedTunAdapterRemoval",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(manager, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is TunAdapterNotReadyException)
        {
            // Expected only in the fail-closed test's synthetic scan failure.
        }
    }

    private static FakeProcessRunner NewTunRunner() =>
        new FakeProcessRunner()
            .OnRun(IsNetshShow, Ok("VPNRouter-TUN\r\n"))
            .OnRun(IsNetshDisable, Ok())
            .OnRun(IsResolve, Ok(InstanceId + "\r\n"));

    private static async Task WithTunRunnerAsync(
        FakeProcessRunner fake,
        Func<Task> body,
        Action? cleanup = null,
        Func<string, NativePnpRemovalResult>? nativeRemove = null,
        Func<string, NativePnpPresenceResult>? nativeQuery = null,
        Func<string, NativePnpLookupResult>? nativeLookup = null)
    {
        var previousRunner = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        var previousRequirement = TunAdapterDiagnostics.RequiresNativePnpApi;
        var previousRemove = TunAdapterDiagnostics.RemoveNativePnpDevice;
        var previousQuery = TunAdapterDiagnostics.QueryNativePnpPresence;
        var previousLookup = TunAdapterDiagnostics.ResolveNativePnpDeviceIds;
        SingBoxManager.ResetTunRemovalQueueForTests();
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(true);
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        TunAdapterDiagnostics.RequiresNativePnpApi = static () => true;
        TunAdapterDiagnostics.RemoveNativePnpDevice = nativeRemove ??
            (_ => new NativePnpRemovalResult(true, false, 0));
        TunAdapterDiagnostics.QueryNativePnpPresence = nativeQuery ??
            (_ => new NativePnpPresenceResult(NativePnpPresence.Absent, 0x0D));
        TunAdapterDiagnostics.ResolveNativePnpDeviceIds = nativeLookup ??
            (_ => new NativePnpLookupResult(true, new[] { InstanceId }, null));
        try
        {
            await body();
        }
        finally
        {
            cleanup?.Invoke();
            TunAdapterDiagnostics.Runner = previousRunner;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
            TunAdapterDiagnostics.RequiresNativePnpApi = previousRequirement;
            TunAdapterDiagnostics.RemoveNativePnpDevice = previousRemove;
            TunAdapterDiagnostics.QueryNativePnpPresence = previousQuery;
            TunAdapterDiagnostics.ResolveNativePnpDeviceIds = previousLookup;
            SingBoxManager.ResetTunRemovalQueueForTests();
            TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        }
    }

    private static ProcessResult Ok(string stdout = "") =>
        new(0, stdout, string.Empty, TimeSpan.Zero, false);

    private static bool IsNetshShow(ProcessRequest request) =>
        request.ExecutablePath == "netsh" && request.Arguments.Contains("show");

    private static bool IsNetshDisable(ProcessRequest request) =>
        request.ExecutablePath == "netsh" && request.Arguments.Contains("admin=disabled");

    private static bool IsResolve(ProcessRequest request) =>
        request.ExecutablePath == "powershell.exe"
        && request.Arguments.Any(argument => argument.Contains("PnPDeviceID"));

}
