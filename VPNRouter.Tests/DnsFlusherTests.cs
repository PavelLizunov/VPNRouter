#nullable enable

using System.Runtime.InteropServices;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class DnsFlusherTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public void FlushInstance_HappyPath_ReturnsTrueAndRunsIpconfig()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: 0,
                Stdout: "Windows IP Configuration\n\nSuccessfully flushed the DNS Resolver Cache.\n",
                Stderr: "",
                Duration: TimeSpan.FromMilliseconds(50),
                TimedOut: false));

        var sut = new DnsFlusher(fake);
        var ok = sut.FlushInstance();

        Assert.True(ok, "Exit 0 should return true");
        Assert.Single(fake.RunCalls);
        Assert.Equal("ipconfig.exe", fake.RunCalls[0].ExecutablePath);
    }

    [Fact]
    public void FlushInstance_ArgumentCorrectness_OnlyFlushdns()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        sut.FlushInstance();

        Assert.Single(fake.RunCalls);
        var args = fake.RunCalls[0].Arguments;
        Assert.Single(args);
        Assert.Equal("/flushdns", args[0]);
    }

    [Fact]
    public void FlushInstance_NonzeroExit_ReturnsFalseDoesNotThrow()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: 1,
                Stdout: "",
                Stderr: "The requested operation requires elevation.",
                Duration: TimeSpan.FromMilliseconds(20),
                TimedOut: false));

        var sut = new DnsFlusher(fake);

        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_Timeout_ReturnsFalseDoesNotThrow()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(
                ExitCode: -1,
                Stdout: "",
                Stderr: "",
                Duration: TimeSpan.FromSeconds(5),
                TimedOut: true));

        var sut = new DnsFlusher(fake);
        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_RunnerThrows_IsCaughtReturnsFalse()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            _ => throw new InvalidOperationException("simulated runner failure"));

        var sut = new DnsFlusher(fake);
        bool? result = null;
        var ex = Record.Exception(() => result = sut.FlushInstance());

        Assert.Null(ex);
        Assert.NotNull(result);
        Assert.False(result!.Value);
    }

    [Fact]
    public void FlushInstance_Idempotent_CalledTwiceBothSucceed()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        var first = sut.FlushInstance();
        var second = sut.FlushInstance();

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, fake.RunCalls.Count);
        Assert.All(fake.RunCalls, r => Assert.Equal("ipconfig.exe", r.ExecutablePath));
    }

    [Fact]
    public void FlushInstance_OnWindows_HasReasonableTimeout()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake);
        sut.FlushInstance();

        Assert.Single(fake.RunCalls);
        var timeout = fake.RunCalls[0].Timeout;
        Assert.NotNull(timeout);
        Assert.True(timeout!.Value >= TimeSpan.FromSeconds(1),
            $"Expected ≥1s timeout, got {timeout.Value}");
        Assert.True(timeout.Value <= TimeSpan.FromSeconds(30),
            $"Expected ≤30s timeout, got {timeout.Value}");
    }

    [Fact]
    public void StaticFacade_Flush_NoThrowOnRealRuntime()
    {
        var ex = Record.Exception(() => DnsFlusher.Flush());
        Assert.Null(ex);
    }

    [Fact]
    public void FlushInstance_NativeFlusherSuccess_ReturnsTrueWithoutRunningProcess()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        var sut = new DnsFlusher(fake, nativeFlusher: () => true);
        var ok = sut.FlushInstance();

        Assert.True(ok);
        Assert.Empty(fake.RunCalls);
    }

    [Fact]
    public void FlushInstance_NativeFlusherFails_FallsBackToIpconfig()
    {
        if (!IsWindows) return;

        var fake = new FakeProcessRunner();
        fake.OnRun(
            r => r.ExecutablePath == "ipconfig.exe",
            new ProcessResult(0, "Successfully flushed the DNS Resolver Cache.", "", TimeSpan.FromMilliseconds(10), false));

        var sut = new DnsFlusher(fake, nativeFlusher: () => false);
        var ok = sut.FlushInstance();

        Assert.True(ok);
        Assert.Single(fake.RunCalls);
        Assert.Equal("ipconfig.exe", fake.RunCalls[0].ExecutablePath);
    }
}
