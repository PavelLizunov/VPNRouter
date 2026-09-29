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
            var batPath = Path.Combine(ZapretUpdater.BinDir, "_vpnrouter_launch.bat");
            if (File.Exists(batPath))
            {
                try { File.Delete(batPath); } catch { }
            }
        }
        catch { }
    }

    private bool SeededOrRealExe() =>
        _seededStub || File.Exists(_winwsPath);

    [Fact]
    public void Start_RoutesThroughCmdBat_DoesNotRedirectStreams()
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

            Assert.Equal("cmd.exe", call.ExecutablePath);

            Assert.Equal(2, call.Arguments.Count);
            Assert.Equal("/c", call.Arguments[0]);
            Assert.EndsWith("_vpnrouter_launch.bat", call.Arguments[1]);
            Assert.Contains(ZapretUpdater.BinDir, call.Arguments[1]);

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
        }
        finally
        {
            sut.Dispose();
        }
    }
}
