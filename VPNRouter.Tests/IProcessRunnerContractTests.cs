#nullable enable

using System.Runtime.InteropServices;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class IProcessRunnerContractTests
{
    private const int ExpectedSuccessExitCode = 0;
    private const int ExpectedFailureExitCode = 1;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsExitCodeAndStreams()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "echo hello-stdout" });

        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedSuccessExitCode, result.ExitCode);
        Assert.Contains("hello-stdout", result.Stdout);
        Assert.False(result.TimedOut);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_TimeoutExceeded_KillsAndReturnsTimedOut()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "ping", "-n", "30", "127.0.0.1" },
            Timeout: TimeSpan.FromMilliseconds(500));

        var startedAt = DateTime.UtcNow;
        var result = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(result.TimedOut, "Expected TimedOut=true on timeout");
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"Expected fast kill; took {elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task RunAsync_CancellationRequested_KillsAndThrows()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "ping", "-n", "30", "127.0.0.1" });

        using var cts = new CancellationTokenSource();
        var startedAt = DateTime.UtcNow;
        var task = runner.RunAsync(request, cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        var elapsed = DateTime.UtcNow - startedAt;
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"Expected fast kill on cancel; took {elapsed.TotalSeconds}s");
    }

    [Fact]
    public async Task Start_LongRunning_FiresOutputLineEvents()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "echo A & echo B & echo C" });

        var lines = new List<string>();
        using var handle = runner.Start(request);
        handle.OutputLine += (_, line) =>
        {
            lock (lines) lines.Add(line.Trim());
        };

        var exitCode = await handle.WaitForExitAsync(TestContext.Current.CancellationToken);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedSuccessExitCode, exitCode);
        lock (lines)
        {
            Assert.Contains("A", lines);
            Assert.Contains("B", lines);
            Assert.Contains("C", lines);
        }
    }

    [Fact]
    public async Task Start_Killed_TriggersExitedWithSpecificCode()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "ping", "-n", "30", "127.0.0.1" });

        int? observedExitCode = null;
        var exitedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle = runner.Start(request);
        handle.Exited += (_, code) =>
        {
            observedExitCode = code;
            exitedSignal.TrySetResult(true);
        };

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(handle.HasExited);

        handle.Kill();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await exitedSignal.Task.WaitAsync(timeoutCts.Token);

        Assert.True(handle.HasExited);
        Assert.NotNull(observedExitCode);
        Assert.NotEqual(0, observedExitCode!.Value);
    }

    [Fact]
    public async Task Start_DisposeBeforeExit_KillsCleanly()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "ping", "-n", "30", "127.0.0.1" });

        var handle = runner.Start(request);
        var pid = handle.Pid;
        Assert.True(pid > 0);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(handle.HasExited);

        handle.Dispose();

        handle.Dispose();

        await Task.Delay(300, TestContext.Current.CancellationToken);

        var stillAlive = false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            stillAlive = !p.HasExited;
        }
        catch (ArgumentException)
        {
            stillAlive = false;
        }
        Assert.False(stillAlive, $"Process PID {pid} survived Dispose");
    }
}
