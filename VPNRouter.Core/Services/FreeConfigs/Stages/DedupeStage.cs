// Phase 3E (2026-05-18) — DedupeStage.
//
// Cross-source dedupe on the in-memory FreeConfigEntry list. Uses
// OrdinalIgnoreCase on the entry Id (host:port:uuid hash). NO mutation
// of the original case on host/uuid — Reality public_key matching is
// case-sensitive everywhere downstream (CLAUDE.md Golden Rule #7). The
// FreeConfigEntry.Id from ParseStage already lowercases host+uuid before
// hashing so cased input variations dedupe correctly, but we preserve the
// original Host string on the output entry.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 3 of the Free Configs pipeline. Cross-source deduplication: when
/// multiple sources contain the same vless:// URI we keep the first
/// occurrence and drop the rest. Skipped via the pool short-circuit
/// (pool.json comes pre-deduped by the GitHub Actions cron job).
/// </summary>
public sealed class DedupeStage : IFreeConfigStage
{
    /// <inheritdoc />
    public string Name => "dedupe";

    /// <inheritdoc />
    public bool Optional => false;

    /// <inheritdoc />
    public Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        var byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
        var input = ctx.Input;
        var duplicates = 0;

        foreach (var entry in input)
        {
            if (string.IsNullOrEmpty(entry.Id))
            {
                ctx.Logger.Debug("DedupeStage: skipping entry with empty Id");
                continue;
            }

            if (!byId.TryAdd(entry.Id, entry))
            {
                duplicates++;
            }
        }

        if (duplicates > 0)
        {
            ctx.Logger.Information(
                "DedupeStage: collapsed {dupes} duplicates → {unique} unique entries",
                duplicates, byId.Count);
        }

        // Return as a List for downstream stages that mutate (GeoIP +
        // CacheMerge). The Values collection on Dictionary is iteration-
        // stable but not enumerable as IReadOnlyList<T>.
        var output = new List<FreeConfigEntry>(byId.Values);

        return Task.FromResult(new StageResult(
            Success: true,
            Output: output,
            FailureReason: null,
            Duration: sw.Elapsed));
    }
}
