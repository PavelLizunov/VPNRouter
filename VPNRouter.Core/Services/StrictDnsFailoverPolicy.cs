#nullable enable

namespace VPNRouter.Core.Services;

/// <summary>
/// What the reconciler should do to the StrictDns "all DNS via tunnel" routing.
/// </summary>
public enum StrictDnsAction
{
    /// <summary>Effective state already matches desired — do nothing.</summary>
    None,
    /// <summary>Suppress StrictDns: regenerate with <c>dns.final = local-dns</c>
    /// (DoH on the real NIC) so DNS resolves while the tunnel can't carry it.</summary>
    FailOpen,
    /// <summary>Re-arm StrictDns: regenerate with <c>dns.final = vpn-dns</c>
    /// (DoH through the tunnel) now that the proxy is reachable again.</summary>
    ReArm,
}

/// <summary>
/// Pure decision for the StrictDns ("Strict DNS — all DNS via VPN") runtime
/// failover (v2.42.0).
///
/// <para><b>The bug it fixes.</b> StrictDns forces <c>dns.final = vpn-dns</c> — every
/// DNS query (even for apps not in the routing list) rides a DoH server reachable
/// only <em>through the proxy/tunnel</em>. When the selected server is dead or slow
/// (the germany "endless loading / no internet" report, 2026-06-11 logs), ALL DNS
/// hangs on that dead path and the whole machine loses name resolution — there is no
/// fallback. StrictDns is a privacy feature (hide which domains you resolve while
/// proxying); when the tunnel can't carry DNS there is nothing to protect, so forcing
/// DNS through it buys zero privacy and pure harm. So it becomes a projection of live
/// proxy reachability: keep it armed only while the proxy is actually reachable,
/// fail open to a direct resolver otherwise, and re-arm on recovery.</para>
///
/// <para><b>Scope.</b> Only when StrictDns is the <em>sole</em> driver of
/// <c>vpn-dns</c> — split/include mode. Full-tunnel and exclude mode legitimately
/// route ALL traffic through the tunnel, so their DNS must stay on <c>vpn-dns</c>
/// regardless; the caller passes <c>strictDnsSoleDriver=false</c> there and this
/// policy never suppresses. (Sibling of <see cref="DnsLockdownPolicy"/>, which does
/// the same fail-open reframe for the firewall-level DNS-port lockdown.)</para>
///
/// <para>Holds only the decision so the transition matrix is unit-testable on any OS;
/// the probe + config regen + hot-reload live in <c>HealthMonitor</c>.</para>
/// </summary>
public static class StrictDnsFailoverPolicy
{
    /// <summary>
    /// Decide the action given whether StrictDns is the sole driver of vpn-dns
    /// (<paramref name="strictDnsSoleDriver"/>), whether the proxy is reachable
    /// right now (<paramref name="proxyHealthy"/> — a live Clash delay probe), and
    /// whether we have currently suppressed StrictDns
    /// (<paramref name="currentlyFailedOver"/>). Idempotent: returns
    /// <see cref="StrictDnsAction.None"/> whenever effective already matches desired,
    /// so the caller only regenerates + reloads on a real transition.
    /// </summary>
    public static StrictDnsAction Decide(bool strictDnsSoleDriver, bool proxyHealthy, bool currentlyFailedOver)
    {
        // We want StrictDns suppressed exactly when it's the sole driver AND the
        // proxy is unreachable. Anything else → StrictDns honoured as configured.
        bool shouldSuppress = strictDnsSoleDriver && !proxyHealthy;

        if (shouldSuppress && !currentlyFailedOver) return StrictDnsAction.FailOpen;
        if (!shouldSuppress && currentlyFailedOver) return StrictDnsAction.ReArm;
        return StrictDnsAction.None;
    }
}
