#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace VPNRouter.Core.Services;

/// <summary>Read-only counts over the current rolling window.</summary>
public sealed record ConnHealthSnapshot(
    string? Node,
    int RelayOpenAttempts,
    int RelayOpenFails,
    int ProxyStreamErrors,
    int LocalCloses,
    int Other,
    double FailureRate,
    bool WouldWarn);

/// <summary>
/// Observe-only rolling-window aggregator of <see cref="ConnLogEvent"/>s.
///
/// <para>It computes a <see cref="ConnHealthSnapshot.WouldWarn"/> flag (sustained
/// high relay-open failure rate over a minimum sample) purely for <em>calibration</em>
/// — B0 never acts on it. The warning (backlog C) and failover (backlog B) are
/// separate, later stages that consume a calibrated version of this state. Keeping
/// B0 silent is the explicit lesson from the independent review (§E7): a warning
/// shipped before the classifier is calibrated would blame the proxy for the user's
/// own local closes.</para>
///
/// <para><strong>Denominator.</strong> Failure rate is
/// <see cref="ConnHealthCategory.RelayOpenFail"/> / total relay-open
/// <em>attempts</em> (<see cref="ConnHealthCategory.RelayOpenAttempt"/>), as the
/// review's §E2 requires a defined population. Local closes and non-proxy resets
/// never enter the numerator.</para>
///
/// <para>Thread-safe: the Clash <c>/logs</c> reader records from a background loop
/// while the UI/health-tick may snapshot concurrently.</para>
/// </summary>
public sealed class ConnectionHealthState
{
    // Defaults: 5-minute observation window; need a real sample before WouldWarn
    // means anything; "high" = at least half of relay-open attempts failing.
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

    /// <summary>Label the proxy node currently in use (for per-node attribution).</summary>
    public void SetActiveNode(string? node)
    {
        lock (_gate)
            _activeNode = node;
    }

    /// <summary>Record one classified event into the window.</summary>
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

    /// <summary>Snapshot of the whole window (all nodes).</summary>
    public ConnHealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            return Build(_entries, _activeNode);
        }
    }

    /// <summary>Per-node snapshots over the window.</summary>
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

        // Clamp to [0,1]: classifier marker asymmetry (e.g. a UDP relay-open
        // failure counted while its "packet connection" attempt wording isn't)
        // can leave fails > attempts at a window edge, which would otherwise
        // render a nonsensical >100% rate. No-op for the normal fails<=attempts.
        double rate = attempts > 0 ? Math.Min(1.0, (double)fails / attempts) : 0.0;
        bool wouldWarn = attempts >= _minSample && rate >= _warnThreshold;
        return new ConnHealthSnapshot(node, attempts, fails, streamErrors, locals, other, rate, wouldWarn);
    }
}
