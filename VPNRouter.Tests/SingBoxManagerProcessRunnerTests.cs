// Phase 3+ (2026-05-21) — wire-shape tests pinning the IProcessRunner
// adoption on SingBoxManager's LaunchProcess + Stop paths.
//
// Predecessors:
// - VlessDeepVerifierProcessRunnerTests (commit 34bbeae) — long-lived,
//   single spawn, no Exited event.
// - TgProxyManagerProcessRunnerTests (commit 8a5079e) — long-lived,
//   Exited event wired, OutputLine/ErrorLine stats parser.
//
// SingBoxManager is the heaviest target: full Exited→Crashed event hop,
// Linux pkexec/macOS sudo wrapping (argv tokens differ per-platform),
// the StopInternal kill+wait+dispose sequence that used to carry the
// EnableRaisingEvents=false-before-Kill pattern explicitly. After this
// migration, the pattern is implicit (lives in ProcessHandle.Dispose).
//
// Brief: plans/phase3-iprocessrunner-singboxmanager-2026-05-21.md

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

/// <summary>
/// Behaviour pins for the Phase 3+ IProcessRunner adoption on
/// <see cref="SingBoxManager"/>. These tests exercise the spawn path
/// through a <see cref="FakeProcessRunner"/> — the ProcessRequest
/// argv shape, the Exited→Crashed event hop, and the
/// Stop/Restart kill+wait sequence. The lifecycle is intentionally
/// covered as black-box at the seam boundary; deep behaviour inside
/// LaunchProcess (TUN adapter cleanup, log rotation, JSON write) is
/// already pinned by the source-string suites
/// (<see cref="SingBoxManagerRestartTunHandshakeTests"/>) and isn't
/// re-tested here.
/// </summary>
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
        try { Directory.Delete(_tempDataDir, recursive: true); } catch { /* best-effort */ }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static SingBoxSettings DefaultSettings(string exePath) => new()
    {
        ExecutablePath = exePath,
        ClashApi = "127.0.0.1:9090"
    };

    /// <summary>
    /// Construct a SingBoxManager wired to a FakeProcessRunner and a
    /// stub IHttpClient (no Clash API calls in these tests).
    /// </summary>
    private static SingBoxManager BuildManager(IProcessRunner runner, string exePath)
    {
        return new SingBoxManager(
            DefaultSettings(exePath),
            logger: null,
            http: new FakeHttpClient(),
            runner: runner);
    }

    /// <summary>
    /// Write a dummy sing-box binary file (the manager's File.Exists
    /// guard requires it). The file content is irrelevant — the fake
    /// runner never executes the path.
    /// </summary>
    private static string CreateStubExe()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sbm-stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "stub");
        return tmp;
    }

    // ─── 1. LaunchProcess argv shape ────────────────────────────────────

    [Fact]
    public void LaunchProcess_ArgvShapePin_Windows()
    {
        // On Windows the spawn is `<sing-box.exe> run -c <currentConfig>`.
        // Phase 3+ replaces the legacy single-string `Arguments` field
        // with an argv list. Pin the new shape.
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

            // Build a minimal config that ConfigGenerator.Serialize accepts
            // — we don't probe the JSON content here; the spawn shape is
            // what's pinned. JSON is written to %ProgramData%/VPNRouter/
            // config/current.json (production location); the FakeRunner
            // never reads it.
            manager.StartWithJson("{\"log\":{\"level\":\"info\"}}");

            Assert.NotNull(spawnedRequest);
            Assert.Equal(exe, spawnedRequest!.ExecutablePath);
            Assert.True(spawnedRequest.CaptureStdout);
            Assert.True(spawnedRequest.CaptureStderr);

            // argv: ["run", "-c", <currentConfigPath>] — exactly 3 tokens
            // on Windows direct spawn (no sudo / pkexec wrapper).
            Assert.Equal(3, spawnedRequest.Arguments.Count);
            Assert.Equal("run", spawnedRequest.Arguments[0]);
            Assert.Equal("-c", spawnedRequest.Arguments[1]);
            Assert.EndsWith("current.json", spawnedRequest.Arguments[2]);

            // The runner saw exactly one Start call (the LaunchProcess
            // path is a single spawn site — no fallback spawns).
            Assert.Single(fake.StartCalls);

            // Manager state advanced to Running once Start succeeded.
            Assert.Equal(SingBoxState.Running, manager.State);
            Assert.Equal(9999, manager.Pid);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 2. Exited → Crashed event mapping ──────────────────────────────

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
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Handle_Exited_FiresCrashed_EventBubblesToSubscriber()
    {
        // Phase 3+: the legacy `_process.Exited += OnProcessExited` is
        // now `_handle.Exited += OnProcessExited`. The OnProcessExited
        // body fires the Crashed event for downstream consumers
        // (HealthMonitor's auto-restart loop).
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

            // Simulate sing-box crash (non-zero exit code).
            fakeHandle.SignalExit(exitCode: 137);

            // Drain the threadpool tick so the Exited handler runs.
            // FakeProcessHandle invokes Exited synchronously inside
            // SignalExit, so this is immediate.
            Assert.True(crashedFired,
                "OnProcessExited must invoke the Crashed event after IProcessHandle.Exited fires.");
            Assert.Equal(SingBoxState.Failed, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 3. Stop kill+wait sequence ─────────────────────────────────────

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

            // Pre-Stop: handle is alive, KillCallCount == 0.
            Assert.False(fakeHandle.HasExited);
            Assert.Equal(0, fakeHandle.KillCallCount);

            manager.Stop();

            // Stop's Windows-graceful kill path:
            //   _handle.Kill(true);
            //   _handle.WaitForExitAsync(5s).GetAwaiter().GetResult();
            //   _handle.Dispose();
            // The FakeProcessHandle's Kill sets exit synchronously, so
            // KillCallCount went to ≥1 and HasExited is true. Dispose
            // doesn't add another Kill (the IDisposable race is fine).
            Assert.True(fakeHandle.HasExited);
            Assert.True(fakeHandle.KillCallCount >= 1,
                "Stop must call Kill on the running handle.");
            Assert.Equal(SingBoxState.Stopped, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 4. Stop idempotency post-migration ─────────────────────────────

    [Fact]
    public void Stop_IsIdempotent_AcrossLifecycle()
    {
        // Pin: calling Stop twice is safe post-migration. The first call
        // fires the kill+wait+dispose chain; the second call hits the
        // `_handle == null` cleanup-only branch.
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
            manager.Stop();   // second call must not throw

            Assert.Equal(SingBoxState.Stopped, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 5. Restart preserves TUN lock (companion to source pin) ────────

    [Fact]
    public void Restart_PreservesTunLock_BehaviourPin()
    {
        // The source-pin `Restart_PreservesTunLock_SourcePin` in
        // SingBoxManagerStateMachineTests pins that Restart calls
        // StopInternal(releaseLock: false) — not the public Stop. Add a
        // behaviour-side companion: after Restart, the SingBoxManager
        // is back in Running state with a fresh handle, and the original
        // handle was killed during the intermediate Stop.
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

            // After start: handles[0] alive, state Running.
            Assert.Single(handles);
            Assert.False(handles[0].HasExited);

            manager.Restart();

            // After Restart: handles[0] killed (via intermediate Stop),
            // handles[1] alive, state Running again.
            Assert.Equal(2, handles.Count);
            Assert.True(handles[0].HasExited,
                "Restart must kill the prior handle during its intermediate Stop step.");
            Assert.False(handles[1].HasExited,
                "Restart must spawn a fresh handle for the relaunched sing-box.");
            Assert.Equal(SingBoxState.Running, manager.State);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    // ─── 6. Static Runner seam regression pin ───────────────────────────

    [Fact]
    public void Construction_Runner_Default_IsProductionProcessRunner()
    {
        // Pin the seam: the static Runner property defaults to a
        // production ProcessRunner. Tests can swap it, but the default
        // must not be a fake (otherwise the production path would lose
        // its real-process semantics on a stale fake leak).
        var defaultRunner = SingBoxManager.Runner;

        Assert.NotNull(defaultRunner);
        Assert.IsType<ProcessRunner>(defaultRunner);
    }

    [Fact]
    public void Construction_RunnerParameter_OverridesStatic()
    {
        // Pin: passing `runner:` to the ctor wires the instance to that
        // runner — NOT the static Runner property. Tests rely on this to
        // avoid mutating static state across the parallel xUnit run.
        var injected = new FakeProcessRunner();
        injected.OnStart(_ => true, _ => new FakeProcessHandle());

        var sbm = new SingBoxManager(
            new SingBoxSettings { ClashApi = "127.0.0.1:9090", ExecutablePath = "ignored" },
            logger: null,
            http: new FakeHttpClient(),
            runner: injected);

        // Reach in via reflection — the field is private. The shape pin
        // is: when ctor receives a runner, the manager's `_runner` field
        // points at it (not at the static Runner default).
        var runnerField = typeof(SingBoxManager).GetField(
            "_runner",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runnerField);

        Assert.Same(injected, runnerField!.GetValue(sbm));
        sbm.Dispose();
    }

    // ─── 7. SEC-1.2-02 & SEC-1.2-01 Regression Tests ─────────────────────

    [Fact]
    public void Restart_WhenLaunchThrows_ReleasesTunLockAndSetsFailedState()
    {
        // SEC-1.2-02: when LaunchProcess throws inside Restart, State must be set to Failed
        // and _tunLock released so the global named semaphore is not permanently leaked.
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

            // Verify _tunLock was released by verifying a new manager can acquire it:
            using var secondManager = BuildManager(fake, exe);
            // If the lock was not released, StartWithJson would throw TunOwnershipException
            var started = Record.Exception(() => secondManager.StartWithJson("{}"));
            Assert.Null(started);
        }
        finally
        {
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Stop_WhenHandleNulled_SuppressesExitEventUsingEventCode()
    {
        // SEC-1.2-01: verify that when Exited event carries code -1, even if _handle
        // was already disposed/nulled by StopInternal, suppression correctly engages.
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
            try { File.Delete(exe); } catch { /* best-effort */ }
        }
    }
}
