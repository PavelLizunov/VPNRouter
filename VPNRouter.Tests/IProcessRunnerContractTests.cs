#nullable enable
// ============================================================================
// IProcessRunnerContractTests.cs — concrete-impl contract tests
// ============================================================================
//
// 6 tests pinning the IProcessRunner contract on the concrete ProcessRunner
// impl using real `cmd` / `pwsh` / `sleep`-equivalent spawns. These prove
// the seam works end-to-end on Windows so Phase 2G can refactor remaining
// services with confidence. Per the methodology test pyramid §5, these are
// "contract tests for new abstractions" — ≥1 happy path + edge cases.
//
// FakeProcessRunner has its own coverage via the per-service tests Phase 2G
// will write; these tests focus on the concrete implementation behaviour.
//
// Brief: plans/phase2-2D-iprocessrunner-2026-05-17.md
// ============================================================================

using System.Runtime.InteropServices;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// Contract tests for <see cref="IProcessRunner"/> + <see cref="ProcessRunner"/>.
/// Uses real CLI binaries (cmd / powershell on Windows) for genuine
/// end-to-end coverage of the spawn-and-wait + spawn-and-stream paths.
/// Tests skip silently on non-Windows hosts since the v3.0 desktop target
/// is still Windows-first and the CI Mac/Linux runners would just exit
/// without coverage signal.
/// </summary>
public sealed class IProcessRunnerContractTests
{
    // Known fixtures: exit codes we expect from canned cmd invocations.
    // Keeping them named so a regression is loud.
    private const int ExpectedSuccessExitCode = 0;
    private const int ExpectedFailureExitCode = 1;

    /// <summary>
    /// CI Mac/Linux hosts skip these tests because the binaries differ
    /// (no cmd.exe). They're not "wrong" on those platforms — we just
    /// don't have the right fixtures yet. Phase 3 cross-platform port
    /// will add posix-shell equivalents.
    /// </summary>
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task RunAsync_HappyPath_ReturnsExitCodeAndStreams()
    {
        if (!IsWindows) return;

        var runner = new ProcessRunner();
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "echo hello-stdout" });

        var result = await runner.RunAsync(request);

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
        // `timeout /t 10` sleeps 10s — much longer than our 500ms cap.
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "ping", "-n", "30", "127.0.0.1" },
            Timeout: TimeSpan.FromMilliseconds(500));

        var startedAt = DateTime.UtcNow;
        var result = await runner.RunAsync(request);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(result.TimedOut, "Expected TimedOut=true on timeout");
        // Sanity: we returned in well under the natural 30s ping duration
        // — proves we actually killed it, didn't wait it out.
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

        // Wait briefly so the process is actually running, then cancel.
        await Task.Delay(100);
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
        // Emit 3 distinct lines then exit. Using `cmd /c (echo A & echo B & echo C)`
        // so we have multiple OutputDataReceived callbacks to observe.
        var request = new ProcessRequest(
            ExecutablePath: "cmd.exe",
            Arguments: new[] { "/c", "echo A & echo B & echo C" });

        var lines = new List<string>();
        using var handle = runner.Start(request);
        handle.OutputLine += (_, line) =>
        {
            // cmd's `echo` emits a trailing space before the `&` separator.
            // Trim so the assertion below pins logical content, not the
            // shell's punctuation quirks.
            lock (lines) lines.Add(line.Trim());
        };

        var exitCode = await handle.WaitForExitAsync(CancellationToken.None);

        // Give the OutputDataReceived dispatcher a beat to flush after exit.
        await Task.Delay(200);

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

        // Wait a beat to ensure the process is running before kill.
        await Task.Delay(100);
        Assert.False(handle.HasExited);

        handle.Kill();

        // Exited may fire on a threadpool thread; wait up to 2s with cancel.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await exitedSignal.Task.WaitAsync(timeoutCts.Token);

        Assert.True(handle.HasExited);
        Assert.NotNull(observedExitCode);
        // Killed processes return a non-zero exit code. Don't pin a specific
        // value because Windows reports varied codes depending on whether
        // we killed via taskkill vs TerminateProcess.
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

        // Verify the process is alive before disposing.
        await Task.Delay(100);
        Assert.False(handle.HasExited);

        // Dispose should kill cleanly without throwing.
        handle.Dispose();

        // Re-dispose: must not throw (idempotent).
        handle.Dispose();

        // Give the OS a brief window to clean up the process. After dispose
        // the process should be dead. We use Process.GetProcessById which
        // throws when the PID is no longer valid as a proxy for "dead".
        await Task.Delay(300);

        var stillAlive = false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            stillAlive = !p.HasExited;
        }
        catch (ArgumentException)
        {
            // PID no longer maps to a running process — that's the success case.
            stillAlive = false;
        }
        Assert.False(stillAlive, $"Process PID {pid} survived Dispose");
    }
}
