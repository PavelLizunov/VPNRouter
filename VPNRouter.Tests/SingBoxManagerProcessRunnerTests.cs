#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerProcessRunnerTests : IDisposable
{
    private readonly string _previousDataDir;
    private readonly string _tempDataDir;

    public SingBoxManagerProcessRunnerTests()
    {
        _previousDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(
            Path.GetTempPath(),
            $"vpnrouter-sbm-runner-{Guid.NewGuid():N}");
        AppPaths.OverrideDataDir(_tempDataDir);
        AppPaths.EnsureDirectories();
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_previousDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); } catch { }
    }

    private static SingBoxSettings DefaultSettings(string exePath) => new()
    {
        ExecutablePath = exePath,
        ClashApi = "127.0.0.1:9090"
    };

    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath)
    {
        return new SingBoxManager(
            DefaultSettings(exePath),
            logger: null,
            http: new FakeHttpClient(),
            runner: runner);
    }

    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    [Fact]
    public void LaunchProcess_ArgvShapePin_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var spawnedRequest = (ProcessRequest?)null;
        var fakeHandle = new FakeProcessHandle(pid: 9999);
        fake.OnStart(_ => true, req =>
        {
            spawnedRequest = req;
            return fakeHandle;
        });

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);

            manager.StartWithJson("{\"log\":{\"level\":\"info\"}}");

            Assert.NotNull(spawnedRequest);
            Assert.Equal(exe, spawnedRequest!.ExecutablePath);
            Assert.True(spawnedRequest.CaptureStdout);
            Assert.True(spawnedRequest.CaptureStderr);

            Assert.Equal(3, spawnedRequest.Arguments.Count);
            Assert.Equal("run", spawnedRequest.Arguments[0]);
            Assert.Equal("-c", spawnedRequest.Arguments[1]);
            Assert.EndsWith("current.json", spawnedRequest.Arguments[2]);

            Assert.Single(fake.StartCalls);

            Assert.Equal(SingBoxState.Running, manager.State);
            Assert.Equal(9999, manager.Pid);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void StartWithJson_WhenHandleAlive_IsNoOp_NotRestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 4242);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);

            manager.StartWithJson("{\"log\":{\"level\":\"info\"}}");
            manager.StartWithJson("{\"log\":{\"level\":\"debug\"}}");

            Assert.Single(fake.StartCalls);
            Assert.False(fakeHandle.HasExited);
            Assert.Equal(0, fakeHandle.KillCallCount);
            Assert.Equal(SingBoxState.Running, manager.State);
            Assert.Equal(4242, manager.Pid);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Handle_Exited_FiresCrashed_EventBubblesToSubscriber()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 7777);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            var crashedFired = false;
            manager.Crashed += (_, _) => crashedFired = true;

            manager.StartWithJson("{}");

            fakeHandle.SignalExit(exitCode: 137);

            Assert.True(crashedFired,
                "OnProcessExited must invoke the Crashed event after IProcessHandle.Exited fires.");
            Assert.Equal(SingBoxState.Failed, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Stop_Kills_And_WaitsForExit_OnRunningHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 5555);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            Assert.False(fakeHandle.HasExited);
            Assert.Equal(0, fakeHandle.KillCallCount);

            manager.Stop();

            Assert.True(fakeHandle.HasExited);
            Assert.True(fakeHandle.KillCallCount >= 1,
                "Stop must call Kill on the running handle.");
            Assert.Equal(SingBoxState.Stopped, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Stop_IsIdempotent_AcrossLifecycle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 6666);
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            manager.Stop();
            manager.Stop();

            Assert.Equal(SingBoxState.Stopped, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Restart_PreservesTunLock_BehaviourPin()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var handles = new System.Collections.Generic.List<FakeProcessHandle>();
        fake.OnStart(_ => true, _ =>
        {
            var h = new FakeProcessHandle(pid: 10_000 + handles.Count);
            handles.Add(h);
            return h;
        });

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            Assert.Single(handles);
            Assert.False(handles[0].HasExited);

            manager.Restart();

            Assert.Equal(2, handles.Count);
            Assert.True(handles[0].HasExited,
                "Restart must kill the prior handle during its intermediate Stop step.");
            Assert.False(handles[1].HasExited,
                "Restart must spawn a fresh handle for the relaunched sing-box.");
            Assert.Equal(SingBoxState.Running, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Restart_WhenLaunchThrows_ReleasesTunLockAndSetsFailedState()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        int startCount = 0;
        fake.OnStart(_ => true, _ =>
        {
            startCount++;
            if (startCount == 2)
                throw new InvalidOperationException("Simulated second launch failure inside Restart");
            return new FakeProcessHandle(pid: 7777);
        });

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            manager.StartWithJson("{}");

            var ex = Assert.Throws<InvalidOperationException>(() => manager.Restart());
            Assert.Equal("Simulated second launch failure inside Restart", ex.Message);
            Assert.Equal(SingBoxState.Failed, manager.State);

            using var secondManager = BuildManager(fake, exe);
            var started = Record.Exception(() => secondManager.StartWithJson("{}"));
            Assert.Null(started);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }

    [Fact]
    public void Stop_WhenHandleNulled_SuppressesExitEventUsingEventCode()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();
        var fakeHandle = new FakeProcessHandle(pid: 8888) { SimulateExitedRaceLost = true };
        fake.OnStart(_ => true, _ => fakeHandle);

        var exe = CreateStubExe();
        try
        {
            using var manager = BuildManager(fake, exe);
            bool crashedFired = false;
            manager.Crashed += (_, _) => crashedFired = true;

            manager.StartWithJson("{}");
            manager.Stop();

            Assert.False(crashedFired, "Intentional stop must suppress Crashed even if Exited callback arrives late.");
            Assert.Equal(SingBoxState.Stopped, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { }
        }
    }
}
