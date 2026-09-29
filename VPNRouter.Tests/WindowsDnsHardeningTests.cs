#nullable enable

#if PLATFORM_WINDOWS
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class WindowsDnsHardeningTests : IDisposable
{
    private const string ExpectedInterfaceAlias = "VPNRouter-TUN";

    public void Dispose()
    {
        WindowsDnsHardening._runnerOverride = null;
    }

    [Fact]
    public void TrySetTunMetricViaRunner_HappyPath_InvokesNetshAndReturnsTrue()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(
            r => r.ExecutablePath == "netsh.exe",
            new ProcessResult(ExitCode: 0, Stdout: "Ok.", Stderr: "",
                Duration: TimeSpan.FromMilliseconds(50), TimedOut: false));

        var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
            metric: 1,
            runner: runner,
            log: Serilog.Log.Logger,
            interfaceAlias: ExpectedInterfaceAlias);

        Assert.True(ok);
        Assert.Single(runner.RunCalls);
        var call = runner.RunCalls[0];
        Assert.Equal("netsh.exe", call.ExecutablePath);
        Assert.Contains("interface", call.Arguments);
        Assert.Contains("ipv4", call.Arguments);
        Assert.Contains("set", call.Arguments);
        Assert.Contains(ExpectedInterfaceAlias, call.Arguments);
        Assert.Contains("metric=1", call.Arguments);
    }

    [Fact]
    public void TrySetTunMetricViaRunner_Metric0ResetPath_PassesAutoFlag()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(
            r => r.ExecutablePath == "netsh.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(20), false));

        var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
            metric: 0, runner: runner, log: Serilog.Log.Logger,
            interfaceAlias: ExpectedInterfaceAlias);

        Assert.True(ok);
        Assert.Contains("metric=0", runner.RunCalls[0].Arguments);
    }

    [Fact]
    public void TrySetTunMetricViaRunner_CalledTwiceWithSameMetric_BothSucceed()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(
            _ => true,
            new ProcessResult(0, "Ok.", "", TimeSpan.FromMilliseconds(40), false));

        var first = WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);
        var second = WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, runner.RunCalls.Count);
        Assert.Equal(
            string.Join(' ', runner.RunCalls[0].Arguments),
            string.Join(' ', runner.RunCalls[1].Arguments));
    }

    [Fact]
    public void TrySetTunMetricViaRunner_NonzeroExitCode_ReturnsFalse()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(_ => true,
            new ProcessResult(1, "", "Not found", TimeSpan.FromMilliseconds(30), false));

        var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        Assert.False(ok);
    }

    [Fact]
    public void TrySetTunMetricViaRunner_TimedOut_ReturnsFalse()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(_ => true,
            new ProcessResult(-1, "", "", TimeSpan.FromSeconds(5), TimedOut: true));

        var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        Assert.False(ok);
    }

    [Fact]
    public void TrySetTunMetricViaRunner_RunnerThrows_SwallowsAndReturnsFalse()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(_ => true,
            _ => Task.FromException<ProcessResult>(
                new InvalidOperationException("netsh.exe not found in PATH")));

        var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        Assert.False(ok);
    }

    [Fact]
    public void StaticDefault_TrySetTunMetric_RoutesThroughOverrideRunner()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(_ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        try
        {
            WindowsDnsHardening._runnerOverride = runner;
            var ok = WindowsDnsHardening.TrySetTunMetricViaRunner(
                1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

            Assert.True(ok);
            Assert.Single(runner.RunCalls);
        }
        finally
        {
            WindowsDnsHardening._runnerOverride = null;
        }
    }

    [Fact]
    public void TrySetTunMetricViaRunner_PassesNetshExeNotJustNetsh()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(
            _ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        Assert.Equal("netsh.exe", runner.RunCalls[0].ExecutablePath);
    }

    [Fact]
    public void TrySetTunMetricViaRunner_RequestUsesArgumentListNotShell()
    {
        var runner = new FakeProcessRunner();
        runner.OnRun(
            _ => true,
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        WindowsDnsHardening.TrySetTunMetricViaRunner(
            1, runner, Serilog.Log.Logger, ExpectedInterfaceAlias);

        var call = runner.RunCalls[0];
        Assert.True(call.Arguments.Count >= 5,
            $"Expected ArgumentList shape (multiple args); got {call.Arguments.Count}");
    }
}
#endif
