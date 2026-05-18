// Phase 3E (2026-05-18) — TestStage helpers split out so TestStage.cs
// stays under the <200 LOC stage gate. Both methods are private statics
// — no behaviour-bearing state is duplicated here.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

public sealed partial class TestStage
{
    /// <summary>
    /// Walk every entry, check the server IP / Reality public_key /
    /// short_id against <see cref="PlaceholderDefense"/>, and mutate
    /// matches in-place to <see cref="FreeConfigStatus.TlsFailed"/> with
    /// a "placeholder credential" LastError. Returns the count of rejected
    /// entries so the orchestrator can surface it in telemetry.
    /// </summary>
    private static int RejectPlaceholders(List<FreeConfigEntry> configs, DateTime now)
    {
        var rejected = 0;
        foreach (var cfg in configs)
        {
            // Cheap path: server IP fingerprint check.
            var hit = PlaceholderDefense.Inspect(
                realityPubkey: null,
                realityShortId: null,
                server: cfg.Host);

            // Full URI inspection — picks up Reality public_key / short_id.
            hit ??= PlaceholderDefense.InspectUri(cfg.RawUri);

            if (hit != null)
            {
                cfg.Status = FreeConfigStatus.TlsFailed;
                cfg.LastError = $"placeholder credential ({hit})";
                cfg.LastTestedAt = now;
                cfg.LatencyMs = 0;
                rejected++;
            }
        }
        return rejected;
    }

    /// <summary>
    /// Build the prioritised toTest list: drop Verified + recent-tested
    /// entries, sort by status quality (Ok &gt; Slow &gt; Unknown &gt;
    /// Implausible &gt; TlsFailed &gt; Timeout &gt; Unreachable), then by
    /// previous latency, then cap at <paramref name="maxTestCount"/>.
    /// </summary>
    private static List<FreeConfigEntry> BuildToTestList(
        List<FreeConfigEntry> configs,
        DateTime skipCutoff,
        int maxTestCount,
        out int skippedRecent)
    {
        var localSkipped = 0;
        var result = configs
            .Where(c =>
            {
                if (c.Status == FreeConfigStatus.Verified) return false;
                if (c.Status != FreeConfigStatus.Unknown &&
                    c.LastTestedAt.HasValue && c.LastTestedAt.Value >= skipCutoff)
                {
                    Interlocked.Increment(ref localSkipped);
                    return false;
                }
                return true;
            })
            .OrderBy(c => c.Status switch
            {
                FreeConfigStatus.Ok          => 0,
                FreeConfigStatus.Slow        => 1,
                FreeConfigStatus.Unknown     => 2,
                FreeConfigStatus.Implausible => 3,
                FreeConfigStatus.TlsFailed   => 4,
                FreeConfigStatus.Timeout     => 5,
                FreeConfigStatus.Unreachable => 6,
                _                            => 7,
            })
            .ThenBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
            .Take(maxTestCount)
            .ToList();
        skippedRecent = localSkipped;
        return result;
    }
}
