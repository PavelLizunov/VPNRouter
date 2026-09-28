using System;

namespace VPNRouter.Core.Services;

public sealed class UdpDegradationDetector
{
    private readonly int _minTimeouts;
    private readonly TimeSpan _cooldown;
    private DateTimeOffset? _lastFireUtc;

    public UdpDegradationDetector(int minTimeouts = 30, TimeSpan? cooldown = null)
    {
        if (minTimeouts < 1) throw new ArgumentOutOfRangeException(nameof(minTimeouts));
        _minTimeouts = minTimeouts;
        _cooldown = cooldown ?? TimeSpan.FromMinutes(10);
    }

    public bool ShouldFailover(int udpTimeouts, int udpSuccesses, DateTimeOffset nowUtc)
    {
        if (udpTimeouts < _minTimeouts || udpSuccesses > 0)
            return false;

        if (_lastFireUtc is { } last && nowUtc - last < _cooldown)
            return false;

        _lastFireUtc = nowUtc;
        return true;
    }
}
