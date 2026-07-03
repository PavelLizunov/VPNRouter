namespace VPNRouter.Core.Services;

/// <summary>
/// W0.1 (true-split): the pure decision for the Windows "wedge kill". A wedged sing-box
/// is alive (process-liveness true) but its Clash API stopped serving — the TUN no longer
/// forwards, so ALL traffic incl. split-EXCLUDED apps black-holes until the process dies.
/// HealthMonitor calls this each tick with the live serving probe; on a true return it
/// hard-kills sing-box (the wintun adapter dies with it → OS restores routes) and drives
/// the normal crash-recovery path. Extracted (mirrors <c>DnsLockdownPolicy</c>) so the
/// latch/streak logic is unit-testable without a real sing-box / ProgramData.
/// </summary>
internal static class WedgeKillPolicy
{
    /// <summary>
    /// Returns true when a wedge kill should fire, updating the caller's latch + streak.
    /// <paramref name="servingConfirmed"/> latches true once serving is ever observed this
    /// lifecycle — before that we never count "not serving" (it's just the TUN warm-up, or a
    /// non-default clash_api port the probe can't reach). Only after the tunnel has proven it
    /// CAN serve do <paramref name="threshold"/> consecutive not-serving ticks count as a wedge.
    /// </summary>
    public static bool ShouldKill(bool serving, ref bool servingConfirmed, ref int streak, int threshold)
    {
        if (serving) { servingConfirmed = true; streak = 0; return false; }
        if (!servingConfirmed) return false;      // never served yet → warm-up, don't count
        return ++streak >= threshold;
    }
}
