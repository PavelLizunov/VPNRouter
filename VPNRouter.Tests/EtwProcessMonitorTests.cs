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

#endif
}
