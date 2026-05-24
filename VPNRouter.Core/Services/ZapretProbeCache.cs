#nullable enable
// =============================================================================
// ZapretProbeCache — v2.37.0-r6 (2026-05-25)
//
// Persists the last successful Flowseal probe winner so returning users get
// instant warm-start instead of waiting 2-7 minutes for a fresh sweep.
//
// Cache file: %ProgramData%\VPNRouter\cache\zapret_probe.json
//
// Schema:
//   {
//     "Strategy": "general (ALT3)",
//     "LastSuccessAt": "2026-05-25T01:32:36Z",
//     "LastSweepAt":   "2026-05-25T01:32:36Z",
//     "SuccessRunCount": 3,
//     "LastFailureCount": 0,
//     "SchemaVersion": 1
//   }
//
// Eviction policy:
//   - LastSweepAt > 7 days old → treat as stale, force full sweep.
//   - SuccessRunCount = 0 → unreliable, force full sweep.
//   - LastFailureCount >= 3 consecutive → demoted, force full sweep.
//
// On success → SuccessRunCount++, LastFailureCount = 0, refresh timestamps.
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

public sealed class ZapretProbeCacheEntry
{
    public string Strategy { get; set; } = string.Empty;
    public DateTime LastSuccessAt { get; set; } = DateTime.MinValue;
    public DateTime LastSweepAt { get; set; } = DateTime.MinValue;
    public int SuccessRunCount { get; set; }
    public int LastFailureCount { get; set; }
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// True if this cache entry is recent (sweep within 7d) and reliable
    /// (at least 1 success, fewer than 3 consecutive failures).
    /// </summary>
    public bool IsRecentAndReliable() =>
        !string.IsNullOrWhiteSpace(Strategy)
        && SuccessRunCount > 0
        && LastFailureCount < 3
        && (DateTime.UtcNow - LastSweepAt) < TimeSpan.FromDays(7);
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
    public static void RecordSuccess(string strategy, ILogger? logger = null)
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
                SchemaVersion = 1,
            };
            WriteAtomic(entry, logger);
            logger?.Information(
                "[ZapretProbeCache] Recorded success: {Strategy} (run #{N})",
                strategy, entry.SuccessRunCount);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[ZapretProbeCache] RecordSuccess failed");
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
