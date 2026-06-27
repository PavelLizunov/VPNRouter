using System;

namespace VPNRouter.Core.Services;

/// <summary>
/// RB4 (2026-06-27): decides when the UDP proxy path (<c>proxy-udp</c>) has
/// collapsed badly enough that the connect path should re-probe + fail over to a
/// live UDP server (which RB1 then selects). It is deliberately CONSERVATIVE and
/// RATE-LIMITED so it can never cause reconnect storms — even before its
/// thresholds are tuned against a live game repro:
/// <list type="bullet">
///   <item>fires only when the UDP path is essentially DEAD for the window —
///   many timeouts AND <em>zero</em> successes; transient flakiness (any success)
///   never fires;</item>
///   <item>at most one failover per <see cref="_cooldown"/> (storm guard).</item>
/// </list>
/// Pure + deterministic: the clock is injected, so it is fully unit-testable.
///
/// <para><strong>Not yet wired at runtime.</strong> The real-time proxy-udp
/// "no recent network activity" signal it needs does not exist in the app today
/// (B0's snapshot doesn't track it and B0 is default-off). See
/// <c>plans/roblox-reliability-RB1-RB4-2026-06-27.md</c> (RB4) for the wiring plan;
/// the trigger is staged for a live-tuning session, not shipped blind.</para>
/// </summary>
public sealed class UdpDegradationDetector
{
    private readonly int _minTimeouts;
    private readonly TimeSpan _cooldown;
    private DateTimeOffset? _lastFireUtc;

    /// <param name="minTimeouts">proxy-udp timeouts in the window required to fire (default 30).</param>
    /// <param name="cooldown">minimum gap between failovers (default 10 min) — the storm guard.</param>
    public UdpDegradationDetector(int minTimeouts = 30, TimeSpan? cooldown = null)
    {
        if (minTimeouts < 1) throw new ArgumentOutOfRangeException(nameof(minTimeouts));
        _minTimeouts = minTimeouts;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Given the proxy-udp timeout/success counts observed in the current rolling
    /// window, decide whether to fail the UDP path over. Returns true at most once
    /// per cooldown, and only when the path is fully dead (timeouts ≥ threshold and
    /// successes == 0). A true result records the fire time for the cooldown.
    /// </summary>
    public bool ShouldFailover(int udpTimeouts, int udpSuccesses, DateTimeOffset nowUtc)
    {
        // Fully dead only: many timeouts AND zero successes. Any success => the path
        // is merely flaky, not dead — never fail over (would churn working sessions).
        if (udpTimeouts < _minTimeouts || udpSuccesses > 0)
            return false;

        // Storm guard: one failover per cooldown.
        if (_lastFireUtc is { } last && nowUtc - last < _cooldown)
            return false;

        _lastFireUtc = nowUtc;
        return true;
    }
}
