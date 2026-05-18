// Phase 3E (2026-05-18) — FetchStage.
//
// Pulls raw vless:// URI lines from all enabled FreeConfigSource entries
// (built-in 14 + user-added). Tries the server-side pool.json short-circuit
// first when allowed — when the pool fetch succeeds with >1000 entries we
// skip the per-source fan-out entirely AND signal a downstream short-circuit
// that bypasses Parse + Dedupe + GeoIP (the pool is pre-enriched).
//
// Output:
//   - Pool path: pre-parsed FreeConfigEntry list (already deduped, GeoIP-
//     enriched). ShortCircuit = true, downstream Parse/Dedupe/GeoIP no-op.
//   - Per-source path: empty FreeConfigEntry list with the raw URI strings
//     stored on per-instance state (PendingFetches) that ParseStage reads.
//     ShortCircuit = false.
//
// Per-source fetch already retries inside FreeConfigFetcher (2 attempts with
// 10-second timeouts). The stage-level retry on top is set to a small
// number (default 2) so a fully-offline machine gets two passes before we
// give up + surface "no sources" via the empty output.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 1 of the Free Configs pipeline. Pulls raw URI lines either from
/// the server-side pool.json (short-circuit) or fans out across all enabled
/// sources via <see cref="FreeConfigFetcher"/>.
///
/// <para>This stage is the only one that owns side-effectful HTTP I/O —
/// downstream stages work off the in-memory entry list.</para>
/// </summary>
public sealed class FetchStage : IFreeConfigStage
{
    private readonly FreeConfigFetcher _fetcher;
    private readonly FreeConfigPoolFetcher _poolFetcher;
    private readonly bool _useServerPool;

    /// <summary>
    /// Pending raw URI buckets (source → list of vless:// strings) populated
    /// by the per-source path. ParseStage drains this; cleared at the top of
    /// every <see cref="RunAsync"/> call so re-runs start clean.
    /// </summary>
    internal Dictionary<FreeConfigSource, List<string>> PendingFetches { get; }
        = new();

    public FetchStage(
        FreeConfigFetcher fetcher,
        FreeConfigPoolFetcher poolFetcher,
        bool useServerPool = true)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _poolFetcher = poolFetcher ?? throw new ArgumentNullException(nameof(poolFetcher));
        _useServerPool = useServerPool;
    }

    /// <inheritdoc />
    public string Name => "fetch";

    /// <inheritdoc />
    public bool Optional => false;

    /// <inheritdoc />
    public async Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sw = Stopwatch.StartNew();

        PendingFetches.Clear();

        // ── Stage 0 (v2.14.1): try server-side pool.json first ──
        if (_useServerPool)
        {
            ctx.StageNotice?.Invoke("Fetching pool.json from GitHub Releases...");
            try
            {
                var poolEntries = await _poolFetcher.FetchPoolAsync(ct);
                if (poolEntries != null && poolEntries.Count > 1000)
                {
                    ctx.Logger.Information(
                        "FetchStage: pool loaded {n} entries — skipping per-source fetch + GeoIP",
                        poolEntries.Count);
                    ctx.StageNotice?.Invoke($"Pool loaded: {poolEntries.Count} configs (country codes included)");

                    return new StageResult(
                        Success: true,
                        Output: poolEntries,
                        FailureReason: null,
                        Duration: sw.Elapsed,
                        ShortCircuit: true,
                        ShortCircuitStages: new[] { "parse", "dedupe", "geoip" });
                }
                else if (poolEntries != null)
                {
                    ctx.Logger.Warning(
                        "FetchStage: pool has only {n} entries — falling back to per-source fetch",
                        poolEntries.Count);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ctx.Logger.Warning(
                    "FetchStage: pool fetch failed: {err} — falling back to per-source fetch",
                    ex.Message);
            }
        }

        // ── Per-source fan-out ──
        var enabledSources = ctx.Sources.Where(s => s.Enabled).ToList();
        if (enabledSources.Count == 0)
        {
            ctx.Logger.Warning("FetchStage: no enabled sources — pipeline will produce empty result");
            return new StageResult(
                Success: true,
                Output: Array.Empty<FreeConfigEntry>(),
                FailureReason: null,
                Duration: sw.Elapsed);
        }

        ctx.StageNotice?.Invoke($"Fetching sources (0/{enabledSources.Count})...");

        var fetchedCount = 0;
        var currentlyFetching = new ConcurrentBag<string>();

        var fetchTasks = enabledSources.Select(async s =>
        {
            currentlyFetching.Add(s.Name);
            try
            {
                var raws = await _fetcher.FetchAsync(s, ct);
                var done = Interlocked.Increment(ref fetchedCount);
                var remaining = currentlyFetching.Where(n => n != s.Name).FirstOrDefault() ?? "";
                var label = done == enabledSources.Count
                    ? $"Fetching sources ({done}/{enabledSources.Count}) — done"
                    : remaining.Length > 0
                        ? $"Fetching sources ({done}/{enabledSources.Count}): {remaining}..."
                        : $"Fetching sources ({done}/{enabledSources.Count})...";
                ctx.StageNotice?.Invoke(label);
                return (Source: s, Raws: raws);
            }
            finally { /* best-effort; ConcurrentBag doesn't support remove */ }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        // Stash raw URI buckets for ParseStage to drain via PendingFetches.
        foreach (var (src, raws) in fetched)
        {
            PendingFetches[src] = raws;
        }

        ctx.Logger.Information(
            "FetchStage: fetched {n} sources, total raw URIs {raw}",
            fetched.Length,
            fetched.Sum(f => f.Raws.Count));

        // The output list is empty — we don't materialise FreeConfigEntry
        // until ParseStage. This keeps the contract honest: FetchStage
        // doesn't parse.
        return new StageResult(
            Success: true,
            Output: Array.Empty<FreeConfigEntry>(),
            FailureReason: null,
            Duration: sw.Elapsed);
    }
}
