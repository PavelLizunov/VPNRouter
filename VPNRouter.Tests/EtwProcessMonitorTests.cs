#nullable enable

using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class EtwProcessMonitorTests
{
#if PLATFORM_WINDOWS
    [Fact]
    public void TranslateProcessEvent_HappyPath_ConstructsArgsVerbatim()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 4321,
            imageFileName: "Discord.exe",
            parentProcessId: 1234);

        Assert.Equal(4321, args.ProcessId);
        Assert.Equal("Discord.exe", args.ProcessName);
        Assert.Equal(1234, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_NullImageFileName_NormalisesToEmptyString()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 9999,
            imageFileName: null,
            parentProcessId: 1);

        Assert.Equal(9999, args.ProcessId);
        Assert.NotNull(args.ProcessName);
        Assert.Equal(string.Empty, args.ProcessName);
    }

    [Fact]
    public void TranslateProcessEvent_PidZero_PassesThroughForCallerFilter()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 0,
            imageFileName: "Idle",
            parentProcessId: 0);

        Assert.Equal(0, args.ProcessId);
        Assert.Equal("Idle", args.ProcessName);
        Assert.Equal(0, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_NegativePid_PassesThroughForCallerFilter()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: -1,
            imageFileName: "TransientSlot.exe",
            parentProcessId: -1);

        Assert.Equal(-1, args.ProcessId);
        Assert.Equal("TransientSlot.exe", args.ProcessName);
        Assert.Equal(-1, args.ParentProcessId);
    }

    [Fact]
    public void TranslateProcessEvent_EmptyImageFileName_PreservedAsEmpty()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 5,
            imageFileName: string.Empty,
            parentProcessId: 4);

        Assert.Equal(string.Empty, args.ProcessName);
    }

    [Fact]
    public void TranslateProcessEvent_PreservesCaseExactly()
    {
        var args = EtwProcessMonitor.TranslateProcessEvent(
            processId: 1,
            imageFileName: "MiXeDcAsE.ExE",
            parentProcessId: 0);

        Assert.Equal("MiXeDcAsE.ExE", args.ProcessName);
    }

    [Fact]
    public void Constructor_OptionalProcessRunnerSeam_AcceptsCustomFake()
    {
        if (!OperatingSystem.IsWindows()) return;

        var fake = new FakeProcessRunner();

        using var monitorWithFake = new EtwProcessMonitor(
            logger: null,
            processRunner: fake);
        using var monitorDefault = new EtwProcessMonitor();

        Assert.Empty(fake.RunCalls);
        Assert.Empty(fake.StartCalls);
    }

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        monitor.Dispose();
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;

        var monitor = new EtwProcessMonitor();
        try
        {
            monitor.Stop();
        }
        finally
        {
            monitor.Dispose();
        }
    }

    [Fact]
    public void Events_RemainUnsubscribed_NoFireWithoutSession()
    {
        if (!OperatingSystem.IsWindows()) return;

        var startedCount = 0;
        var stoppedCount = 0;

        using var monitor = new EtwProcessMonitor();
        monitor.ProcessStarted += (_, _) => Interlocked.Increment(ref startedCount);
        monitor.ProcessStopped += (_, _) => Interlocked.Increment(ref stoppedCount);

        Assert.Equal(0, startedCount);
        Assert.Equal(0, stoppedCount);
    }
#endif

    [Fact]
    public void TestClassCompilesOnAllPlatforms()
    {
        Assert.True(true);
    }
}
