#nullable enable

using System;
using System.IO;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class ZapretStrategyTestResult
{
    public int Passed { get; set; }
    public int Total { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class ZapretProbeCacheEntry
{
    public string Strategy { get; set; } = string.Empty;
    public DateTime LastSuccessAt { get; set; } = DateTime.MinValue;
    public DateTime LastSweepAt { get; set; } = DateTime.MinValue;
    public int SuccessRunCount { get; set; }
    public int LastFailureCount { get; set; }

    public int TargetsPassed { get; set; }
    public int TargetsTotal { get; set; }

    public Dictionary<string, ZapretStrategyTestResult> PerStrategyResults { get; set; }
        = new Dictionary<string, ZapretStrategyTestResult>(StringComparer.Ordinal);

    public int SchemaVersion { get; set; } = 3;

    public bool IsRecentAndReliable() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && SuccessRunCount > 0
        && LastFailureCount < 3
        && (DateTime.UtcNow - LastSweepAt) < TimeSpan.FromDays(7);

    public bool IsStale() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && (DateTime.UtcNow - LastSweepAt) > TimeSpan.FromDays(7);

    public bool HasTargetScore() => TargetsTotal > 0;
}

public static class ZapretProbeCache
{
    private static string CachePath => Path.Combine(AppPaths.CacheDir, "zapret_probe.json");

    public static ZapretProbeCacheEntry? TryLoad(ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                logger?.Debug("[ZapretProbeCache] No cache at {Path}", CachePath);
                return null;
            }
            string json;
            try { json = File.ReadAllText(CachePath); }
            catch (Exception ioEx)
            {
                logger?.Warning(ioEx,
                    "[ZapretProbeCache] IO error reading cache at {Path} (will redo sweep)",
                    CachePath);
                return null;
            }

            ZapretProbeCacheEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ZapretProbeCacheEntry>(json);
            }
            catch (JsonException jsonEx)
            {
                var preview = json.Length > 100 ? json.Substring(0, 100) + "…" : json;
                logger?.Warning(jsonEx,
                    "[ZapretProbeCache] CORRUPTED JSON at {Path}: {Preview}",
                    CachePath, preview);
                return null;
            }

            if (entry == null || string.IsNullOrWhiteSpace(entry.Strategy))
            {
                logger?.Debug("[ZapretProbeCache] Cache deserialized to null/empty");
                return null;
            }
            return entry;
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] Unexpected load failure (will redo sweep)");
            return null;
        }
    }

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

    public static void RecordSuccess(string strategy, ILogger? logger = null)
        => RecordSuccess(strategy, 0, 0, logger);

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

    public static void RecordFailure(string strategy, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return;
        try
        {
            var existing = TryLoad(logger);
            if (existing == null
                || !string.Equals(existing.Strategy, strategy, StringComparison.Ordinal))
            {
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
        File.Move(tmp, CachePath, overwrite: true);
    }
}
