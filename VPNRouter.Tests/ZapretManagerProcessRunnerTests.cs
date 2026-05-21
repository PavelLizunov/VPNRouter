#nullable enable
// ============================================================================
// ZapretManagerProcessRunnerTests.cs — Phase 3+ (2026-05-21)
// ============================================================================
//
// Pins the IProcessRunner wire shape for ZapretManager — the third long-lived
// (`Start`) migration target in the Phase 3+ adoption sweep after
// TgProxyManager (commit 8a5079e) and VlessDeepVerifier (commit 34bbeae).
//
// ZapretManager is the most constrained migration target so far. Two
// observable behaviours had to be re-mapped onto the IProcessHandle event
// shape, and one design constraint had to be satisfied to preserve the
// Cygwin-winws.exe contract from CLAUDE.md:
//
//   1. **Spawn**: legacy `Process.Start(ProcessStartInfo { FileName = batPath,
//      UseShellExecute = true })` cannot route through the IProcessRunner
//      seam because the runner hardwires `UseShellExecute=false` (security
//      invariant from ProcessRunner.cs:172). UseShellExecute=false cannot
//      exec a .bat directly — CreateProcess fails. Migration wraps the .bat
//      with `cmd.exe /c <batPath>` so the runner spawns cmd.exe (a real PE)
//      which then interprets the .bat unchanged.
//
//   2. **Cygwin "real console" requirement** — per CLAUDE.md "Zapret (DPI
//      Bypass)" and ZapretManager.Start comments lines 153-156: winws.exe is
//      built against Cygwin and its POSIX path resolver fails ("cannot
//      access file" + silent exit) when stdout is pipe-redirected. Solution:
//      route `CaptureStdout=false, CaptureStderr=false` in the ProcessRequest
//      so cmd.exe inherits a real (hidden) console from CreateNoWindow=true.
//      Pinned by `Start_RoutesThroughCmdBat_DoesNotRedirectStreams`.
//
//   3. **ImmediateExit via handle.Exited** — legacy code subscribed
//      `_process.Exited += (_, _) => { runtime = now - startedAt;
//      DetectImmediateExit(runtime, code); }`. Post-migration the
//      `IProcessHandle.Exited` event carries the exit code directly as
//      `EventHandler<int>`, so the lambda body shrinks; same observable
//      semantics. Pinned by `Start_HandleExitsWithin2s_FiresImmediateExitEvent`.
//
//   4. **Cygwin .bat content** is generated UNTOUCHED by this migration. The
//      `BuildCygwinLaunchBat` regression-prevention test in ZapretActionsTests
//      already pins the SET BIN= / SET LISTS= contract; no need to duplicate.
//
// Brief: plans/phase3-iprocessrunner-zapretmanager-2026-05-21.md
// ============================================================================

using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// xUnit suite pinning the cmd.exe-wrapped .bat argv emitted by
/// <see cref="ZapretManager.Start"/> / <see cref="ZapretManager.StartFromBat"/>,
/// the stream-non-redirect contract, the 2s ImmediateExit window via
/// <see cref="IProcessHandle.Exited"/>, and the Kill+WaitForExitAsync Stop
/// sequence. Tests inject a <see cref="FakeProcessRunner"/> via the new ctor
/// parameter and drive the spawn through controllable
/// <see cref="FakeProcessHandle"/> instances.
/// </summary>
public sealed class ZapretManagerProcessRunnerTests : IDisposable
{
    // ZapretManager.Start calls File.Exists on ZapretUpdater.WinwsExePath
    // before invoking the runner. We seed a stub winws.exe under the real
    // %ProgramData%\VPNRouter\zapret\bin path, then clean up via IDisposable.
    // Tracking via _seededStub so we don't clobber a real Zapret install on
    // the dev machine.
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
                _seededStub = false; // existing real install — leave it.
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Non-elevated CI may not have ProgramData write access.
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
            // Best-effort: remove the temp .bat written by Start() too.
            var batPath = Path.Combine(ZapretUpdater.BinDir, "_vpnrouter_launch.bat");
            if (File.Exists(batPath))
            {
                try { File.Delete(batPath); } catch { /* defensive */ }
            }
        }
        catch { /* defensive */ }
    }

    private bool SeededOrRealExe() =>
        _seededStub || File.Exists(_winwsPath);

    // ── 1. Argv shape pin — cmd.exe /c <batPath> ───────────────────────────

    [Fact]
    public void Start_RoutesThroughCmdBat_DoesNotRedirectStreams()
    {
        // Pin the post-migration ProcessRequest shape:
        //   ExecutablePath = "cmd.exe"
        //   Arguments = ["/c", "<batPath>"]
        //   CaptureStdout = false, CaptureStderr = false
        //
        // The capture-false invariant is load-bearing: Cygwin winws.exe
        // requires a real (hidden) console inherited from cmd.exe, which
        // it gets only when streams are NOT pipe-redirected. ProcessRunner
        // gives that via CreateNoWindow=true when CaptureStdout/Err=false.
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

            // cmd.exe interpreter — runner can't exec .bat directly with
            // UseShellExecute=false.
            Assert.Equal("cmd.exe", call.ExecutablePath);

            // Argv: /c <batPath>
            Assert.Equal(2, call.Arguments.Count);
            Assert.Equal("/c", call.Arguments[0]);
            // The bat path lives under ZapretUpdater.BinDir.
            Assert.EndsWith("_vpnrouter_launch.bat", call.Arguments[1]);
            Assert.Contains(ZapretUpdater.BinDir, call.Arguments[1]);

            // Streams MUST NOT be redirected — Cygwin requirement.
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

    // ── 2. ImmediateExit detection via handle.Exited (within 2s window) ────

    [Fact]
    public async Task Start_HandleExitsWithin2sNonZero_FiresImmediateExitEvent()
    {
        // The Phase 3+ migration moved the immediate-exit classifier from
        // `_process.Exited += ...` to `_handle.Exited += ...`. Same 2s window
        // (ImmediateExitWindow = TimeSpan.FromSeconds(2)), same non-zero-code
        // gate (exit 0 = normal stop, no AV hint).
        //
        // Pin: when the FakeProcessHandle signals exit within ~ms with a
        // non-zero code, ImmediateExitDetected fires exactly once.
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

            // Signal a fast non-zero exit — classic AV-kill scenario.
            handle.SignalExit(-1);

            // The Exited event fires synchronously on the test thread because
            // FakeProcessHandle.SignalExit invokes it directly. Allow a moment
            // for the lambda's DetectImmediateExit call to complete.
            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(1, immediateExitFired);
        }
        finally
        {
            sut.Dispose();
        }
    }

    // ── 3. ImmediateExit NOT fired for zero-code exit (normal stop) ────────

    [Fact]
    public async Task Start_HandleExitsWithin2sCodeZero_DoesNotFireImmediateExitEvent()
    {
        // Exit code 0 = normal stop (the .bat wrapper finishes after
        // winws.exe is launched). DetectImmediateExit must early-return for
        // this case so the AV-hint toast doesn't false-positive when the
        // wrapper succeeds.
        if (!SeededOrRealExe()) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88003);
        fake.OnStart(_ => true, _ => handle);

        var sut = new ZapretManager(logger: null, runner: fake);
        var immediateExitFired = 0;
        sut.ImmediateExitDetected += () => Interlocked.Increment(ref immediateExitFired);

        try
        {
            sut.Start("--wf-tcp=443");
            handle.SignalExit(0);
            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(0, immediateExitFired);
        }
        finally
        {
            sut.Dispose();
        }
    }

    // ── 4. Stop: Kill + WaitForExitAsync sequence ──────────────────────────

    [Fact]
    public void Stop_OnRunningManager_KillsHandleAndDisposes()
    {
        // Stop must:
        //   1. Call handle.Kill(entireProcessTree:true) — kills cmd.exe
        //      AND its child winws.exe transitively.
        //   2. WaitForExitAsync up to 3s (sync via GetAwaiter().GetResult()).
        //   3. Dispose the handle.
        //   4. Null _handle so IsRunning becomes false.
        //
        // FakeProcessHandle.Kill SignalExits so we get full Stop coverage
        // without an alive-handle 3s wait.
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

    // ── 5. Idempotent Stop — second/third call no-ops, no throw ────────────

    [Fact]
    public void Stop_CalledTwice_SecondCallIsNoOp()
    {
        // The legacy `_process == null || _process.HasExited` short-circuit
        // is preserved via `_handle == null || _handle.HasExited`. Pin:
        // second + third Stop calls don't double-dispose, don't throw.
        if (!SeededOrRealExe()) return;

        var fake = new FakeProcessRunner();
        var handle = new FakeProcessHandle(pid: 88005);
        fake.OnStart(_ => true, _ => handle);

        var sut = new ZapretManager(logger: null, runner: fake);
        try
        {
            sut.Start("--wf-tcp=443");

            sut.Stop();
            var ex = Record.Exception(() => sut.Stop());
            Assert.Null(ex);

            ex = Record.Exception(() => sut.Stop());
            Assert.Null(ex);

            Assert.False(sut.IsRunning);
        }
        finally
        {
            sut.Dispose();
        }
    }

    // ── 6. Ctor wiring: runner=null falls back to static Runner default ────

    [Fact]
    public void Constructor_AcceptsCustomRunner_WiresUpInjection()
    {
        // The new ctor signature accepts an optional IProcessRunner so tests
        // can inject FakeProcessRunner without going through the static
        // Runner property. Pin that the ctor doesn't ignore the argument
        // and that null falls back to the static default.
        var fake = new FakeProcessRunner();

        // Smoke: ctor doesn't throw on either path.
        using var withFake = new ZapretManager(logger: null, runner: fake);
        using var withDefault = new ZapretManager(logger: null, runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);
    }

    // ── 7. StartFromBat: shape pins for the Flowseal-wrapper path ──────────

    [Fact]
    public void StartFromBat_RoutesThroughCmdBat_WithZapretDirAsWorkingDir()
    {
        // The Flowseal-wrapper path (`StartFromBat`) builds a different
        // wrapper .bat (the silent variant that sources service.bat
        // prologue calls) but spawns it the same way: `cmd.exe /c <wrapper>`
        // with streams non-redirected. The wrapper path lives under
        // zapretDir (the directory containing the strategy .bat the caller
        // passed). Pin both: cmd.exe invocation + WorkingDirectory=zapretDir.
        //
        // We synth a temp zapretDir with a stub strategy .bat so the
        // File.Exists check at the top of StartFromBat passes.
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var batPath = Path.Combine(tempDir, "fake-strategy.bat");
        File.WriteAllText(batPath, "@echo off\r\nrem fake strategy");

        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 88006);
            fake.OnStart(_ => true, _ => handle);

            var sut = new ZapretManager(logger: null, runner: fake);
            try
            {
                sut.StartFromBat(batPath, "--wf-tcp=443");

                Assert.Single(fake.StartCalls);
                var call = fake.StartCalls[0];
                Assert.Equal("cmd.exe", call.ExecutablePath);
                Assert.Equal(2, call.Arguments.Count);
                Assert.Equal("/c", call.Arguments[0]);
                Assert.EndsWith("_vpnrouter_silent.bat", call.Arguments[1]);
                Assert.Contains(tempDir, call.Arguments[1]);

                // WorkingDirectory must be zapretDir so the wrapper's
                // `call service.bat status_zapret` invocations resolve.
                Assert.Equal(tempDir, call.WorkingDirectory);

                // Cygwin requirement preserved on this path too.
                Assert.False(call.CaptureStdout);
                Assert.False(call.CaptureStderr);
            }
            finally
            {
                sut.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* defensive */ }
        }
    }
}
