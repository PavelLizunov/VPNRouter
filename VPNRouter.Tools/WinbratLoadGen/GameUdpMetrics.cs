#nullable enable

namespace VPNRouter.Tools.WinbratLoadGen;

public sealed record GameUdpSummary(int Sent, int Received, int Loss, int Duplicate, int Reorder, int Corruption, int Unknown, double RttP50Ms, double RttP95Ms, double RttP99Ms, double MaxAcknowledgedGapMs);

public static class GameUdpProfile
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BurstStart = TimeSpan.FromMinutes(2.5);
    public static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan NormalInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(20);

    public static TimeSpan IntervalAt(TimeSpan elapsed) =>
        elapsed >= BurstStart && elapsed < BurstStart + BurstDuration ? BurstInterval : NormalInterval;
}

public sealed class GameUdpMetrics
{
    private readonly object _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _outstanding = new();
    private readonly HashSet<long> _received = new();
    private readonly List<double> _rtts = new();
    private DateTimeOffset? _firstSentAt;
    private DateTimeOffset? _lastSentAt;
    private DateTimeOffset? _lastAcknowledgementAt;
    private long _highestSequence = -1;
    private double _maxAcknowledgedGap;
    private int _sent;
    private int _duplicate;
    private int _reorder;
    private int _corruption;
    private int _unknown;

    public void Sent(long sequence, DateTimeOffset at)
    {
        lock (_gate)
        {
            _sent++;
            _firstSentAt ??= at;
            _lastSentAt = at;
            _outstanding[sequence] = at;
        }
    }

    public void Received(long sequence, bool payloadMatches, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_received.Contains(sequence)) { _duplicate++; return; }
            if (!_outstanding.TryGetValue(sequence, out var sentAt)) { _unknown++; return; }
            if (!payloadMatches) { _corruption++; return; }

            _received.Add(sequence);
            _outstanding.Remove(sequence);
            if (sequence < _highestSequence) _reorder++; else _highestSequence = sequence;
            _rtts.Add((at - sentAt).TotalMilliseconds);
            if (_lastAcknowledgementAt is { } last) _maxAcknowledgedGap = Math.Max(_maxAcknowledgedGap, (at - last).TotalMilliseconds);
            _lastAcknowledgementAt = at;
        }
    }

    public bool HasFailureGap(DateTimeOffset now)
    {
        lock (_gate)
            return _firstSentAt is { } first &&
                _lastSentAt is { } lastSent && now - lastSent <= TimeSpan.FromMilliseconds(250) &&
                now - (_lastAcknowledgementAt ?? first) >= TimeSpan.FromSeconds(3);
    }

    public GameUdpSummary Snapshot()
    {
        lock (_gate)
            return new(_sent, _received.Count, _sent - _received.Count, _duplicate, _reorder, _corruption, _unknown, Percentile(.50), Percentile(.95), Percentile(.99), _maxAcknowledgedGap);
    }

    private double Percentile(double fraction)
    {
        if (_rtts.Count == 0) return 0;
        var sorted = _rtts.Order().ToArray();
        return sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
    }
}
