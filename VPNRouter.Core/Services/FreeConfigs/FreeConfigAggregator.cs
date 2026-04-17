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
    private readonly ILogger _logger;

    public FreeConfigAggregator(ILogger logger)
    {
        _logger = logger;
        _fetcher = new FreeConfigFetcher(logger);
        _tester = new FreeConfigTester();
        _geoIp = new FreeConfigGeoIp(logger);
        _cache = new FreeConfigCache(logger);
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
    /// <paramref name="maxTestCount"/> caps how many configs are actually TCP-tested
    /// (still fetches/parses all, but skipped ones keep their last known status).
    /// With 2000+ configs on first run, testing all can take 10+ minutes.
    /// </summary>
    public async Task<List<FreeConfigEntry>> RefreshAsync(
        IReadOnlyList<FreeConfigSource>? sources = null,
        int maxTestCount = 500,
        CancellationToken ct = default)
    {
        sources ??= FreeConfigSources.Default;

        // ── Stage 1: fetch all sources in parallel ──
        OnStageChanged?.Invoke("Fetching sources...");
        var fetchTasks = sources
            .Where(s => s.Enabled)
            .Select(async s => (s, raws: await _fetcher.FetchAsync(s, ct)));
        var fetched = await Task.WhenAll(fetchTasks);

        // ── Stage 2: parse + dedupe ──
        OnStageChanged?.Invoke("Parsing configs...");
        var byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
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
                catch
                {
                    parseErrors++;
                }
            }
        }

        _logger.Information("FreeConfigAggregator: parsed {ok} unique ({err} errors) from {src} sources",
            byId.Count, parseErrors, fetched.Length);

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
        OnStageChanged?.Invoke("Resolving country codes...");
        var needGeo = configs.Where(c => string.IsNullOrEmpty(c.CountryCode)).ToList();
        if (needGeo.Count > 0)
        {
            try
            {
                await _geoIp.EnrichAsync(needGeo, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning("GeoIP enrich failed: {err}", ex.Message);
            }
        }

        // ── Stage 4: test connectivity (capped) ──
        // Prioritize: previously-working > previously-unknown > previously-failed.
        // Save cache at start (so partial data survives crashes) and every 50 results.
        var toTest = configs
            .OrderBy(c => c.Status switch
            {
                FreeConfigStatus.Ok          => 0,
                FreeConfigStatus.Slow        => 1,
                FreeConfigStatus.Unknown     => 2,
                FreeConfigStatus.Timeout     => 3,
                FreeConfigStatus.Unreachable => 4,
                _                            => 5,
            })
            .ThenBy(c => c.LatencyMs > 0 ? c.LatencyMs : int.MaxValue)
            .Take(maxTestCount)
            .ToList();

        var cacheFile = new FreeConfigCache.CacheFile
        {
            LastAggregatedAt = DateTime.UtcNow,
            Configs = configs,
        };
        _cache.Save(cacheFile); // initial save so partial results survive unexpected exit

        OnStageChanged?.Invoke($"Testing {toTest.Count} configs...");
        var lastSave = DateTime.UtcNow;
        var progress = new Progress<(int done, int total)>(p =>
        {
            OnTestProgress?.Invoke(p.done, p.total);
            // Periodic incremental save every ~50 tests or every 5 seconds.
            if (p.done % 50 == 0 || (DateTime.UtcNow - lastSave).TotalSeconds > 5)
            {
                lastSave = DateTime.UtcNow;
                _cache.Save(cacheFile);
            }
        });

        try
        {
            await _tester.TestAllAsync(toTest, progress, ct);
        }
        catch (OperationCanceledException)
        {
            // Save whatever we have so far, then re-throw for UI to handle.
            _cache.Save(cacheFile);
            throw;
        }

        // ── Stage 5: persist final state ──
        OnStageChanged?.Invoke("Saving cache...");
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
