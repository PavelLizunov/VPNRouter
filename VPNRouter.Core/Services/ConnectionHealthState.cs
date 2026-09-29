#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

public sealed record ConnHealthSnapshot(
    string? Node,
    int RelayOpenAttempts,
    int RelayOpenFails,
    int ProxyStreamErrors,
    int LocalCloses,
    int Other,
    double FailureRate,
    bool WouldWarn);

public sealed class ConnectionHealthState
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);
    private const int DefaultMinSample = 20;
    private const double DefaultWarnThreshold = 0.5;

    private readonly TimeSpan _window;
    private readonly int _minSample;
    private readonly double _warnThreshold;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private readonly Queue<Entry> _entries = new();
    private string? _activeNode;

    private readonly record struct Entry(DateTimeOffset At, ConnHealthCategory Category, string? Node);

    public ConnectionHealthState(
        TimeSpan? window = null,
        int minSample = DefaultMinSample,
        double warnThreshold = DefaultWarnThreshold,
        Func<DateTimeOffset>? clock = null)
    {
        _window = window ?? DefaultWindow;
        _minSample = minSample;
        _warnThreshold = warnThreshold;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void SetActiveNode(string? node)
    {
        lock (_gate)
            _activeNode = node;
    }

    public void Record(ConnLogEvent ev)
    {
        if (ev is null)
            return;

        var now = _clock();
        lock (_gate)
        {
            _entries.Enqueue(new Entry(now, ev.Category, _activeNode));
            Prune(now);
        }
    }

    public ConnHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            return Build(_entries, _activeNode);
        }
    }

    public IReadOnlyList<ConnHealthSnapshot> SnapshotByNode()
    {
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            return _entries
                .GroupBy(e => e.Node)
                .Select(g => Build(g, g.Key))
                .ToList();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (_entries.Count > 0 && _entries.Peek().At < cutoff)
            _entries.Dequeue();
    }

    private ConnHealthSnapshot Build(IEnumerable<Entry> entries, string? node)
    {
        int attempts = 0, fails = 0, streamErrors = 0, locals = 0, other = 0;
        foreach (var e in entries)
        {
            switch (e.Category)
            {
                case ConnHealthCategory.RelayOpenAttempt: attempts++; break;
                case ConnHealthCategory.RelayOpenFail: fails++; break;
                case ConnHealthCategory.ProxyStreamError: streamErrors++; break;
                case ConnHealthCategory.LocalClose: locals++; break;
                default: other++; break;
            }
        }

        double rate = attempts > 0 ? Math.Min(1.0, (double)fails / attempts) : 0.0;
        bool wouldWarn = attempts >= _minSample && rate >= _warnThreshold;
        return new ConnHealthSnapshot(node, attempts, fails, streamErrors, locals, other, rate, wouldWarn);
    }
}
