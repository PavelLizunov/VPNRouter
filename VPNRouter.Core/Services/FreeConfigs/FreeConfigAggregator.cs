using System.Security.Cryptography;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Orchestrates the full pipeline:
/// 1. Fetch all enabled sources
/// 2. Parse vless:// URIs into FreeConfigEntry
/// 3. Deduplicate by (host:port:uuid)
/// 4. Enrich with GeoIP (country code)
/// 5. Test TCP connectivity + measure latency
/// 6. Persist to cache
/// </summary>
public sealed class FreeConfigAggregator
{
    private readonly FreeConfigFetcher _fetcher;
    private readonly FreeConfigTester _tester;
    private readonly FreeConfigGeoIp _geoIp;
    private readonly FreeConfigCache _cache;
    private readonly FreeConfigPoolFetcher _poolFetcher;
    private readonly ILogger _logger;

    public FreeConfigAggregator(ILogger logger)
    {
        _logger = logger;
        _fetcher = new FreeConfigFetcher(logger);
        _tester = new FreeConfigTester();
        _geoIp = new FreeConfigGeoIp(logger);
        _cache = new FreeConfigCache(logger);
        _poolFetcher = new FreeConfigPoolFetcher(logger);
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

    /// <summary>
    /// Events for UI progress reporting.
    /// </summary>
    public event Action<string>? OnStageChanged;
    public event Action<int, int>? OnTestProgress; // (done, total)

    /// <summary>
    /// Full refresh: fetch → parse → dedupe → geoip → test → persist.
    /// Returns the fresh list of configs.
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

        // ── Stage 0 (v2.14.1): try server-side pool.json first ──
        // If successful, skip fetching 14 sources + skip GeoIP entirely.
        // Pool is refreshed by GitHub Actions every 6h — contains metadata + country codes.
        var poolLoaded = false;
        List<FreeConfigEntry>? poolEntries = null;
        if (UseServerPool)
        {
            OnStageChanged?.Invoke("Fetching pool.json from GitHub Releases...");
            try
            {
                poolEntries = await _poolFetcher.FetchPoolAsync(ct);
                if (poolEntries != null && poolEntries.Count > 1000)
                {
                    poolLoaded = true;
                    _logger.Information("Pool loaded: {n} entries — skipping per-source fetch + GeoIP", poolEntries.Count);
                    OnStageChanged?.Invoke($"Pool loaded: {poolEntries.Count} configs (country codes included)");
                }
                else if (poolEntries != null)
                {
                    _logger.Warning("Pool has only {n} entries — falling back to per-source fetch", poolEntries.Count);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning("Pool fetch failed: {err} — falling back to per-source fetch", ex.Message);
            }
        }

        Dictionary<string, FreeConfigEntry> byId;

        if (poolLoaded && poolEntries != null)
        {
            // Pool already contains parsed + deduped + GeoIP-enriched entries.
            // Skip Stages 1-2 entirely, build byId directly.
            byId = poolEntries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
            _logger.Information("FreeConfigAggregator: using {n} entries from pool.json (GeoIP pre-enriched)", byId.Count);
        }
        else
        {
            // ── Stage 1: fetch all sources in parallel, with per-source progress ──
            var enabledSources = sources.Where(s => s.Enabled).ToList();
            OnStageChanged?.Invoke($"Fetching sources (0/{enabledSources.Count})...");

            var fetchedCount = 0;
            var currentlyFetching = new System.Collections.Concurrent.ConcurrentBag<string>();

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
                    OnStageChanged?.Invoke(label);
                    return (s, raws);
                }
                finally { /* best-effort; ConcurrentBag doesn't support remove */ }
            });
            var fetched = await Task.WhenAll(fetchTasks);

            // ── Stage 2: parse + dedupe ──
            OnStageChanged?.Invoke("Parsing configs...");
            byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
            var parseErrors = 0;

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
                            Id         = id,
                            SourceUrl  = src.Url,
                            RawUri     = raw,
                            Host       = vless.Server,
                            Port       = vless.Port,
                            Uuid       = vless.Uuid,
                            Name       = vless.Name ?? "",
                            Sni        = vless.Reality?.ServerName ?? vless.Tls?.ServerName ?? "",
                            Transport  = vless.Transport?.Type ?? "tcp",
                            Security   = vless.Security ?? "reality",
                        };
                    }
                    catch { parseErrors++; }
                }
            }

            _logger.Information("FreeConfigAggregator: parsed {ok} unique ({err} errors) from {src} sources",
                byId.Count, parseErrors, fetched.Length);
        }

        var configs = byId.Values.ToList();

        // Merge with existing cache to preserve latency/status history where possible.
        var existing = _cache.Load();
        var existingById = existing.Configs.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
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
            }
        }

        // ── Stage 3: enrich GeoIP (only for entries without CC) ──
        var needGeo = configs.Where(c => string.IsNullOrEmpty(c.CountryCode)).ToList();
        if (needGeo.Count > 0)
        {
            OnStageChanged?.Invoke($"Resolving country codes ({needGeo.Count} IPs)...");

            // Forward GeoIP internal progress to UI so user sees what's happening.
            _geoIp.Progress = new Progress<(string stage, int done, int total)>(p =>
            {
                var label = p.stage == "dns"
                    ? $"Resolving DNS: {p.done}/{p.total}"
                    : $"Resolving country (batch {p.done}/{p.total})";
                OnStageChanged?.Invoke(label);
            });

            try
            {
                await _geoIp.EnrichAsync(needGeo, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning("GeoIP enrich failed: {err}", ex.Message);
            }
            finally
            {
                _geoIp.Progress = null;
            }
        }

        // ── Stage 4: test connectivity (with skip-recent logic) ──
        // Skip:
        //   1. Verified entries (gold — the weaker TCP+TLS test can only downgrade them)
        //   2. Entries tested within the last `skipRecentHours` hours (default 6h):
        //      their status is fresh enough, re-testing wastes user time. The Retest button
        //      forces a full re-check regardless of age.
        // Priority for the rest: Ok > Slow > Unknown > Implausible > TlsFailed > Timeout > Unreachable
        var now = DateTime.UtcNow;
        var skipCutoff = now - TimeSpan.FromHours(skipRecentHours);
        var skippedRecent = 0;

        var toTest = configs
            .Where(c =>
            {
                if (c.Status == FreeConfigStatus.Verified) return false;
                // Skip if tested recently AND had a definite status (Ok/Slow/TlsFailed/Timeout/Unreachable/Implausible).
                // Unknown entries always get tested even if "LastTestedAt" was set (can happen with partial runs).
                if (c.Status != FreeConfigStatus.Unknown &&
                    c.LastTestedAt.HasValue && c.LastTestedAt.Value >= skipCutoff)
                {
                    Interlocked.Increment(ref skippedRecent);
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

        if (skippedRecent > 0)
        {
            _logger.Information("FreeConfigAggregator: skipped {n} recently-tested entries (< {h}h old)",
                skippedRecent, skipRecentHours);
        }

        var cacheFile = new FreeConfigCache.CacheFile
        {
            LastAggregatedAt = DateTime.UtcNow,
            Configs = configs,
        };
        _cache.Save(cacheFile); // initial save so partial results survive unexpected exit

        // v2.13.17: goal-seeking mode — stop early once N entries match latency criterion.
        var goalMode = goalTargetCount.HasValue && goalMaxLatencyMs.HasValue;
        using var goalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var stageMsg = goalMode
            ? $"Testing {toTest.Count} configs · goal: find {goalTargetCount} with ping < {goalMaxLatencyMs}ms"
            : skippedRecent > 0
                ? $"Testing {toTest.Count} configs (skipped {skippedRecent} recently-tested)..."
                : $"Testing {toTest.Count} configs...";
        OnStageChanged?.Invoke(stageMsg);

        var lastSave = DateTime.UtcNow;
        var foundMatching = 0;
        var goalReached = false;

        var progress = new Progress<(int done, int total)>(p =>
        {
            OnTestProgress?.Invoke(p.done, p.total);

            // Goal-seeking early stop: count how many entries pass the latency gate.
            if (goalMode && !goalReached)
            {
                var matching = toTest.Count(c =>
                    c.Status == FreeConfigStatus.Ok &&
                    c.LatencyMs > 0 &&
                    c.LatencyMs <= goalMaxLatencyMs!.Value);

                if (matching > foundMatching)
                {
                    foundMatching = matching;
                    OnStageChanged?.Invoke(
                        $"Testing ({p.done}/{p.total}) · found {foundMatching}/{goalTargetCount}");
                }

                if (matching >= goalTargetCount!.Value)
                {
                    goalReached = true;
                    _logger.Information("Latency goal reached: {found}/{target} after {done}/{total} tests",
                        matching, goalTargetCount, p.done, p.total);
                    goalCts.Cancel(); // stop the tester early
                }
            }

            // Periodic incremental save every ~50 tests or every 5 seconds.
            if (p.done % 50 == 0 || (DateTime.UtcNow - lastSave).TotalSeconds > 5)
            {
                lastSave = DateTime.UtcNow;
                _cache.Save(cacheFile);
            }
        });

        try
        {
            await _tester.TestAllAsync(toTest, progress, goalCts.Token);
        }
        catch (OperationCanceledException) when (goalReached && !ct.IsCancellationRequested)
        {
            // Goal-reached early stop — NOT a user cancellation. Proceed to save + return normally.
            _logger.Information("Goal-reached stop at {found}/{target} matching entries",
                foundMatching, goalTargetCount);
        }
        catch (OperationCanceledException)
        {
            // User cancel — save partial progress, then re-throw so UI shows "Cancelled".
            _cache.Save(cacheFile);
            throw;
        }

        // ── Stage 5: persist final state ──
        OnStageChanged?.Invoke(goalReached
            ? $"Goal reached: {foundMatching} configs with ping < {goalMaxLatencyMs}ms"
            : "Saving cache...");
        _cache.Save(cacheFile);

        OnStageChanged?.Invoke("Done");
        return configs;
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
}
