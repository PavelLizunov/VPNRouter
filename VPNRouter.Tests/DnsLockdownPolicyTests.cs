// v2.42.0 — DnsLeakLockdown "Auto" (fail-open) decision matrix.
//
// The "Block DNS outside VPN" firewall lockdown is now a pure projection of
// live tunnel state: effective = settingEnabled AND tunnelServing. The moment
// the tunnel stops routing (sing-box crash + restart backoff, a dead/slow
// server, rapid server-switching) the lockdown is LIFTED so the user keeps
// DNS + internet, then RE-ARMED once the tunnel is confirmed serving again.
//
// This fixes the r10..r18 / v2.31.8 bug class where the lockdown was coupled to
// the session — installed after warm-up, removed only on the user's Stop — and
// nothing lifted it when the tunnel died mid-session, stranding the user with
// "no internet / endless loading" (the surito/germany diagnostics, 2026-06-11).
//
// DnsLockdownPolicy.Decide holds the whole decision so the transition matrix is
// unit-testable on any OS (the netsh side lives behind IWindowsDnsHardening).

#nullable enable

using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public class DnsLockdownPolicyTests
{
    // ---- The 8-cell truth table: (settingEnabled, tunnelServing, effective) ----
    // desired = settingEnabled && tunnelServing
    //   desired && !effective -> Enable
    //   !desired && effective -> Disable
    //   otherwise             -> None

    [Theory]
    // setting OFF: lockdown must never be armed, regardless of tunnel/effective.
    [InlineData(false, false, false, DnsLockdownAction.None)]
    [InlineData(false, true,  false, DnsLockdownAction.None)]
    [InlineData(false, false, true,  DnsLockdownAction.Disable)] // tear down a stale lockdown
    [InlineData(false, true,  true,  DnsLockdownAction.Disable)] // setting flipped off while armed
    // setting ON: lockdown follows the live serving signal.
    [InlineData(true,  false, false, DnsLockdownAction.None)]    // tunnel down, already open -> stay open
    [InlineData(true,  true,  false, DnsLockdownAction.Enable)]  // tunnel up, not yet armed -> arm
    [InlineData(true,  false, true,  DnsLockdownAction.Disable)] // tunnel DIED while armed -> FAIL OPEN
    [InlineData(true,  true,  true,  DnsLockdownAction.None)]    // tunnel up, already armed -> idempotent
    public void Decide_FullTruthTable(bool settingEnabled, bool tunnelServing, bool effective, DnsLockdownAction expected)
    {
        Assert.Equal(expected, DnsLockdownPolicy.Decide(settingEnabled, tunnelServing, effective));
    }

    // ---- The bug this fixes, stated as its own test ----

    [Fact]
    public void TunnelDies_WhileArmed_LiftsLockdown()
    {
        // User had "Block DNS outside VPN" on and a working tunnel (armed),
        // then the server went dead. The reconcile MUST lift the block so the
        // user is not stranded offline during the crash/backoff window.
        var action = DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: false, currentlyEffective: true);
        Assert.Equal(DnsLockdownAction.Disable, action);
    }

    [Fact]
    public void TunnelRecovers_ReArmsLockdown()
    {
        // After the lift, once the tunnel is confirmed serving again the next
        // tick re-arms the privacy lockdown — the user gets leak protection
        // back automatically without a manual toggle.
        var action = DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: true, currentlyEffective: false);
        Assert.Equal(DnsLockdownAction.Enable, action);
    }

    // ---- Idempotence: steady states never re-touch the firewall ----

    [Fact]
    public void SteadyServing_IsNoOp()
    {
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: true, currentlyEffective: true));
    }

    [Fact]
    public void SteadyDownAndOpen_IsNoOp()
    {
        // Tunnel down and lockdown already open — the common state during a
        // multi-attempt restart backoff. Must not thrash the firewall every tick.
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: true, tunnelServing: false, currentlyEffective: false));
    }

    [Fact]
    public void SettingOff_WhileArmed_TearsDownOnce()
    {
        // First reconcile after the user unchecks the box (still effective) -> Disable.
        Assert.Equal(DnsLockdownAction.Disable,
            DnsLockdownPolicy.Decide(settingEnabled: false, tunnelServing: true, currentlyEffective: true));
        // Subsequent reconcile (now not effective) -> None.
        Assert.Equal(DnsLockdownAction.None,
            DnsLockdownPolicy.Decide(settingEnabled: false, tunnelServing: true, currentlyEffective: false));
    }
}
