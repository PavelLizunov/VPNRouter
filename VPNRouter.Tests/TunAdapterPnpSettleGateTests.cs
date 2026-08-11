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
        var fake = NewTunRunner(request =>
        {
            var present = samples.Count > 0 && samples.Dequeue();
            return Ok(present ? InstanceId + "\r\n" : "No devices were found.\r\n");
        });

        await WithTunRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, "VPNRouter-TUN", "test.stable-removal");
            Assert.True(removed);
        });

        Assert.Equal(2, fake.RunCalls.Count(IsPnpScan));
        Assert.Equal(8, fake.RunCalls.Count(IsExactInstanceQuery));
        Assert.All(fake.RunCalls.Where(IsExactInstanceQuery), request =>
            Assert.Contains(InstanceId, request.Arguments));
    }

    [Fact]
    public async Task Removal_DeviceNeverDisappears_FailsClosedAfterBoundedPolls()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var fake = NewTunRunner(_ => Ok(InstanceId + "\r\n"));

        await WithTunRunnerAsync(fake, async () =>
        {
            await Assert.ThrowsAsync<TunAdapterNotReadyException>(() =>
                TunAdapterDiagnostics.TryRemoveAdapterAsync(
                    logger: null, "VPNRouter-TUN", "test.timeout"));
        });

        Assert.Equal(40, fake.RunCalls.Count(IsExactInstanceQuery));
    }

    [Fact]
    public async Task Removal_SimilarlyPrefixedInstance_DoesNotSatisfyExactIdQuery()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var fake = NewTunRunner(_ => Ok(InstanceId + "_OTHER\r\n"));

        await WithTunRunnerAsync(fake, async () =>
        {
            var removed = await TunAdapterDiagnostics.TryRemoveAdapterAsync(
                logger: null, "VPNRouter-TUN", "test.exact-id");
            Assert.True(removed);
        });

        Assert.Equal(4, fake.RunCalls.Count(IsExactInstanceQuery));
    }

    [Fact]
    public async Task LaunchProcess_PnpRescanFailure_DoesNotSpawnSingBox()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var tunRunner = NewTunRunner(_ => Ok("No devices were found.\r\n"), scanExitCode: 1);
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
        }, cleanup: () => DisposeAndDrain(manager));
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
            .OnRun(IsNetshDisable, Ok())
            .OnRun(IsResolve, async _ =>
            {
                var call = Interlocked.Increment(ref resolveCall);
                if (call == 2)
                {
                    queuedRemovalEntered.TrySetResult(true);
                    return await releaseQueuedRemoval.Task.ConfigureAwait(false);
                }
                return Ok(string.Empty);
            });

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
        }, cleanup: () => DisposeAndDrain(manager));
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
            .OnRun(IsNetshDisable, Ok())
            .OnRun(IsResolve, async _ =>
            {
                var call = Interlocked.Increment(ref resolveCall);
                if (call == 2)
                {
                    oldRemovalEntered.TrySetResult(true);
                    return await releaseOldRemoval.Task.ConfigureAwait(false);
                }
                return Ok(string.Empty);
            });

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
        });
    }

    [Fact]
    public async Task QueuedSettleFailure_IsNotClearedByLaterAlreadyAbsentResult()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var resolveCall = 0;
        var tunRunner = new FakeProcessRunner()
            .OnRun(IsResolve, _ => Task.FromResult(Ok(
                Interlocked.Increment(ref resolveCall) == 1
                    ? InstanceId + "\r\n"
                    : string.Empty)))
            .OnRun(IsPnpRemove, Ok())
            .OnRun(IsPnpScan, new ProcessResult(
                1, string.Empty, "synthetic scan failure", TimeSpan.Zero, false));
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
        }, cleanup: () => DisposeAndDrain(manager));
    }

    [Fact]
    public async Task QueuedTransientScanFailure_RecoversOnlyAfterExactIdSettles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only PnP behavior.");

        var scanCall = 0;
        var tunRunner = new FakeProcessRunner()
            .OnRun(IsResolve, Ok(InstanceId + "\r\n"))
            .OnRun(IsPnpRemove, Ok())
            .OnRun(IsPnpScan, _ => Task.FromResult(
                Interlocked.Increment(ref scanCall) == 1
                    ? new ProcessResult(1, string.Empty, "transient scan failure", TimeSpan.Zero, false)
                    : Ok()))
            .OnRun(IsExactInstanceQuery, Ok("No devices were found.\r\n"));
        var manager = NewManager(new FakeProcessRunner());

        await WithTunRunnerAsync(tunRunner, async () =>
        {
            InvokeQueue(manager, "test.queue.transient");

            var wait = typeof(SingBoxManager).GetMethod("WaitForQueuedTunAdapterRemoval",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            await Task.Run(() => wait.Invoke(manager, null));

            Assert.Equal(3, scanCall);
            Assert.Equal(4, tunRunner.RunCalls.Count(IsExactInstanceQuery));
        }, cleanup: () => DisposeAndDrain(manager));
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

    private static FakeProcessRunner NewTunRunner(
        Func<ProcessRequest, ProcessResult> instanceQuery,
        int scanExitCode = 0) =>
        new FakeProcessRunner()
            .OnRun(IsNetshShow, Ok("VPNRouter-TUN\r\n"))
            .OnRun(IsNetshDisable, Ok())
            .OnRun(IsResolve, Ok(InstanceId + "\r\n"))
            .OnRun(IsPnpRemove, Ok())
            .OnRun(IsPnpScan, new ProcessResult(
                scanExitCode, string.Empty, "synthetic scan failure", TimeSpan.Zero, false))
            .OnRun(IsExactInstanceQuery, request => Task.FromResult(instanceQuery(request)));

    private static async Task WithTunRunnerAsync(
        FakeProcessRunner fake,
        Func<Task> body,
        Action? cleanup = null)
    {
        var previousRunner = TunAdapterDiagnostics.Runner;
        var previousDelay = TunAdapterDiagnostics.RemovalDelayAsync;
        SingBoxManager.ResetTunRemovalQueueForTests();
        TunAdapterDiagnostics.ResetRemoveNetAdapterLatchForTests();
        TunAdapterDiagnostics.SetNetAdapterModuleAvailableForTests(true);
        TunAdapterDiagnostics.Runner = fake;
        TunAdapterDiagnostics.RemovalDelayAsync = static (_, _) => Task.CompletedTask;
        try
        {
            await body();
        }
        finally
        {
            cleanup?.Invoke();
            TunAdapterDiagnostics.Runner = previousRunner;
            TunAdapterDiagnostics.RemovalDelayAsync = previousDelay;
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

    private static bool IsPnpRemove(ProcessRequest request) =>
        request.ExecutablePath == "pnputil.exe" && request.Arguments.Contains("/remove-device");

    private static bool IsPnpScan(ProcessRequest request) =>
        request.ExecutablePath == "pnputil.exe" && request.Arguments.Contains("/scan-devices");

    private static bool IsExactInstanceQuery(ProcessRequest request) =>
        request.ExecutablePath == "pnputil.exe" && request.Arguments.Contains("/enum-devices");
}
