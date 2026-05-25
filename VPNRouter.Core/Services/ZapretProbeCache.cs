#nullable enable
// =============================================================================
// ZapretProbeCache — v2.37.0-r6 (2026-05-25)
//
// Persists the last successful Flowseal probe winner so returning users get
// instant warm-start instead of waiting 2-7 minutes for a fresh sweep.
//
// Cache file: %ProgramData%\VPNRouter\cache\zapret_probe.json
//
// Schema v2 (r24 — added score fields for Hero summary card):
//   {
//     "Strategy": "general (ALT3)",
//     "LastSuccessAt": "2026-05-25T01:32:36Z",
//     "LastSweepAt":   "2026-05-25T01:32:36Z",
//     "SuccessRunCount": 3,
//     "LastFailureCount": 0,
//     "TargetsPassed": 4,    // r24: how many DPI targets the winner passed
//     "TargetsTotal":  5,    // r24: how many were probed
//     "SchemaVersion": 2
//   }
//
// Schema v1 entries (no Targets* fields) keep working — JSON deserializer
// defaults the missing ints to 0 and the UI renders "N мин назад" without
// the "X из Y" line. Next sweep upgrades them to v2.
//
// Eviction policy:
//   - LastSweepAt > 7 days old → treat as stale, force full sweep.
//   - SuccessRunCount = 0 → unreliable, force full sweep.
//   - LastFailureCount >= 3 consecutive → demoted, force full sweep.
//
// Freshness tier (for Hero card):
//   - IsRecentAndReliable() → "✓ working"        (< 7 days, ≥1 success)
//   - IsStale()             → "⚠ устарела"      (> 7 days but still has data)
//   - else null entry       → "◌ не проверена"   (no cache file at all)
//
// On success → SuccessRunCount++, LastFailureCount = 0, refresh timestamps,
//              store target score from the winning probe.
// On failure → LastFailureCount++.
//
// All file ops are best-effort: cache corruption / IO errors silently degrade
// to "no cache" (caller does full sweep). Never throws into the caller.
// =============================================================================

using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class ZapretStrategyTestResult
{
    /// <summary>How many DPI test labels passed during the probe (status=OK).</summary>
    public int Passed { get; set; }
    /// <summary>Total DPI test labels run for the strategy.</summary>
    public int Total { get; set; }
    /// <summary>UTC timestamp when the strategy was tested.</summary>
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class ZapretProbeCacheEntry
{
    public string Strategy { get; set; } = string.Empty;
    public DateTime LastSuccessAt { get; set; } = DateTime.MinValue;
    public DateTime LastSweepAt { get; set; } = DateTime.MinValue;
    public int SuccessRunCount { get; set; }
    public int LastFailureCount { get; set; }

    // r24 — score from the winning strategy's DPI target probe. v1 cache
    // files don't carry these fields; deserializer defaults them to 0 and
    // the UI just omits the "X из Y" line until the next sweep upgrades.
    public int TargetsPassed { get; set; }
    public int TargetsTotal { get; set; }

    // r37 — per-strategy results from the last full sweep. Allows the Hero
    // ComboBox to badge every probed strategy (✓ 5/5 / ⚠ 3/5 / ✗ 0/5),
    // not just the winner. Keyed by strategy display name (matches the
    // string Flowseal emits in "[N/T] strategy.bat" header line). Empty
    // dict on v1/v2 cache entries — UI gracefully degrades to "no badge".
    public Dictionary<string, ZapretStrategyTestResult> PerStrategyResults { get; set; }
        = new Dictionary<string, ZapretStrategyTestResult>(StringComparer.Ordinal);

    public int SchemaVersion { get; set; } = 3;

    /// <summary>
    /// True if this cache entry is recent (sweep within 7d) and reliable
    /// (at least 1 success, fewer than 3 consecutive failures).
    /// </summary>
    public bool IsRecentAndReliable() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && SuccessRunCount > 0
        && LastFailureCount < 3
        && (DateTime.UtcNow - LastSweepAt) < TimeSpan.FromDays(7);

    /// <summary>
    /// r24 — true when we have a strategy on file but the last sweep was
    /// more than 7 days ago. UI shows the entry with a "⚠ устарела" badge
    /// and nudges the user toward re-verify, but never auto-runs the
    /// 5-minute sweep on its own (the user explicitly chose manual-refresh
    /// over background reprobes when picking this UX).
    /// </summary>
    public bool IsStale() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && (DateTime.UtcNow - LastSweepAt) > TimeSpan.FromDays(7);

    /// <summary>
    /// r24 — true when we have score data to display ("4 из 5 целей").
    /// v1 cache files load with TargetsTotal=0 and we omit the score line.
    /// </summary>
    public bool HasTargetScore() => TargetsTotal > 0;
}

/// <summary>
/// Persist + retrieve the last-known-good Zapret strategy so the magic
/// button can warm-start instead of running a 2-7 min Flowseal sweep
/// every time. Cache miss / corruption / failure all degrade gracefully
/// to "do the full sweep".
/// </summary>
public static class ZapretProbeCache
{
    private static string CachePath => Path.Combine(AppPaths.CacheDir, "zapret_probe.json");

    /// <summary>
    /// Load the cached probe entry, or null if missing / corrupt / disabled.
    /// Logs at Debug level on parse failure so the operator can grep.
    /// Never throws.
    /// </summary>
    public static ZapretProbeCacheEntry? TryLoad(ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                logger?.Debug("[ZapretProbeCache] No cache at {Path}", CachePath);
                return null;
            }
            var json = File.ReadAllText(CachePath);
            var entry = JsonSerializer.Deserialize<ZapretProbeCacheEntry>(json);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Strategy))
            {
                logger?.Debug("[ZapretProbeCache] Cache deserialized to null/empty");
                return null;
            }
            return entry;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[ZapretProbeCache] Failed to load cache (will redo sweep)");
            return null;
        }
    }

    /// <summary>
    /// Persist a fresh-sweep success. Bumps SuccessRunCount when the strategy
    /// matches the cached one, otherwise resets to 1 for the new strategy.
    /// Always resets LastFailureCount to 0 on success.
    /// </summary>
    /// <param name="strategy">Winning strategy name.</param>
    /// <param name="targetsPassed">r24 — how many DPI targets the winner
    /// passed in the verifying probe. 0 if not tracked (legacy callers).</param>
    /// <param name="targetsTotal">r24 — how many DPI targets were probed.
    /// 0 if not tracked. Cache renders the score line only when total > 0.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public static void RecordSuccess(
        string strategy,
        int targetsPassed,
        int targetsTotal,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return;
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            var existing = TryLoad(logger);
            var now = DateTime.UtcNow;
            var entry = new ZapretProbeCacheEntry
            {
                Strategy = strategy,
                LastSuccessAt = now,
                LastSweepAt = now,
                SuccessRunCount = (existing != null
                    && string.Equals(existing.Strategy, strategy, StringComparison.Ordinal))
                    ? existing.SuccessRunCount + 1
                    : 1,
                LastFailureCount = 0,
                TargetsPassed = targetsPassed,
                TargetsTotal = targetsTotal,
                SchemaVersion = 2,
            };
            WriteAtomic(entry, logger);
            logger?.Information(
                "[ZapretProbeCache] Recorded success: {Strategy} (run #{N}, score {P}/{T})",
                strategy, entry.SuccessRunCount, targetsPassed, targetsTotal);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] RecordSuccess failed");
        }
    }

    /// <summary>
    /// Backward-compat overload: pre-r24 callers that don't carry score
    /// data. Persists the success without any score; the Hero card omits
    /// the "X из Y" line for entries that came through this path.
    /// </summary>
    public static void RecordSuccess(string strategy, ILogger? logger = null)
        => RecordSuccess(strategy, 0, 0, logger);

    /// <summary>
    /// r37 — persist the full per-strategy results of a sweep alongside the
    /// winner. Each entry in <paramref name="perStrategyResults"/> records
    /// how many DPI test labels passed for that strategy. Use this so the
    /// Hero ComboBox can badge every probed strategy, not just the winner.
    ///
    /// <para>Called from <c>ZapretAutoStrategy.Sweep</c> at the end of a
    /// full sweep (including early-exit when a winner aces enough targets
    /// to be confident — the partial sweep's data is still useful for the
    /// strategies that DID get tested).</para>
    /// </summary>
    /// <param name="winner">Winning strategy name (may be empty if the
    /// sweep ended with no winner; results still get persisted).</param>
    /// <param name="winnerPassed">Winner's pass count (0 if no winner).</param>
    /// <param name="winnerTotal">Winner's probe total (0 if no winner).</param>
    /// <param name="perStrategyResults">All strategies tested during this
    /// sweep, keyed by display name. May be empty (probe failed before any
    /// strategy completed).</param>
    public static void RecordSweepResults(
        string winner,
        int winnerPassed,
        int winnerTotal,
        Dictionary<string, ZapretStrategyTestResult> perStrategyResults,
        ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            var existing = TryLoad(logger);
            var now = DateTime.UtcNow;

            // Merge per-strategy results — keep older results for strategies
            // not in THIS sweep (e.g. user picked a quick-strategy that
            // didn't run a full sweep). Newer results overwrite older.
            var merged = (existing?.PerStrategyResults != null
                ? new Dictionary<string, ZapretStrategyTestResult>(
                    existing.PerStrategyResults, StringComparer.Ordinal)
                : new Dictionary<string, ZapretStrategyTestResult>(StringComparer.Ordinal));

            if (perStrategyResults != null)
            {
                foreach (var kv in perStrategyResults)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            var hasWinner = !string.IsNullOrWhiteSpace(winner);
            var entry = new ZapretProbeCacheEntry
            {
                Strategy = hasWinner ? winner : (existing?.Strategy ?? string.Empty),
                LastSuccessAt = hasWinner ? now : (existing?.LastSuccessAt ?? DateTime.MinValue),
                LastSweepAt = now,
                SuccessRunCount = hasWinner
                    ? ((existing != null
                        && string.Equals(existing.Strategy, winner, StringComparison.Ordinal))
                        ? existing.SuccessRunCount + 1
                        : 1)
                    : (existing?.SuccessRunCount ?? 0),
                LastFailureCount = hasWinner ? 0 : (existing?.LastFailureCount ?? 0),
                TargetsPassed = winnerPassed,
                TargetsTotal = winnerTotal,
                PerStrategyResults = merged,
                SchemaVersion = 3,
            };
            WriteAtomic(entry, logger);
            logger?.Information(
                "[ZapretProbeCache] Recorded sweep results: winner={Winner} ({P}/{T}), perStrategy={N} entries",
                hasWinner ? winner : "<none>", winnerPassed, winnerTotal, merged.Count);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] RecordSweepResults failed");
        }
    }

    /// <summary>
    /// Record a failure of the cached strategy. Bumps LastFailureCount; after
    /// 3 consecutive failures the entry is no longer reliable
    /// (<see cref="ZapretProbeCacheEntry.IsRecentAndReliable"/> returns false)
    /// so the next probe runs a fresh full sweep.
    /// </summary>
    public static void RecordFailure(string strategy, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return;
        try
        {
            var existing = TryLoad(logger);
            if (existing == null
                || !string.Equals(existing.Strategy, strategy, StringComparison.Ordinal))
            {
                // No cache to demote — failure of a strategy that wasn't
                // cached is irrelevant (full sweep covers it).
                return;
            }
            existing.LastFailureCount += 1;
            existing.LastSweepAt = DateTime.UtcNow;
            WriteAtomic(existing, logger);
            logger?.Information(
                "[ZapretProbeCache] Recorded failure: {Strategy} (consecutive fails {N})",
                strategy, existing.LastFailureCount);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] RecordFailure failed");
        }
    }

    /// <summary>
    /// Erase the cache (used by tests and by manual UI reset). Idempotent.
    /// </summary>
    public static void Clear(ILogger? logger = null)
    {
        try
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
                logger?.Information("[ZapretProbeCache] Cache cleared");
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] Clear failed");
        }
    }

    private static void WriteAtomic(ZapretProbeCacheEntry entry, ILogger? logger)
    {
        var tmp = CachePath + ".tmp";
        var json = JsonSerializer.Serialize(entry,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json);
        // File.Move with overwrite=true is atomic on NTFS (replaces the
        // target file in place); on POSIX it's an unlink+rename which is
        // also atomic at the directory entry level. Both prevent torn
        // writes leaving the JSON half-flushed.
        File.Move(tmp, CachePath, overwrite: true);
    }
}
