// Phase 3E (2026-05-18) — TestStage. Final pre-display gate: TCP + TLS
// probe with skip-recent gate (default 6h), Verified-skip, Phase 3D
// PlaceholderDefense pre-test rejection, goal-mode early-stop, and
// incremental cache save every 50 tests / 5 s.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services.FreeConfigs.Stages;

/// <summary>
/// Stage 6 of the Free Configs pipeline. TCP + TLS probe for every entry
/// the skip-recent gate considers worth testing. Mutates entries in place
/// (LatencyMs, Status, LastTestedAt, LastError). Not optional — the test
/// is the user-visible signal that "this config works".
/// </summary>
public sealed partial class TestStage : IFreeConfigStage
{
    private readonly FreeConfigTester _tester;

    public TestStage(FreeConfigTester tester)
    {
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));
    }

    /// <inheritdoc />
    public string Name => "test";

    /// <inheritdoc />
    public bool Optional => false;

    /// <summary>True when the latest run hit the latency goal and stopped
    /// early. Owned by the orchestrator's bookkeeping — written here so
    /// the orchestrator can read it after RunAsync returns.</summary>
    public bool GoalReached { get; private set; }

    /// <summary>Count of matching entries when goal-mode is active.</summary>
    public int FoundMatching { get; private set; }

    /// <summary>Count of entries skipped because they were tested
    /// within the last <see cref="StageContext.SkipRecentHours"/> hours.</summary>
    public int SkippedRecent { get; private set; }

    /// <summary>Count of entries rejected by Phase 3D's
    /// PlaceholderDefense.Inspect — bait IPs / Reality public_key /
    /// short_id that match known fingerprints.</summary>
    public int RejectedPlaceholder { get; private set; }

    /// <inheritdoc />
    public async Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sw = Stopwatch.StartNew();

        GoalReached = false;
        FoundMatching = 0;
        SkippedRecent = 0;
        RejectedPlaceholder = 0;

        var configs = ctx.Input.ToList(); // working copy with in-place mutation
        var now = DateTime.UtcNow;

        // Phase 3D pre-test placeholder rejection — mutates entries in place
        // to TlsFailed with LastTestedAt = now, so the skip-recent gate
        // below catches them naturally without a dedicated skip clause.
        RejectedPlaceholder = RejectPlaceholders(configs, now);
        if (RejectedPlaceholder > 0)
            ctx.Logger.Information(
                "TestStage: rejected {n} entries via PlaceholderDefense before TCP probe",
                RejectedPlaceholder);

        // Skip-recent + sort by status quality + take MaxTestCount.
        var skipCutoff = now - TimeSpan.FromHours(ctx.SkipRecentHours);
        var toTest = BuildToTestList(configs, skipCutoff, ctx.MaxTestCount, out var skipped);
        SkippedRecent = skipped;
        if (SkippedRecent > 0)
            ctx.Logger.Information(
                "TestStage: skipped {n} recently-tested entries (< {h}h old)",
                SkippedRecent, ctx.SkipRecentHours);

        // ── Initial cache save (preserves partial progress on unexpected exit) ──
        var cacheFile = new FreeConfigCache.CacheFile
        {
            LastAggregatedAt = DateTime.UtcNow,
            Configs = configs,
        };
        ctx.Cache.Save(cacheFile);

        // ── Goal-mode bookkeeping ──
        var goalMode = ctx.GoalTargetCount.HasValue && ctx.GoalMaxLatencyMs.HasValue;
        using var goalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var stageMsg = goalMode
            ? $"Testing {toTest.Count} configs · goal: find {ctx.GoalTargetCount} with ping < {ctx.GoalMaxLatencyMs}ms"
            : SkippedRecent > 0
                ? $"Testing {toTest.Count} configs (skipped {SkippedRecent} recently-tested)..."
                : $"Testing {toTest.Count} configs...";
        ctx.StageNotice?.Invoke(stageMsg);

        var lastSave = DateTime.UtcNow;
        var foundLocal = 0;
        var goalReachedLocal = false;

        var progress = new Progress<(int done, int total)>(p =>
        {
            ctx.TestProgress?.Invoke(p.done, p.total);

            // Goal-seeking early stop: count entries that pass the gate.
            if (goalMode && !goalReachedLocal)
            {
                var matching = toTest.Count(c =>
                    c.Status == FreeConfigStatus.Ok &&
                    c.LatencyMs > 0 &&
                    c.LatencyMs <= ctx.GoalMaxLatencyMs!.Value);

                if (matching > foundLocal)
                {
                    foundLocal = matching;
                    ctx.StageNotice?.Invoke(
                        $"Testing ({p.done}/{p.total}) · found {foundLocal}/{ctx.GoalTargetCount}");
                }

                if (matching >= ctx.GoalTargetCount!.Value)
                {
                    goalReachedLocal = true;
                    ctx.Logger.Information(
                        "TestStage: latency goal reached: {found}/{target} after {done}/{total} tests",
                        matching, ctx.GoalTargetCount, p.done, p.total);
                    goalCts.Cancel();
                }
            }

            // Periodic incremental save every ~50 tests or every 5 seconds.
            if (p.done % 50 == 0 || (DateTime.UtcNow - lastSave).TotalSeconds > 5)
            {
                lastSave = DateTime.UtcNow;
                ctx.Cache.Save(cacheFile);
            }
        });

        try
        {
            await _tester.TestAllAsync(toTest, progress, goalCts.Token);
        }
        catch (OperationCanceledException) when (goalReachedLocal && !ct.IsCancellationRequested)
        {
            // Goal-reached early stop — NOT a user cancellation. Continue
            // to the final save below.
            ctx.Logger.Information(
                "TestStage: goal-reached stop at {found}/{target} matching entries",
                foundLocal, ctx.GoalTargetCount);
        }
        catch (OperationCanceledException)
        {
            // User cancel — save partial progress, then re-throw so UI shows "Cancelled".
            ctx.Cache.Save(cacheFile);
            throw;
        }

        // Final save.
        ctx.Cache.Save(cacheFile);

        GoalReached = goalReachedLocal;
        FoundMatching = foundLocal;

        return new StageResult(
            Success: true,
            Output: configs,
            FailureReason: goalReachedLocal ? "goal-reached" : null,
            Duration: sw.Elapsed);
    }

    // Helpers split into TestStage.Helpers.cs to keep this file under
    // the Phase 3 <200 LOC stage gate.
}
