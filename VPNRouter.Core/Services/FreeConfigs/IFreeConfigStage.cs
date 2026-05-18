// Phase 3E (2026-05-18) — Free Configs pipeline stages.
//
// Audit B: the 6-stage Free Configs pipeline (fetch → parse → dedupe → GeoIP
// → test → cache-merge) lived in one orchestrator (FreeConfigAggregator).
// Splitting into composable stages with explicit contracts enables:
//   - Per-stage retry policy (e.g. fetch retries, test doesn't).
//   - Stage replay for debugging (replay GeoIP from cached fetch output).
//   - Optional stages (skip GeoIP when offline / pool-loaded).
//   - Per-stage testability (unit-test parse without spinning network).
//
// Mirror of Phase 2F ConfigPipeline + Phase 3C StartupPipeline. See file
// header on those orchestrators for the philosophy and trade-offs.
//
// What's NOT here:
//   - FreeConfigAggregator still owns the high-level "Refresh" call that
//     drives UI status events, batched search hooks, and the FetchPoolAsync
//     short-circuit. The pipeline runs inside Refresh; the orchestrator
//     stays a thin loop with per-stage retry wrapping.
//   - Phase 4 will add `StageTelemetry` that records per-stage durations to
//     vpnrouter.log for user-facing diagnostics. The Duration field on
//     StageResult is the seed for that.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// A single composable stage in the Free Configs refresh pipeline. Stages
/// are stateless — all per-run state flows through <see cref="StageContext"/>.
/// Concrete stages live in <c>Stages/</c> and are wired by
/// <see cref="FreeConfigAggregator"/>.
/// </summary>
public interface IFreeConfigStage
{
    /// <summary>Short stable name for diagnostics + telemetry (<c>"fetch"</c>,
    /// <c>"parse"</c>, ...). Becomes the key in retry-policy lookups.</summary>
    string Name { get; }

    /// <summary>
    /// True when failure should NOT abort the pipeline — the orchestrator
    /// propagates the same input to the next stage as a no-op. Used by
    /// GeoIP (offline) and by FetchStage's pool-loaded short-circuit
    /// (which makes downstream Fetch/Parse/Dedupe optional).
    /// </summary>
    bool Optional { get; }

    /// <summary>
    /// Execute this stage and return the transformed entry set. Stages
    /// MUST NOT mutate <see cref="StageContext.Input"/> in-place — they
    /// produce a new list (which can be the same reference when the stage
    /// is a no-op pass-through, e.g. when GeoIP is short-circuited).
    /// </summary>
    Task<StageResult> RunAsync(StageContext ctx, CancellationToken ct);
}

/// <summary>
/// Per-run inputs passed into <see cref="IFreeConfigStage.RunAsync"/>.
///
/// <para><c>Input</c> is the previous stage's output — the orchestrator
/// rolls it forward via <c>ctx = ctx with { Input = result.Output }</c>.</para>
///
/// <para>Stages can read mutable settings via <c>Settings</c> + cache via
/// <c>Cache</c>. The cache is exposed as a reference (not a snapshot) so
/// stages can opt into incremental writes during long-running test passes
/// — see TestStage.</para>
/// </summary>
/// <param name="Input">Entries from the previous stage (or the seed entries
/// for FetchStage). May be empty on a fresh first run; never null.</param>
/// <param name="Settings">App settings reference. Some stages mutate
/// flags inside this (e.g. <c>UseServerPool</c> short-circuits per-source
/// fetch) so callers MUST not assume settings is unchanged after a run.</param>
/// <param name="Cache">FreeConfig cache for merge + incremental save. Stages
/// that touch the cache load it once at the top of RunAsync.</param>
/// <param name="Sources">List of sources to fetch (built-in + user). Empty
/// list means "skip per-source fetch" — useful for pool-only flows.</param>
/// <param name="Logger">Serilog logger threaded through every stage.</param>
/// <param name="StageNotice">Optional UI status-line callback. Stages call
/// this to surface "Fetching sources (3/14)…" etc. May be null for tests.</param>
/// <param name="TestProgress">Optional UI progress callback used by
/// TestStage. May be null for tests.</param>
/// <param name="MaxTestCount">Cap on how many configs the test stage
/// actually probes (default int.MaxValue). Lower values cap first-run time.</param>
/// <param name="SkipRecentHours">Window during which previously-tested
/// entries skip re-testing (default 6h). The Retest path overrides this.</param>
/// <param name="GoalTargetCount">Optional latency-goal early-stop count.</param>
/// <param name="GoalMaxLatencyMs">Optional latency-goal threshold.</param>
public sealed record StageContext(
    IReadOnlyList<FreeConfigEntry> Input,
    AppSettings Settings,
    FreeConfigCache Cache,
    IReadOnlyList<FreeConfigSource> Sources,
    ILogger Logger,
    Action<string>? StageNotice = null,
    Action<int, int>? TestProgress = null,
    int MaxTestCount = int.MaxValue,
    int SkipRecentHours = 6,
    int? GoalTargetCount = null,
    int? GoalMaxLatencyMs = null);

/// <summary>
/// Outcome of <see cref="IFreeConfigStage.RunAsync"/>.
/// </summary>
/// <param name="Success">True on success. False when the stage couldn't
/// produce its expected output but did not throw (e.g. all sources returned
/// HTTP 5xx). When false AND the stage is non-optional, the orchestrator
/// stops the pipeline.</param>
/// <param name="Output">The transformed entry set the next stage will
/// receive as <see cref="StageContext.Input"/>. Stages that pass through
/// unchanged should return the same reference (cheap, no allocation).</param>
/// <param name="FailureReason">Human-readable failure description; null on
/// success. Logged by the orchestrator at Warning level.</param>
/// <param name="Duration">Wall-clock duration of the stage; the orchestrator
/// uses this for per-stage telemetry (Phase 4 follow-up).</param>
/// <param name="ShortCircuit">When true the orchestrator skips all
/// downstream stages that match <see cref="ShortCircuitStages"/>. Used by
/// FetchStage when the pool short-circuit fires — Parse+Dedupe+GeoIP are
/// no-ops in that flow.</param>
/// <param name="ShortCircuitStages">Names of stages to skip after a
/// short-circuit. Empty when ShortCircuit is false.</param>
public sealed record StageResult(
    bool Success,
    IReadOnlyList<FreeConfigEntry> Output,
    string? FailureReason,
    TimeSpan Duration,
    bool ShortCircuit = false,
    IReadOnlyList<string>? ShortCircuitStages = null);

/// <summary>
/// Per-stage retry knobs. Wired into <see cref="StageRetryPolicy"/> via the
/// orchestrator's lookup. Defaults chosen so the existing FreeConfigAggregator
/// behaviour is bit-identical (fetch already had a 2-attempt loop inside
/// <see cref="FreeConfigFetcher"/>; we don't double-retry on top).
/// </summary>
/// <param name="Count">Total attempts including the first try. 1 = no
/// retry; the stage runs at most once. Default 1.</param>
/// <param name="BaseDelayMs">Initial back-off in milliseconds. Subsequent
/// retries scale by 2× (no jitter — Free Configs work happens on a single
/// user machine, no thundering-herd risk).</param>
public sealed record StageRetry(
    int Count = 1,
    int BaseDelayMs = 0);

/// <summary>
/// Map of stage-name → retry knobs. Defaults bake in the historic policy:
/// fetch + parse run once (their own internal logic handles transient
/// failures), test runs once (TCP/TLS probe timeout is the retry), GeoIP
/// runs once (best-effort, optional). Cache-merge runs once (idempotent
/// dictionary merge). Callers can override via constructor injection or
/// AppSettings (Phase 4 will load this from yaml).
/// </summary>
public sealed class StageRetryPolicy
{
    private readonly IReadOnlyDictionary<string, StageRetry> _byName;
    private readonly StageRetry _default;

    public StageRetryPolicy(
        IReadOnlyDictionary<string, StageRetry>? overrides = null,
        StageRetry? @default = null)
    {
        _byName = overrides ?? new Dictionary<string, StageRetry>(StringComparer.OrdinalIgnoreCase);
        _default = @default ?? new StageRetry();
    }

    /// <summary>Resolve the retry policy for a named stage; falls back to
    /// the default when the stage isn't listed.</summary>
    public StageRetry For(string stageName)
    {
        ArgumentNullException.ThrowIfNull(stageName);
        return _byName.TryGetValue(stageName, out var s) ? s : _default;
    }

    /// <summary>Built-in policy used by FreeConfigAggregator when no
    /// override is supplied. Fetch retries once on transient failure;
    /// other stages run once (their internal logic owns the retry).</summary>
    public static StageRetryPolicy Default { get; } = new(
        overrides: new Dictionary<string, StageRetry>(StringComparer.OrdinalIgnoreCase)
        {
            ["fetch"] = new StageRetry(Count: 2, BaseDelayMs: 500),
            ["parse"] = new StageRetry(),
            ["dedupe"] = new StageRetry(),
            ["geoip"] = new StageRetry(),
            ["test"] = new StageRetry(),
            ["cache-merge"] = new StageRetry(),
        });
}
