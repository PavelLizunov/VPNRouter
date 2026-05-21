#nullable enable
// ============================================================================
// VlessDeepVerifierProcessRunnerTests.cs — Phase 3+ (2026-05-21)
// ============================================================================
//
// Pins the IProcessRunner wire shape for the sing-box probe inside
// VlessDeepVerifier.VerifyAsync. After the Phase 3+ migration the spawn
// no longer calls Process.Start directly — it routes through the
// per-instance IProcessRunner seam. This file assigns a FakeProcessRunner
// + FakeProcessHandle and asserts:
//
//   * the ProcessRequest argv shape (`run -c <temp-config>`),
//   * the stream-capture flags (CaptureStdout / CaptureStderr both true),
//   * that stderr accumulation flows via handle.ErrorLine (used for the
//     "sing-box: <snippet>" surface when the SOCKS port never binds),
//   * that the Kill-in-finally fires when the probe times out (port
//     never binds within SingBoxWarmup),
//   * that caller cancellation propagates to handle.Kill via finally,
//   * ctor injection (runner:null → static Runner default).
//
// Why this matters: a regression in the spawn wire shape would silently
// flip every Servers / Subscriptions deep-verify into a 12s timeout
// (sing-box exits immediately when its args are malformed), surfacing as
// a wave of "timeout" verdicts in the UI with no clue where the gap is.
// Pinning the argv + stderr propagation protects that path without
// invoking the real binary.
//
// Brief: plans/phase3-iprocessrunner-vlessdeepverifier-2026-05-21.md
// ============================================================================

using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// xUnit suite pinning the sing-box spawn invocation emitted by
/// <see cref="VlessDeepVerifier.VerifyAsync"/> via the per-instance
/// IProcessRunner seam. The seam is private but its observable effect
/// (an IProcessRunner.Start call with a specific ProcessRequest) is
/// asserted through the test-only ctor that accepts an explicit
/// IProcessRunner.
/// </summary>
public sealed class VlessDeepVerifierProcessRunnerTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    /// <summary>
    /// Create a temp file that <c>VlessDeepVerifier.IsAvailable</c> will
    /// accept (just needs <c>File.Exists</c> to return true — we don't
    /// actually execute the file). Returned path is deleted by the
    /// caller in a finally block.
    /// </summary>
    private static string CreateFakeSingBoxBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-sing-box-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "");
        return path;
    }

    private static VlessServerEntry CleanEntry() =>
        VlessDeepVerifierTests.CleanVlessEntry();

    // ─── 1. ProcessRequest argv shape pin ────────────────────────────────

    [Fact]
    public async Task VerifyAsync_SpawnsSingBox_WithExpectedArgvAndCaptureFlags()
    {
        // CRITICAL invariant: the sing-box spawn MUST be `<bin> run -c <temp>`
        // with stdout+stderr captured. A drift to e.g. `start -c` or
        // `--config` would silently break every deep-verify; sing-box
        // exits with a usage-error message that the verifier currently
        // reads from stderr.
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 4242);
            fake.OnStart(_ => true, _ => handle);

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);
            var testCt = TestContext.Current.CancellationToken;
            // Don't await — fire the call, then immediately snapshot the
            // ProcessRequest. The probe will sit waiting for the SOCKS
            // port to bind (it won't); kill via handle.SignalExit after.
            var probeTask = verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, testCt);

            // Give the verifier a moment to enter the spawn block. The
            // request is synchronously recorded by FakeProcessRunner before
            // VerifyAsync awaits anything I/O-bound on the spawn.
            for (var i = 0; i < 50 && fake.StartCalls.Count == 0; i++)
                await Task.Delay(20, testCt);

            Assert.Single(fake.StartCalls);
            var call = fake.StartCalls[0];
            Assert.Equal(binPath, call.ExecutablePath);
            Assert.Equal(3, call.Arguments.Count);
            Assert.Equal("run", call.Arguments[0]);
            Assert.Equal("-c", call.Arguments[1]);
            Assert.StartsWith(Path.GetTempPath(), call.Arguments[2]);
            Assert.Contains("sb-dv-", call.Arguments[2]);
            Assert.EndsWith(".json", call.Arguments[2]);
            Assert.True(call.CaptureStdout, "Stdout must be captured for sing-box error surface");
            Assert.True(call.CaptureStderr, "Stderr must be captured for the snippet displayed on failure");

            // Let the probe complete (port-bind will fail, verifier will
            // give up; handle will be killed in finally).
            await probeTask;
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // ─── 2. Stderr accumulation via handle.ErrorLine ─────────────────────

    [Fact]
    public async Task VerifyAsync_StderrFromHandle_SurfacesInFailureError()
    {
        // The verifier accumulates handle.ErrorLine events into a
        // StringBuilder and surfaces a trimmed snippet when the SOCKS
        // port never binds. Pin that path — the legacy code used
        // process.ErrorDataReceived, the new code uses handle.ErrorLine,
        // both must produce identical surface.
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            FakeProcessHandle? captured = null;
            fake.OnStart(_ => true, _ =>
            {
                captured = new FakeProcessHandle(pid: 4242);
                return captured;
            });

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);
            var testCt = TestContext.Current.CancellationToken;
            var probeTask = verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, testCt);

            // Wait for the handle to be created (the subscription is wired
            // synchronously after _runner.Start), then pump a stderr line.
            for (var i = 0; i < 50 && captured == null; i++)
                await Task.Delay(20, testCt);
            Assert.NotNull(captured);
            captured!.EmitError("FATAL: bind: address already in use");

            // Probe will time out waiting for the port to bind; the
            // resulting error surface MUST carry the stderr snippet.
            var result = await probeTask;

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
            // The snippet is wrapped as "sing-box: <text>" when stderr is
            // non-empty (vs "sing-box didn't bind" when stderr is empty).
            Assert.Contains("sing-box:", result.Error!, StringComparison.Ordinal);
            Assert.Contains("bind", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // ─── 3. Kill-in-finally fires on probe timeout ───────────────────────

    [Fact]
    public async Task VerifyAsync_PortNeverBinds_KillsHandleInFinally()
    {
        // The Kill-in-finally is the only thing keeping a hung sing-box
        // from leaking past the probe. The legacy code called
        // process.Kill(entireProcessTree:true); the new code calls
        // handle.Kill(true) — pin that the migration didn't drop the
        // call (e.g. an over-eager refactor removing it because Dispose
        // also kills, ignoring that Dispose runs after Kill in finally).
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 4242);
            fake.OnStart(_ => true, _ => handle);

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);
            var result = await verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, TestContext.Current.CancellationToken);

            Assert.False(result.Ok);
            Assert.True(handle.HasExited, "handle should be marked exited after the finally block ran");
            Assert.True(handle.KillCallCount >= 1,
                $"Kill should have been invoked at least once in finally; got {handle.KillCallCount}");
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // ─── 4. Caller cancellation propagates to handle.Kill ────────────────

    [Fact]
    public async Task VerifyAsync_CancelledMidProbe_KillsHandle()
    {
        // Caller-side cancellation must propagate into the finally block's
        // handle.Kill — otherwise a parent batch that ctrl-C's the verifier
        // would orphan sing-box processes. The OperationCanceledException
        // path catches and returns DeepVerifyResult.Failed("timeout") (the
        // legacy surface; same here).
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 4242);
            fake.OnStart(_ => true, _ => handle);

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);

            using var cts = new CancellationTokenSource();
            var probeTask = verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, cts.Token);

            // Wait for the spawn to register, then cancel.
            for (var i = 0; i < 50 && fake.StartCalls.Count == 0; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            cts.Cancel();

            var result = await probeTask;

            Assert.False(result.Ok);
            Assert.True(handle.KillCallCount >= 1,
                $"Kill should fire on caller cancellation; got {handle.KillCallCount}");
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // ─── 5. Ctor wiring: runner=null falls back to static Runner ─────────

    [Fact]
    public void Constructor_AcceptsCustomRunner_WiresUpInjection()
    {
        // Both public and internal ctors gained an optional IProcessRunner
        // parameter. Pin that the ctor doesn't ignore the argument and
        // that null falls back to the static default — same shape as
        // HostsManager + FirewallManager + TunAdapterDiagnostics.
        var fake = new FakeProcessRunner();

        // Public ctor (production resolves _singBoxPath via AppPaths).
        var withFake = new VlessDeepVerifier(SilentLogger(), fake);
        var withDefault = new VlessDeepVerifier(SilentLogger(), runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);

        // Internal test ctor (full triple).
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var withBoth = new VlessDeepVerifier(SilentLogger(), binPath, fake);
            var withDefaultRunner = new VlessDeepVerifier(SilentLogger(), binPath, runner: null);
            Assert.NotNull(withBoth);
            Assert.NotNull(withDefaultRunner);
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }
}
