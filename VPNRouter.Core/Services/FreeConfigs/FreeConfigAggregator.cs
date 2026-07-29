using System.Security.Cryptography;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Orchestrates the Free Configs pipeline: fetch pool/sources, dedupe, GeoIP
/// enrich, and TCP+TLS test. The UI drives it via <see cref="FetchPoolAsync"/>
/// (fetch+enrich, no test — the VM tests each ~500-entry slice itself) and
/// <see cref="RetestAsync"/>, reporting progress through
/// <see cref="OnStageChanged"/> / <see cref="OnTestProgress"/>.
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
        : this(logger, new FreeConfigCache(logger))
    {
    }

    /// <summary>Test seam: inject a temp-dir <see cref="FreeConfigCache"/>.</summary>
    internal FreeConfigAggregator(ILogger logger, FreeConfigCache cache)
    {
        _logger = logger;
        _fetcher = new FreeConfigFetcher(logger);
        _tester = new FreeConfigTester();
        _geoIp = new FreeConfigGeoIp(logger);
        _cache = cache;
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
    internal List<FreeConfigEntry> MergeWithCache(List<FreeConfigEntry> fresh)
    {
        try
        {
            var existing = _cache.Load();

            var droppedDuplicates = 0;
            var existingById = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in existing.Configs)
            {
                if (!string.IsNullOrEmpty(c.Id) && !existingById.TryAdd(c.Id, c))
                    droppedDuplicates++;
            }

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

            var byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
            var freshDuplicates = 0;
            foreach (var c in fresh)
            {
                if (string.IsNullOrEmpty(c.Id))
                    continue;
                if (!byId.TryAdd(c.Id, c))
                {
                    droppedDuplicates++;
                    freshDuplicates++;
                }
            }
            if (freshDuplicates > 0)
                fresh = byId.Values.ToList();
            if (droppedDuplicates > 0)
            {
                _logger.Warning(
                    "[FreeConfigAggregator] MergeWithCache: dropped {N} duplicate-ID entries (cache + fresh pool)",
                    droppedDuplicates);
            }

            // Also merge previously-Verified entries that the upstream pool dropped.
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
