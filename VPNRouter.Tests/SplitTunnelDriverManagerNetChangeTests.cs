using System;
using System.Threading;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Regression for the r2 P1 daemon crash found by live testing on windows-brat: a NIC-change burst
/// (TUN up/down on every sing-box crash/restart, disconnect, or NIC flap) fired
/// <see cref="SplitTunnelDriverManager"/>.OnNetworkAddressChanged, whose debounce Task disposed its own
/// <see cref="CancellationTokenSource"/> in a finally while <c>_debounceCts</c> still referenced it — so
/// the NEXT event's <c>Interlocked.Exchange(ref _debounceCts, fresh)?.Cancel()</c> hit a DISPOSED CTS
/// and threw <see cref="ObjectDisposedException"/> SYNCHRONOUSLY on the NetworkChange callback thread,
/// outside the Task's try/catch, crashing the whole VPNRouter process (the daemon HealthMonitor lives in,
/// so sing-box then never auto-recovered). The fix makes the SUPERSEDER own the CTS's disposal (the Task
/// no longer disposes), so <c>_debounceCts</c> never references a disposed CTS.
///
/// <para>Windows-only: the manager is <c>[SupportedOSPlatform("windows")]</c> and the crash is on the
/// Windows <c>NetworkChange</c> path. Uses the <c>RaiseNetworkAddressChangedForTest</c> internal seam
/// (InternalsVisibleTo) to fire the handler synchronously without a real NIC event.</para>
/// </summary>
public class SplitTunnelDriverManagerNetChangeTests
{
    [Fact]
    public void NetworkAddressChanged_BurstAndAfterTaskCompletes_NeverThrows()
    {
        if (!OperatingSystem.IsWindows()) return; // the crash was on the Windows NetworkChange path

        using var mgr = new SplitTunnelDriverManager();

        // 1. Tight burst: each event supersedes + disposes the prior CTS. A regression to the
        //    Task-disposes-its-own-CTS pattern (or an unguarded cancel-after-dispose) throws here.
        var burst = Record.Exception(() =>
        {
            for (int i = 0; i < 50; i++)
                mgr.RaiseNetworkAddressChangedForTest();
        });
        Assert.Null(burst);

        // 2. The exact r2 crash scenario: let the last debounce Task complete (past the 2 s debounce),
        //    THEN fire again. In the buggy code the completed Task had already disposed the CTS still
        //    referenced by _debounceCts, so this second event's synchronous Cancel() crashed the process.
        Thread.Sleep(TimeSpan.FromSeconds(2.5));
        var afterComplete = Record.Exception(() => mgr.RaiseNetworkAddressChangedForTest());
        Assert.Null(afterComplete);
    }
}
