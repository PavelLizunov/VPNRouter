#nullable enable

namespace VPNRouter.Tools.WinbratLoadGen;

public sealed record GameUdpSummary(int Sent, int Received, int Loss, int Duplicate, int Reorder, int Corruption, double RttP50Ms, double RttP95Ms, double RttP99Ms, double MaxAcknowledgedGapMs);

public sealed class GameUdpMetrics
{
    private readonly Dictionary<long, DateTimeOffset> _outstanding = new();
    private readonly HashSet<long> _received = new();
    private readonly List<double> _rtts = new();
    private long _highestSequence = -1;
    private double _maxAcknowledgedGap;
    private int _sent;
    private int _duplicate;
    private int _reorder;
    private int _corruption;

    public void Sent(long sequence, DateTimeOffset at) { _sent++; _outstanding[sequence] = at; }

    public void Received(long sequence, bool payloadMatches, DateTimeOffset at)
    {
        if (!payloadMatches) { _corruption++; return; }
        if (!_received.Add(sequence)) { _duplicate++; return; }
        if (sequence < _highestSequence) _reorder++; else _highestSequence = sequence;
        if (_outstanding.Remove(sequence, out var sentAt)) _rtts.Add((at - sentAt).TotalMilliseconds);
        if (_outstanding.Count > 0) _maxAcknowledgedGap = Math.Max(_maxAcknowledgedGap, _outstanding.Values.Max(sentAt => (at - sentAt).TotalMilliseconds));
    }

    public bool HasFailureGap(DateTimeOffset now) => _outstanding.Count > 0 && _outstanding.Values.Any(sentAt => now - sentAt >= TimeSpan.FromSeconds(3));

    public GameUdpSummary Snapshot() => new(_sent, _received.Count, _sent - _received.Count, _duplicate, _reorder, _corruption, Percentile(.50), Percentile(.95), Percentile(.99), _maxAcknowledgedGap);

    private double Percentile(double fraction)
    {
        if (_rtts.Count == 0) return 0;
        var sorted = _rtts.Order().ToArray();
        return sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
    }
}
