#nullable enable

namespace VPNRouter.Core.Services;

/// <summary>
/// What the reconciler should do to the firewall-level DNS lockdown rules.
/// </summary>
public enum DnsLockdownAction
{
    /// <summary>Effective state already matches desired — do nothing.</summary>
    None,
    /// <summary>Install the DNS-port block rules (arm the lockdown).</summary>
    Enable,
    /// <summary>Remove the DNS-port block rules (fail open).</summary>
    Disable,
}

/// <summary>
/// Pure decision for the "Block DNS outside VPN" (DnsLeakLockdown) lifecycle.
///
/// <para><b>Why this exists (the bug it fixes).</b> Historically the firewall
/// DNS-port lockdown was coupled to the <em>session</em>: installed after the
/// warm-up probe, removed only on the user's Stop. Nothing lifted it when the
/// tunnel died mid-session — so a sing-box crash (restart backoff 5/10/20/40/80s),
/// a slow/dead server, or rapid server-switching left DNS blocked on the physical
/// interface and the user stranded offline ("no internet / endless loading").
/// This is the r10–r18 / v2.31.8 bug class.</para>
///
/// <para><b>The reframe.</b> DnsLeakLockdown is a <em>privacy</em> feature — hide
/// which domains you resolve <em>while proxying</em>. Its value exists ONLY while
/// the tunnel is confirmed routing. When the tunnel is down there is nothing to
/// leak around (no VPN traffic to correlate), so keeping DNS blocked buys zero
/// privacy and pure harm. (Contrast <c>block_on_vpn_fail</c>, the kill-switch,
/// which is deliberately fail-CLOSED.) So the lockdown becomes a pure projection
/// of live tunnel state: <c>effective = settingEnabled AND tunnelServing</c>.
/// Anything not "confirmed serving" → DNS open (fail-open / "Auto" mode).</para>
///
/// <para>This type holds only the decision so the full transition matrix is
/// unit-testable on any OS; the stateful netsh side lives in
/// <c>WindowsDnsHardening.ReconcileLockdownForHealth</c> behind the
/// <c>IWindowsDnsHardening</c> seam.</para>
/// </summary>
public static class DnsLockdownPolicy
{
    /// <summary>
    /// Decide the action given the persistent intent (<paramref name="settingEnabled"/>),
    /// the live gate (<paramref name="tunnelServing"/> — TUN confirmed routing,
    /// e.g. Clash API serving / warm-up green), and what is currently installed
    /// (<paramref name="currentlyEffective"/>). Idempotent: returns
    /// <see cref="DnsLockdownAction.None"/> whenever effective already matches
    /// the desired state, so callers only touch the firewall on real transitions.
    /// </summary>
    public static DnsLockdownAction Decide(bool settingEnabled, bool tunnelServing, bool currentlyEffective)
    {
        bool desired = settingEnabled && tunnelServing;
        if (desired && !currentlyEffective) return DnsLockdownAction.Enable;
        if (!desired && currentlyEffective) return DnsLockdownAction.Disable;
        return DnsLockdownAction.None;
    }
}
