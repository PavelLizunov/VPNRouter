namespace VPNRouter.Core.Services;

// Commands and state announcements can change while the same TUN continues serving its counters.
internal readonly record struct AndroidTunnelEvent(string? Command, string? StateEvent, string? Session)
{
    internal void PaintIfCurrent(Func<AndroidTunnelEvent?> readCurrent, Action paint)
    {
        if (readCurrent() == this) paint();
    }
}

internal sealed class AndroidTunnelTraffic
{
    private string? _session;
    private long? _sampleMs;
    private long _down;
    private long _up;

    internal bool SetSession(string? session)
    {
        if (_session == session) return false;
        _session = session;
        _sampleMs = null;
        return true;
    }

    internal (double Down, double Up)? Measure(string? session, long sampleMs, long down, long up)
    {
        var previousMs = _sampleMs;
        if (session == null || session != _session || sampleMs < 0 ||
            (previousMs.HasValue && sampleMs <= previousMs.Value)) return null;
        var previousDown = _down;
        var previousUp = _up;
        _sampleMs = sampleMs;
        _down = down;
        _up = up;
        if (previousMs == null) return null;
        var seconds = (sampleMs - previousMs.Value) / 1000.0;
        return (Math.Max(0, down - previousDown) / seconds, Math.Max(0, up - previousUp) / seconds);
    }
}
