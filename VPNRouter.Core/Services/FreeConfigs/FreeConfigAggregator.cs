using System.Security.Cryptography;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Core.Services.FreeConfigs;

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

    internal FreeConfigAggregator(ILogger logger, FreeConfigCache cache)
    {
        _logger = logger;
        _fetcher = new FreeConfigFetcher(logger);
        _tester = new FreeConfigTester();
        _geoIp = new FreeConfigGeoIp(logger);
        _cache = cache;
        _poolFetcher = new FreeConfigPoolFetcher(logger);
    }

    public bool UseServerPool { get; set; } = true;

    public bool RequireTlsHandshake
    {
        get => _tester.RequireTlsHandshake;
        set => _tester.RequireTlsHandshake = value;
    }

    public FreeConfigCache Cache => _cache;

    public FreeConfigTester Tester => _tester;

    public const int DefaultBatchSize = 500;

    public event Action<string>? OnStageChanged;
    public event Action<int, int>? OnTestProgress;

    public async Task<List<FreeConfigEntry>> FetchPoolAsync(
        IReadOnlyList<FreeConfigSource>? sources = null,
        CancellationToken ct = default)
    {
        sources ??= FreeConfigSources.Default;

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
            finally { }
        });
        var fetched = await Task.WhenAll(fetchTasks);

        OnStageChanged?.Invoke("Parsing configs...");
        var byId = new Dictionary<string, FreeConfigEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (src, raws) in fetched)
        {
            foreach (var raw in raws)
            {
                var mapped = TryParseSourceLine(raw, src.Url);
                if (mapped == null || byId.ContainsKey(mapped.Id)) continue;
                byId[mapped.Id] = mapped;
            }
        }
        var configs = byId.Values.ToList();

        var needGeo = configs.Where(c => string.IsNullOrEmpty(c.CountryCode)).ToList();
        if (needGeo.Count > 0)
        {
            OnStageChanged?.Invoke($"Resolving country codes ({needGeo.Count} IPs)...");
            try { await _geoIp.EnrichAsync(needGeo, ct); }
            catch (Exception ex) { _logger.Warning("GeoIP enrich failed: {err}", ex.Message); }
        }

        return MergeWithCache(configs);
    }

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

            PreservePreviousValidation(byId, fresh, existing.Configs, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigAggregator] MergeWithCache failed (non-fatal)");
        }
        return fresh;
    }

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

    internal static FreeConfigEntry? TryParseSourceLine(string raw, string sourceUrl)
    {
        try
        {
            var parsed = ServerUriParser.Parse(raw);
            var auth = parsed.Uuid;
            if (string.IsNullOrEmpty(auth))
                auth = parsed.Password;
            if (string.IsNullOrEmpty(auth))
                auth = parsed.Awg?.PrivateKey ?? "";
            return new FreeConfigEntry
            {
                Id = BuildId(parsed.Server, parsed.Port, auth),
                SourceUrl = sourceUrl,
                RawUri = raw,
                Host = parsed.Server,
                Port = parsed.Port,
                Uuid = parsed.Uuid,
                Protocol = parsed.Protocol ?? "vless",
                Name = parsed.Name ?? "",
                Sni = parsed.Reality?.ServerName ?? parsed.Tls?.ServerName ?? "",
                Transport = parsed.Transport?.Type ?? "tcp",
                Security = parsed.Security ?? "reality",
            };
        }
        catch (FormatException)
        {
            return null;
        }
        catch (PlaceholderConfigException)
        {
            return null;
        }
    }

    private static string BuildId(string host, int port, string uuid)
    {
        var key = $"{host.ToLowerInvariant()}:{port}:{uuid.ToLowerInvariant()}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 8);
    }

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
