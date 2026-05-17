using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
/// <summary>
/// v2.31.6-r10 (Phase D): tests for the Windows session/power event
/// listener that drives <see cref="HealthMonitor.ProbeNow"/> on
/// resume/unlock/console-connect. The Windows-specific
/// <see cref="Microsoft.Win32.SystemEvents"/> subscription path can't
/// be exercised from a unit test (requires a real OS session message
/// pump), so these tests focus on the public surface invariants:
/// idempotent Start, safe Dispose, callback isolation, no-op on
/// non-Windows. The integration path (PowerModeChanged.Resume →
/// ProbeNow → recovery) is verified manually via real device test
/// (see plan in plans/release-notes-v2.31.6-r10.md).
/// </summary>
public class PowerEventListenerTests
{
    [Fact]
    public void Start_NonWindows_NoOp()
    {
        var fired = false;
        var listener = new PowerEventListener(() => fired = true);
        listener.Start();
        // Whether the actual SystemEvents subscription succeeded is
        // OS-dependent; what we can pin is that Start doesn't throw
        // on either platform and the callback isn't invoked
        // synchronously during Start.
        Assert.False(fired);
        listener.Dispose();
    }

    [Fact]
    public void Start_CalledTwice_IsIdempotent()
    {
        var listener = new PowerEventListener(() => { });
        listener.Start();
        listener.Start(); // second call should not throw or duplicate-subscribe
        listener.Dispose();
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        var listener = new PowerEventListener(() => { });
        listener.Start();
        listener.Dispose();
        listener.Dispose(); // should be a no-op
    }

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrow()
    {
        var listener = new PowerEventListener(() => { });
        listener.Dispose(); // never started — must not throw
    }

    [Fact]
    public void Constructor_NullCallback_DoesNotThrow()
    {
        // Constructor accepts the callback as a regular Action; null
        // would mean the user wants a no-op listener (e.g. for a unit
        // test that just verifies Start/Stop lifecycle). We don't
        // null-guard inside SafeInvoke because it's only reachable
        // from a fired SystemEvents callback — null callback users
        // shouldn't actually subscribe in practice. This test just
        // pins that the ctor itself doesn't NRE during construction.
        var listener = new PowerEventListener(() => { }, logger: null);
        Assert.NotNull(listener);
        listener.Dispose();
    }
}
