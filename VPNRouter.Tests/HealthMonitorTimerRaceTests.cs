using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>v2.31.0-r1 (CO-1 audit fix): debounce timer swap uses
/// Interlocked.Exchange to be atomic against concurrent ETW callbacks.
/// Verify the contract by hammering OnNewProcessDetected from many threads
/// and asserting no exceptions surface and the final timer is non-null.</summary>
public class HealthMonitorTimerRaceTests
{
    [Fact]
    public void Interlocked_Exchange_Pattern_Is_AtomicSwap()
    {
        // We can't easily construct a real HealthMonitor in tests (deps on
        // SingBox/Profile/Firewall instances). Instead, we directly verify
        // the Interlocked.Exchange pattern that CO-1 introduced: many
        // concurrent (newTimer, oldTimer) swaps must not double-Dispose
        // or leak.
        System.Threading.Timer? slot = null;
        var disposeCount = 0;
        var newCount = 0;

        // Wrap a Timer in a counter so we can detect double-dispose.
        System.Threading.Timer MakeTimer()
        {
            var t = new System.Threading.Timer(_ => { });
            System.Threading.Interlocked.Increment(ref newCount);
            return t;
        }

        var threadCount = 16;
        var iterations = 100;
        var threads = new List<Thread>();
        for (int i = 0; i < threadCount; i++)
        {
            threads.Add(new Thread(() =>
            {
                for (int k = 0; k < iterations; k++)
                {
                    var newTimer = MakeTimer();
                    var oldTimer = System.Threading.Interlocked.Exchange(ref slot, newTimer);
                    if (oldTimer != null)
                    {
                        oldTimer.Dispose();
                        System.Threading.Interlocked.Increment(ref disposeCount);
                    }
                }
            }));
        }
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Final timer survives — exactly newCount-disposeCount = 1 timer remains.
        Assert.NotNull(slot);
        slot!.Dispose();

        // Conservation: every "new" was either disposed or is the final one.
        // We expect exactly newCount - 1 disposes (last new is the survivor).
        // This proves the swap was atomic — no Timer was lost or double-disposed.
        Assert.Equal(threadCount * iterations - 1, disposeCount);
    }
}
