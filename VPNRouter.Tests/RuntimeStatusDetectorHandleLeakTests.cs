using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// v2.31.1-r1 — RuntimeStatusDetector handle leak guard (AU-9)
// ═══════════════════════════════════════════════════════════════════════════════
//
// Pre-fix `Process.GetProcessesByName(...)` returned a Process[] where each
// entry holds a kernel handle. The detector is polled every 1–2 seconds, so
// the orphaned Process objects accumulated handles until GC mopped them up
// in batches — matching the audit's "+170 handles per VPN start/stop cycle"
// symptom. The fix routes both detector methods through `AnyProcessAlive`
// which disposes every entry deterministically in a `finally` block.
//
// We can't measure handles directly without a Win32 query, so the test is
// indirect: invoke the detector 5_000 times back-to-back and assert it
// neither throws nor leaves the process in an obviously-degraded state.
// What this regression really pins is that the public surface stays callable
// at any rate without crashing — the dispose pattern itself is a code-review
// invariant verified once at the source.
public class RuntimeStatusDetectorHandleLeakTests
{
    [Fact]
    public void IsVpnRunning_RepeatedCalls_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return; // detector is process-name based on Windows
        for (int i = 0; i < 5_000; i++)
        {
            // Result depends on whether sing-box is currently running on the
            // CI host — we don't care about the value, only that the call
            // returns without throwing and the loop completes promptly.
            _ = VPNRouter.Core.Services.RuntimeStatusDetector.IsVpnRunning();
        }
    }

    [Fact]
    public void IsZapretRunning_RepeatedCalls_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (int i = 0; i < 5_000; i++)
        {
            _ = VPNRouter.Core.Services.RuntimeStatusDetector.IsZapretRunning();
        }
    }
}
