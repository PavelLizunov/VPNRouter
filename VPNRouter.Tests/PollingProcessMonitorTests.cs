#nullable enable
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class PollingProcessMonitorTests
{
    [Fact]
    public void InitialSnapshot_DoesNotPublishEvents()
    {
        var calls = 0;
        var started = 0;
        using var monitor = CreateMonitor(() =>
        {
            Interlocked.Increment(ref calls);
            return Snapshot((1, "Existing"));
        });
        monitor.ProcessStarted += (_, _) => Interlocked.Increment(ref started);

        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(1)));
        monitor.Stop();

        Assert.Equal(0, started);
    }

    [Fact]
    public void SnapshotChanges_PublishStartedAndStopped_WithOriginalCase()
    {
        var calls = 0;
        ProcessEventArgs? started = null;
        ProcessEventArgs? stopped = null;
        using var startedSignal = new ManualResetEventSlim();
        using var stoppedSignal = new ManualResetEventSlim();
        using var monitor = CreateMonitor(() => Interlocked.Increment(ref calls) switch
        {
            1 => Snapshot((1, "Existing")),
            2 => Snapshot((1, "Existing"), (2, "MiXeDCase")),
            _ => Snapshot((2, "MiXeDCase"))
        });
        monitor.ProcessStarted += (_, args) =>
        {
            started = args;
            startedSignal.Set();
        };
        monitor.ProcessStopped += (_, args) =>
        {
            stopped = args;
            stoppedSignal.Set();
        };

        monitor.Start();
        Assert.True(startedSignal.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(stoppedSignal.Wait(TimeSpan.FromSeconds(1)));
        monitor.Stop();

        Assert.Equal(2, started!.ProcessId);
        Assert.Equal("MiXeDCase", started.ProcessName);
        Assert.Equal(1, stopped!.ProcessId);
        Assert.Equal(string.Empty, stopped.ProcessName);
    }

    [Fact]
    public void SnapshotFailure_RecoversWithoutPublishingRecoveryBaseline()
    {
        var calls = 0;
        var startedIds = new List<int>();
        using var signal = new ManualResetEventSlim();
        using var monitor = CreateMonitor(() => Interlocked.Increment(ref calls) switch
        {
            1 => throw new InvalidOperationException("transient snapshot failure"),
            2 => Snapshot((1, "Existing")),
            _ => Snapshot((1, "Existing"), (2, "NewProcess"))
        });
        monitor.ProcessStarted += (_, args) =>
        {
            lock (startedIds)
                startedIds.Add(args.ProcessId);
            signal.Set();
        };

        monitor.Start();
        Assert.True(signal.Wait(TimeSpan.FromSeconds(1)));
        monitor.Stop();

        Assert.Equal([2], startedIds);
    }

    [Fact]
    public void Stop_CancelsWorker_AndMonitorCanRestart()
    {
        var calls = 0;
        using var monitor = CreateMonitor(() =>
        {
            Interlocked.Increment(ref calls);
            return Snapshot();
        });

        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(1)));
        monitor.Stop();
        var stoppedAt = Volatile.Read(ref calls);
        Thread.Sleep(30);
        Assert.Equal(stoppedAt, Volatile.Read(ref calls));

        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) > stoppedAt, TimeSpan.FromSeconds(1)));
        monitor.Stop();
    }

    [Fact]
    public void StopFromEventHandler_ExitsWorkerCleanly()
    {
        var calls = 0;
        using var stopped = new ManualResetEventSlim();
        using var monitor = CreateMonitor(() => Interlocked.Increment(ref calls) == 1
            ? Snapshot()
            : Snapshot((1, "NewProcess")));
        monitor.ProcessStarted += (_, _) =>
        {
            monitor.Stop();
            stopped.Set();
        };

        monitor.Start();

        Assert.True(stopped.Wait(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void StopBeforeStart_AndDoubleDispose_AreSafe()
    {
        var monitor = CreateMonitor(() => Snapshot());
        monitor.Stop();
        monitor.Dispose();
        monitor.Dispose();
    }

    [Fact]
    public void StartAfterDispose_Throws()
    {
        var monitor = CreateMonitor(() => Snapshot());
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    private static PollingProcessMonitor CreateMonitor(Func<Dictionary<int, ProcessEventArgs>> snapshot) =>
        new(logger: null, pollInterval: TimeSpan.FromMilliseconds(5), takeSnapshot: snapshot);

    private static Dictionary<int, ProcessEventArgs> Snapshot(
        params (int ProcessId, string ProcessName)[] processes) =>
        processes.ToDictionary(
            process => process.ProcessId,
            process => new ProcessEventArgs
            {
                ProcessId = process.ProcessId,
                ProcessName = process.ProcessName
            });
}
