#nullable enable

using VPNRouter.Core.Services;

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
    public void SessionBufferSizeMB_IsBoundedTo4MiB()
    {
        Assert.Equal(4, EtwProcessMonitor.SessionBufferSizeMB);
    }

    [Fact]
    public void StartWithNoSubscribers_SkipsKernelSessionAndDisposeIsIdempotent()
    {
        var monitor = new EtwProcessMonitor();
        monitor.Start();
        monitor.Stop();
        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void StopDuringProviderSetup_RetainsWorkerAndDisposesTraceWithoutProcessing()
    {
        using var settingUp = new ManualResetEventSlim();
        using var completeSetup = new ManualResetEventSlim();
        using var processEntered = new ManualResetEventSlim();
        using var stopRequested = new ManualResetEventSlim();
        var trace = new TestTrace(processEntered, stopRequested);
        var starts = 0;
        using var monitor = new EtwProcessMonitor(() =>
        {
            Interlocked.Increment(ref starts);
            settingUp.Set();
            if (!completeSetup.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return trace;
        });
        monitor.ProcessStarted += (_, _) => { };

        try
        {
            monitor.Start();
            Assert.True(settingUp.Wait(TimeSpan.FromSeconds(5)));
            monitor.Stop();
            monitor.Start();
            Assert.Equal(1, Volatile.Read(ref starts));
        }
        finally
        {
            completeSetup.Set();
            monitor.Stop();
        }

        Assert.True(trace.Disposed);
        Assert.False(processEntered.IsSet);
    }

    [Fact]
    public void StopAfterProviderSetup_UnblocksProcessingAndDisposesTrace()
    {
        using var processEntered = new ManualResetEventSlim();
        using var stopRequested = new ManualResetEventSlim();
        var trace = new TestTrace(processEntered, stopRequested);
        using var monitor = new EtwProcessMonitor(() => trace);
        monitor.ProcessStarted += (_, _) => { };
        monitor.Start();

        Assert.True(processEntered.Wait(TimeSpan.FromSeconds(5)));
        monitor.Stop();

        Assert.True(stopRequested.IsSet);
        Assert.True(trace.Disposed);
    }

    private sealed class TestTrace(ManualResetEventSlim processEntered, ManualResetEventSlim stopRequested)
        : EtwProcessMonitor.IProcessTrace
    {
        internal bool Disposed { get; private set; }

        public void Process()
        {
            processEntered.Set();
            if (!stopRequested.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }

        public void Stop() => stopRequested.Set();
        public void Dispose() => Disposed = true;
    }

#endif
}
