using System.Security.Cryptography;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs.Stages;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Orchestrates the full Free Configs pipeline. Phase 3E (2026-05-18)
/// refactored the inline 6-step pipeline into composable
/// <see cref="IFreeConfigStage"/> instances under
/// <see cref="Stages"/>; this class is now a thin orchestrator that runs
/// the stage list under a per-stage <see cref="StageRetryPolicy"/>.
///
/// <para>Stage order (each stage's own file documents what it does):</para>
/// <list type="number">
///   <item><see cref="FetchStage"/> — pool.json short-circuit OR per-source fan-out</item>
///   <item><see cref="ParseStage"/> — raw URIs → FreeConfigEntry (skipped via pool short-circuit)</item>
///   <item><see cref="DedupeStage"/> — cross-source dedupe (skipped via pool short-circuit)</item>
///   <item><see cref="GeoIpStage"/> — ip-api.com country codes (skipped via pool short-circuit)</item>
///   <item><see cref="CacheMergeStage"/> — inherit + preserve from on-disk cache</item>
///   <item><see cref="TestStage"/> — TCP+TLS probe + skip-recent gate + incremental save</item>
/// </list>
/// </summary>
public sealed class FreeConfigAggregator
{
    private readonly FreeConfigFetcher _fetcher;
    private readonly FreeConfigTester _tester;
    private readonly FreeConfigGeoIp _geoIp;
    private readonly FreeConfigCache _cache;
    private readonly FreeConfigPoolFetcher _poolFetcher;
    private readonly ILogger _logger;
    private readonly StageRetryPolicy _retryPolicy;

    public FreeConfigAggregator(ILogger logger, StageRetryPolicy? retryPolicy = null)
    {
        _logger = logger;
        _fetcher = new FreeConfigFetcher(logger);
        _tester = new FreeConfigTester();
        _geoIp = new FreeConfigGeoIp(logger);
        _cache = new FreeConfigCache(logger);
        _poolFetcher = new FreeConfigPoolFetcher(logger);
        _retryPolicy = retryPolicy ?? StageRetryPolicy.Default;
    }

    /// <summary>v2.14.1: whether to prefer server-side pool.json over direct source fetch.</summary>
    public bool UseServerPool { get; set; } = true;

    /// <summary>v2.13.18: toggle TLS handshake validation during TCP+TLS test stage.
    /// true = full validation (default), false = TCP-only fast scan (~3× faster, misses honeypots).</summary>
    public bool RequireTlsHandshake
    {
        get => _tester.RequireTlsHandshake;
        set => _tester.RequireTlsHandshake = value;
    }

    /// <summary>Access to the underlying cache for UI (path, current snapshot).</summary>
    public FreeConfigCache Cache => _cache;

    /// <summary>v2.28.5-r2: expose tester for the batched search flow in
    /// <c>FreeConfigsPageViewModel</c>. The VM tests per-batch so memory
    /// stays bounded — only the current ~500-entry batch lives in the
    /// hot path, instead of all 25 000 pool entries.</summary>
    public FreeConfigTester Tester => _tester;

    /// <summary>v2.28.5-r2: tunable batch size for the new VM-driven
    /// batched flow. 500 keeps memory bounded while still amortising
    /// HTTP fetch + GeoIP overhead. Power users can override via
    /// reflection or future config setting.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// Events for UI progress reporting.
    /// </summary>
    public event Action<string>? OnStageChanged;
    public event Action<int, int>? OnTestProgress; // (done, total)

    /// <summary>
    /// Full refresh: fetch → parse → dedupe → geoip → cache-merge → test.
    /// Returns the fresh list of configs.
    ///
    /// <para>Phase 3E (2026-05-18): the inline 6-step pipeline was replaced
    /// by a stage loop. Per-stage retry policy is configurable via the
    /// constructor (defaults to <see cref="StageRetryPolicy.Default"/>),
    /// so callers can tune fetch retries / test retries independently.</para>
    ///
    /// <paramref name="maxTestCount"/> caps how many configs are actually TCP-tested.
    /// Default = int.MaxValue (test everything). Set lower to limit first-run time.
    /// Incremental cache saves every 50 tests / 5 seconds, so Cancel preserves progress.
    /// </summary>
    public async Task<List<FreeConfigEntry>> RefreshAsync(
        IReadOnlyList<FreeConfigSource>? sources = null,
        int maxTestCount = int.MaxValue,
        int skipRecentHours = 6,
        int? goalTargetCount = null,
        int? goalMaxLatencyMs = null,
        CancellationToken ct = default)
    {
        sources ??= FreeConfigSources.Default;

        // ── Build the stage list ──
        var fetchStage = new FetchStage(_fetcher, _poolFetcher, useServerPool: UseServerPool);
        var parseStage = new ParseStage(fetchStage);
        var dedupeStage = new DedupeStage();
        var geoIpStage = new GeoIpStage(_geoIp);
        var cacheMergeStage = new CacheMergeStage();
        var testStage = new TestStage(_tester);

        var stages = new IFreeConfigStage[]
        {
            fetchStage, parseStage, dedupeStage, geoIpStage, cacheMergeStage, testStage,
        };

        var ctx = new StageContext(
            Input: Array.Empty<FreeConfigEntry>(),
            Settings: new AppSettings(), // settings stub — Phase 4 lifts up to caller
            Cache: _cache,
            Sources: sources,
            Logger: _logger,
            StageNotice: OnStageChanged,
            TestProgress: OnTestProgress != null
                ? (done, total) => OnTestProgress?.Invoke(done, total)
                : null,
            MaxTestCount: maxTestCount,
            SkipRecentHours: skipRecentHours,
            GoalTargetCount: goalTargetCount,
            GoalMaxLatencyMs: goalMaxLatencyMs);

        // ── Run stages in order, honouring short-circuit + retry policy ──
        var skipStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stage in stages)
        {
            ct.ThrowIfCancellationRequested();

            if (skipStages.Contains(stage.Name))
            {
                _logger.Debug("FreeConfigAggregator: short-circuit skipping stage {name}", stage.Name);
                continue;
            }

            var result = await RunWithRetryAsync(stage, ctx, _retryPolicy.For(stage.Name), ct);

            if (!result.Success && !stage.Optional)
            {
                _logger.Warning(
                    "FreeConfigAggregator: stage {name} failed (reason: {reason}) — aborting pipeline",
                    stage.Name, result.FailureReason ?? "(unknown)");
                break;
            }

            ctx = ctx with { Input = result.Output };

            if (result.ShortCircuit && result.ShortCircuitStages != null)
            {
                foreach (var s in result.ShortCircuitStages)
                    skipStages.Add(s);
            }
        }

        // ── Final UI notice mirrors pre-3E "Done" / "Goal reached" message ──
        OnStageChanged?.Invoke(testStage.GoalReached
            ? $"Goal reached: {testStage.FoundMatching} configs with ping < {goalMaxLatencyMs}ms"
            : "Done");

        // The final Output is whatever TestStage produced (or upstream
        // stage if test was skipped on short-circuit). Materialise as
        // List<T> for back-compat with the previous return type.
        return ctx.Input.ToList();
    }

    /// <summary>
    /// Per-stage retry wrapper. Runs the stage up to
    /// <see cref="StageRetry.Count"/> times with exponential back-off,
    /// returning the FIRST successful StageResult. <see cref="OperationCanceledException"/>
    /// always propagates without retry (user cancel never retries).
    /// </summary>
    private async Task<StageResult> RunWithRetryAsync(
        IFreeConfigStage stage,
        StageContext ctx,
        StageRetry retry,
        CancellationToken ct)
    {
        StageResult? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, retry.Count); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                last = await stage.RunAsync(ctx, ct);
                if (last.Success) return last;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "FreeConfigAggregator: stage {name} threw on attempt {n}/{max}",
                    stage.Name, attempt, retry.Count);
                last = new StageResult(
                    Success: false,
                    Output: ctx.Input,
                    FailureReason: ex.Message,
                    Duration: TimeSpan.Zero);
            }

            if (attempt < retry.Count)
            {
                var delay = retry.BaseDelayMs * (int)Math.Pow(2, attempt - 1);
                if (delay > 0)
                    await Task.Delay(delay, ct);
            }
        }
        return last ?? new StageResult(
            Success: false,
            Output: ctx.Input,
            FailureReason: "stage produced no result",
            Duration: TimeSpan.Zero);
    }

    /// <summary>
    /// v2.28.5-r2: fetch + parse + dedupe + GeoIP enrichment, but skip
    /// the TCP+TLS test stage. Used by the batched VM flow which tests
    /// each ~500-entry slice itself (via <see cref="Tester"/>) instead of
    /// loading the whole pool into one big test pass — keeps the
    /// mid-search memory peak bounded.
    /// </summary>
    public async Task<List<FreeConfigEntry>> FetchPoolAsync(
        IReadOnlyList<FreeConfigSource>? sources = null,
        CancellationToken ct = default)
    {
        sources ??= FreeConfigSources.Default;

        // Stage 0: try server-side pool.json first (cheapest path).
        List<FreeConfigEntry>? poolEntries = null;
        if (UseServerPool)
        {
            OnStageChanged?.Invoke("Fetching pool.json from GitHub Releases...");
            try
            {
                poolEntries = await _poolFetcher.FetchPoolAsync(ct);
                if (poolEntries != null && poolEntries.Count > 1000)
                {
                    _logger.Information("Pool loaded: {n} entries", poolEntries.Count);
                    OnStageChanged?.Invoke($"Pool loaded: {poolEntries.Count} configs");
                    return MergeWithCache(poolEntries);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("Pool fetch failed: {err} — falling back to per-source fetch", ex.Message);
            }
        }

        // Fallback: per-source fetch + parse + dedupe + GeoIP.
        var enabledSources = sources.Where(s => s.Enabled).ToList();
        OnStageChanged?.Invoke($"Fetching sources (0/{enabledSources.Count})...");

        var fetchedCount = 0;
        var fetchTasks = enabledSources.Select(async s =>
        {
            try
            {
                var raws = await _fetcher.FetchAsync(s, ct);
                var done = Interlocked.Increment(ref fetchedCount);
                OnStageChanged?.Invoke($"Fetching sources ({done}/{enabledSources.Count})...");
                return (s, raws);
            }
            finally { /* best-effort */ }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        OnStageChanged?.Invoke("Parsing configs...");
        var byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (src, raws) in fetched)
        {
            foreach (var raw in raws)
            {
                try
                {
                    var vless = VlessUriParser.Parse(raw);
                    var id = BuildId(vless.Server, vless.Port, vless.Uuid);
                    if (byId.ContainsKey(id)) continue;
                    byId[id] = new FreeConfigEntry
                    {
                        Id = id,
                        SourceUrl = src.Url,
                        RawUri = raw,
                        Host = vless.Server,
                        Port = vless.Port,
                        Uuid = vless.Uuid,
                        Name = vless.Name ?? "",
                        Sni = vless.Reality?.ServerName ?? vless.Tls?.ServerName ?? "",
                        Transport = vless.Transport?.Type ?? "tcp",
                        Security = vless.Security ?? "reality",
                    };
                }
                catch { }
            }
        }
        var configs = byId.Values.ToList();

        // GeoIP enrichment (best-effort).
        var needGeo = configs.Where(c => string.IsNullOrEmpty(c.CountryCode)).ToList();
        if (needGeo.Count > 0)
        {
            OnStageChanged?.Invoke($"Resolving country codes ({needGeo.Count} IPs)...");
            try { await _geoIp.EnrichAsync(needGeo, ct); }
            catch (Exception ex) { _logger.Warning("GeoIP enrich failed: {err}", ex.Message); }
        }

        return MergeWithCache(configs);
    }

    /// <summary>v2.28.5-r2: merge fresh pool with existing cache so
    /// previously-Verified entries (and recent-Ok entries within the
    /// 24h window) keep their status across Refreshes. Used by
    /// <see cref="FetchPoolAsync"/>.</summary>
    private List<FreeConfigEntry> MergeWithCache(List<FreeConfigEntry> fresh)
    {
        try
        {
            var existing = _cache.Load();
            var existingById = existing.Configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in fresh)
            {
                if (existingById.TryGetValue(cfg.Id, out var prev))
                {
                    cfg.FirstSeenAt = prev.FirstSeenAt;
                    cfg.CountryCode = prev.CountryCode;
                    cfg.ResolvedIp = prev.ResolvedIp;
                    cfg.Status = prev.Status;
                    cfg.LatencyMs = prev.LatencyMs;
                    cfg.LastTestedAt = prev.LastTestedAt;
                    cfg.MeasuredBandwidthMbps = prev.MeasuredBandwidthMbps;
                    cfg.BandwidthTestedAt = prev.BandwidthTestedAt;
                }
            }

            // Also merge previously-Verified entries that the upstream pool dropped.
            var byId = fresh.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
            PreservePreviousValidation(byId, fresh, existing.Configs, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigAggregator] MergeWithCache failed (non-fatal)");
        }
        return fresh;
    }

    /// <summary>Re-test all known configs (no re-fetch).</summary>
    public async Task<List<FreeConfigEntry>> RetestAsync(CancellationToken ct = default)
    {
        var file = _cache.Load();
        if (file.Configs.Count == 0) return file.Configs;

        OnStageChanged?.Invoke("Testing connectivity...");
        var progress = new Progress<(int done, int total)>(p => OnTestProgress?.Invoke(p.done, p.total));
        await _tester.TestAllAsync(file.Configs, progress, ct);

        _cache.Save(file);
        OnStageChanged?.Invoke("Done");
        return file.Configs;
    }

    private static string BuildId(string host, int port, string uuid)
    {
        using var sha = SHA1.Create();
        var key = $"{host.ToLowerInvariant()}:{port}:{uuid.ToLowerInvariant()}";
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8); // 16-char prefix is unique enough for ~100k configs.
    }

    /// <summary>
    /// v2.28.3-r5: preserve previously-validated entries from the cache that
    /// are no longer in the freshly-fetched pool.
    ///
    /// <para>The server-side pool.json is regenerated every 6h and can drop
    /// entries (source rotation, server-side TLS failures, upstream removal).
    /// Without this merge, a user who runs Refresh with new criteria loses
    /// their previously-Verified results just because the upstream pool moved
    /// on. User report (2026-04-27): "не пропадают пред идущие рабочие".</para>
    ///
    /// <para>Preserve only "interesting" entries:</para>
    /// <list type="bullet">
    /// <item>Verified (gold — passed full Deep Verify with HTTP round-trip)</item>
    /// <item>Ok and tested in the last 24h (TCP+TLS pass, recent enough to trust)</item>
    /// </list>
    /// <para>Older Ok entries (&gt;24h) get dropped to keep the cache tractable;
    /// they would re-test from scratch anyway via the skip-recent logic.</para>
    ///
    /// <para>Mutates both <paramref name="byId"/> and <paramref name="configs"/>
    /// in place, returns the number of preserved entries for logging.</para>
    /// </summary>
    /// <remarks>Public for unit testing — exposed via internal visibility
    /// to <see cref="VPNRouter.Tests"/> via InternalsVisibleTo.</remarks>
    internal static int PreservePreviousValidation(
        Dictionary<string, FreeConfigEntry> byId,
        List<FreeConfigEntry> configs,
        IList<FreeConfigEntry> existingConfigs,
        DateTime nowUtc)
    {
        var ageCutoff = nowUtc.AddHours(-24);
        var preserved = 0;
        foreach (var prev in existingConfigs)
        {
            if (string.IsNullOrEmpty(prev.Id)) continue;
            if (byId.ContainsKey(prev.Id)) continue;

            var isVerified = prev.Status == FreeConfigStatus.Verified;
            var isRecentOk = prev.Status == FreeConfigStatus.Ok
                          && prev.LastTestedAt.HasValue
                          && prev.LastTestedAt.Value >= ageCutoff;

            if (isVerified || isRecentOk)
            {
                configs.Add(prev);
                byId[prev.Id] = prev;
                preserved++;
            }
        }
        return preserved;
    }
}
