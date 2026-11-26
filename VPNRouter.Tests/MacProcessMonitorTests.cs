using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Platform.macOS;

namespace VPNRouter.Tests;

public sealed class MacProcessMonitorTests
{
#if !PLATFORM_WINDOWS
    [Fact]
    public void StartWithoutSubscribers_SkipsProcessSnapshot()
    {
        var snapshots = 0;
        using var monitor = new MacProcessMonitor(null, null, () =>
        {
            Interlocked.Increment(ref snapshots);
            return [];
        });

        monitor.Start();
        monitor.Stop();

        Assert.Equal(0, Volatile.Read(ref snapshots));
    }

    [Fact]
    public void StopCancelsLongPollingInterval_AndAllowsAFreshStart()
    {
        using var snapshotTaken = new AutoResetEvent(false);
        using var monitor = new MacProcessMonitor(null, TimeSpan.FromMinutes(5), () =>
        {
            snapshotTaken.Set();
            return new Dictionary<int, ProcessEventArgs>();
        });
        monitor.ProcessStarted += (_, _) => { };

        monitor.Start();
        Assert.True(snapshotTaken.WaitOne(TimeSpan.FromSeconds(5)));
        monitor.Stop();
        monitor.Start();
        Assert.True(snapshotTaken.WaitOne(TimeSpan.FromSeconds(5)));
        monitor.Stop();

        monitor.Dispose();
        monitor.Start();
        Assert.False(snapshotTaken.WaitOne(TimeSpan.Zero));
    }
#endif
}
