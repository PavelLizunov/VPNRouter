// Phase 3E (2026-05-18) — CacheMergeStage.
//
// Merges the in-memory entry list with the persisted cache. Two passes:
//
//   1. Inherit-cache-status — for every fresh entry that ALSO exists in
//      the cache, copy historical fields (FirstSeenAt, Status, LatencyMs,
//      LastTestedAt, ...) so the user's earned verification effort doesn't
//      vanish on every Refresh.
//
//   2. Preserve-previous-validation — for cached entries that are NOT in
//      the fresh pool, keep the entry alive when it is either Verified
//      (gold) or recent-Ok (TCP+TLS pass within the last 24h). Drops
//      everything else.
//
// The merge runs BEFORE TestStage so the test-skip-recent gate sees the
// inherited LastTestedAt. The merge also runs AFTER TestStage as the
// final cache write — that path is owned by the orchestrator + TestStage
// itself (incremental saves every 50 tests / 5 s). This stage is therefore
// a pre-test cache-merge; the post-test save is part of TestStage.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 5 of the Free Configs pipeline. Merges fresh entries with the
/// on-disk cache so the user's historical Verified + recent-Ok entries
/// survive Refresh cycles. Not optional — losing the cache merge would
/// regress the v2.28.3-r5 fix that motivated
/// <see cref="FreeConfigAggregator.PreservePreviousValidation"/>.
/// </summary>
public sealed class CacheMergeStage : IFreeConfigStage
{
    /// <inheritdoc />
    public string Name => "cache-merge";

    /// <inheritdoc />
    public bool Optional => false;

    /// <inheritdoc />
    public Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        var existing = ctx.Cache.Load();
        var existingById = existing.Configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        // The Input list may be the read-only output of FetchStage's pool
        // path — materialise into a mutable list so we can append
        // preserved-from-cache entries.
        var configs = new List<FreeConfigEntry>(ctx.Input);
        var byId = configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        // Pass 1: inherit cache status / latency / timestamps for entries
        // that exist in both pool and cache.
        foreach (var cfg in configs)
        {
            if (existingById.TryGetValue(cfg.Id, out var prev))
            {
                cfg.FirstSeenAt = prev.FirstSeenAt;
                cfg.CountryCode = prev.CountryCode;
                cfg.ResolvedIp  = prev.ResolvedIp;
                cfg.Status      = prev.Status;
                cfg.LatencyMs   = prev.LatencyMs;
                cfg.LastTestedAt = prev.LastTestedAt;
                cfg.MeasuredBandwidthMbps = prev.MeasuredBandwidthMbps;
                cfg.BandwidthTestedAt = prev.BandwidthTestedAt;
                cfg.LastDeepVerifyAt = prev.LastDeepVerifyAt;
                cfg.LastVerifyFailedAt = prev.LastVerifyFailedAt;
            }
        }

        // Pass 2: preserve cache-only Verified + recent-Ok entries.
        var preserved = FreeConfigAggregator.PreservePreviousValidation(
            byId, configs, existing.Configs, DateTime.UtcNow);

        if (preserved > 0)
        {
            ctx.Logger.Information(
                "CacheMergeStage: preserved {n} previously-validated entries not in fresh pool",
                preserved);
        }

        return Task.FromResult(new StageResult(
            Success: true,
            Output: configs,
            FailureReason: null,
            Duration: sw.Elapsed));
    }
}
