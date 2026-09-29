#nullable enable

using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class VlessDeepVerifierProcessRunnerTests
{
    private static ILogger SilentLogger() => new LoggerConfiguration().CreateLogger();

    private static string CreateFakeSingBoxBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-sing-box-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, "");
        return path;
    }

    private static VlessServerEntry CleanEntry() =>
        VlessDeepVerifierTests.CleanVlessEntry();

    [Fact]
    public async Task VerifyAsync_SpawnsSingBox_WithExpectedArgvAndCaptureFlags()
    {
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 4242);
            fake.OnStart(_ => true, _ => handle);

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);
            var testCt = TestContext.Current.CancellationToken;
            var probeTask = verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, testCt);

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

            await probeTask;
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    [Fact]
    public async Task VerifyAsync_StderrFromHandle_SurfacesInFailureError()
    {
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

            for (var i = 0; i < 50 && captured == null; i++)
                await Task.Delay(20, testCt);
            Assert.NotNull(captured);
            captured!.EmitError("FATAL: bind: address already in use");

            var result = await probeTask;

            Assert.False(result.Ok);
            Assert.NotNull(result.Error);
            Assert.Contains("sing-box:", result.Error!, StringComparison.Ordinal);
            Assert.Contains("bind", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    [Fact]
    public async Task VerifyAsync_PortNeverBinds_KillsHandleInFinally()
    {
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

    [Fact]
    public async Task VerifyAsync_CancelledMidProbe_KillsHandle()
    {
        var binPath = CreateFakeSingBoxBinary();
        try
        {
            var fake = new FakeProcessRunner();
            var handle = new FakeProcessHandle(pid: 4242);
            fake.OnStart(_ => true, _ => handle);

            var verifier = new VlessDeepVerifier(SilentLogger(), binPath, fake);

            using var cts = new CancellationTokenSource();
            var probeTask = verifier.VerifyAsync(CleanEntry(), measureBandwidth: false, cts.Token);

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

    [Fact]
    public void Constructor_AcceptsCustomRunner_WiresUpInjection()
    {
        var fake = new FakeProcessRunner();

        var withFake = new VlessDeepVerifier(SilentLogger(), fake);
        var withDefault = new VlessDeepVerifier(SilentLogger(), runner: null);

        Assert.NotNull(withFake);
        Assert.NotNull(withDefault);

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
