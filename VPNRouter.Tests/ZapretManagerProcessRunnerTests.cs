#nullable enable

using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class ZapretManagerProcessRunnerTests : IDisposable
{
    private readonly string _winwsPath = ZapretUpdater.WinwsExePath;
    private readonly bool _seededStub;

    public ZapretManagerProcessRunnerTests()
    {
        try
        {
            Directory.CreateDirectory(ZapretUpdater.BinDir);
            if (!File.Exists(_winwsPath))
            {
                File.WriteAllText(_winwsPath, "fake winws stub for tests");
                _seededStub = true;
            }
            else
            {
                _seededStub = false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            _seededStub = false;
        }
        catch (IOException)
        {
            _seededStub = false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_seededStub && File.Exists(_winwsPath))
            {
                var content = File.ReadAllText(_winwsPath);
                if (content == "fake winws stub for tests")
                {
                    File.Delete(_winwsPath);
                }
            }
        }
        catch { }
    }

    private bool SeededOrRealExe() =>
        _seededStub || File.Exists(_winwsPath);

    [Fact]
    public void Start_LaunchesWinwsWithSeparateArgumentsAndInheritsConsoleStreams()
    {
        if (!SeededOrRealExe()) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88001);
        fake.OnStart(_ => true, _ => handle);

        var sut = new ZapretManager(logger: null, runner: fake);
        try
        {
            sut.Start("--wf-tcp=443 --dpi-desync=fake,multisplit");

            Assert.Single(fake.StartCalls);
            var call = fake.StartCalls[0];

            Assert.Equal(ZapretUpdater.WinwsExePath, call.ExecutablePath);
            Assert.Equal(new[] { "--wf-tcp=443", "--dpi-desync=fake,multisplit" }, call.Arguments);
            Assert.Equal(ZapretUpdater.BinDir, call.WorkingDirectory);
            Assert.Equal(OperatingSystem.IsWindows(), call.ClearInheritedEnvironment);

            Assert.False(call.CaptureStdout,
                "Cygwin winws.exe needs a real console; pipe-redirected stdout breaks it.");
            Assert.False(call.CaptureStderr,
                "Cygwin winws.exe needs a real console; pipe-redirected stderr breaks it.");
        }
        finally
        {
            sut.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DriverConflictPreservesRunningHandleOnManualOrInstalledStrategyRestart(bool installed)
    {
        var runner = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88006);
        runner.OnStart(_ => true, _ => handle);
        bool conflict = false;
        using var manager = new ZapretManager(runner: runner) { HasDriverConflict = () => conflict };
        manager.Start("--wf-tcp=443");
        conflict = true;

        string bat = Path.Combine(ZapretUpdater.ZapretDir, $"general (test-{Guid.NewGuid():N}).bat");
        try
        {
            if (installed)
            {
                File.WriteAllText(bat, "start \"\" \"%BIN%winws.exe\" --wf-tcp=443");
                Assert.Throws<InvalidOperationException>(() => manager.StartFromBat(bat, "ignored"));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => manager.Start("--wf-tcp=80"));
            }
            Assert.Single(runner.StartCalls);
            Assert.Equal(0, handle.KillCallCount);
            Assert.Equal(handle.Pid, manager.Pid);
        }
        finally { if (installed) File.Delete(bat); }
    }

    [Fact]
    public async Task Start_HandleExitsWithin2sNonZero_FiresImmediateExitEvent()
    {
        if (!SeededOrRealExe()) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88002);
        fake.OnStart(_ => true, _ => handle);

        var sut = new ZapretManager(logger: null, runner: fake);
        var immediateExitFired = 0;
        sut.ImmediateExitDetected += () => Interlocked.Increment(ref immediateExitFired);

        try
        {
            sut.Start("--wf-tcp=443");

            handle.SignalExit(-1);

            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(1, immediateExitFired);
        }
        finally
        {
            sut.Dispose();
        }
    }

    [Fact]
    public void UnknownNativeProcessStateIsDistinctFromTheLegacyHasExitedFallback()
    {
        using var process = new System.Diagnostics.Process();
        using var handle = new ProcessHandle(process, new ProcessRequest("unused", []));
        Assert.True(handle.HasExited);
        Assert.Null(handle.QueryHasExited());
    }

    [Fact]
    public void FailedStopKeepsOwnershipAndBlocksReplacementUntilRetrySucceeds()
    {
        var runner = new FakeProcessRunner();
        var handle = new FakeProcessHandle { RefuseKill = true };
        runner.OnStart(_ => true, _ => handle);
        using var manager = new ZapretManager(runner: runner) { HasDriverConflict = () => false };
        manager.Start("--wf-tcp=443");

        manager.Stop();
        Assert.Equal(handle.Pid, manager.Pid);
        Assert.Throws<InvalidOperationException>(() => manager.Start("--wf-tcp=80"));
        Assert.Single(runner.StartCalls);
        Assert.Equal(0, handle.DisposeCallCount);

        handle.RefuseKill = false;
        manager.Stop();
        Assert.False(manager.IsRunning);
        Assert.Equal(1, handle.DisposeCallCount);
    }

    [Fact]
    public void Stop_OnRunningManager_KillsHandleAndDisposes()
    {
        if (!SeededOrRealExe()) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88004);
        fake.OnStart(_ => true, _ => handle);

        var sut = new ZapretManager(logger: null, runner: fake);
        try
        {
            sut.Start("--wf-tcp=443");

            Assert.True(sut.IsRunning);
            Assert.Equal(88004, sut.Pid);

            sut.Stop();

            Assert.False(sut.IsRunning);
            Assert.Null(sut.Pid);
            Assert.True(handle.HasExited);
            Assert.Equal(1, handle.KillCallCount);
            Assert.False(handle.LastKillIncludedChildren);
        }
        finally
        {
            sut.Dispose();
        }
    }
}
