using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Headless.Storage;

namespace VPNRouter.Headless.Features;

public sealed class FreeConfigFeature
{
    private readonly ConfigStorage _storage;
    private readonly FreeConfigCache _cache;
    private readonly FreeConfigAggregator _aggregator;
    private readonly FreeConfigTester _tester;
    private readonly ILogger _logger;

    public FreeConfigFeature(ConfigStorage storage, ILogger? logger = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? Log.Logger;
        _aggregator = new FreeConfigAggregator(_logger);
        _cache = _aggregator.Cache;
        _tester = new FreeConfigTester();
    }

    public object List(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "offset", "limit");

        int offset = 0;
        int limit = 100;

        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("offset", out var offsetProp))
            {
                if (!offsetProp.TryGetInt32(out var o) || o < 0)
                    throw new RouterException("invalid_argument", "offset must be a non-negative integer");
                offset = o;
            }

            if (parameters.TryGetProperty("limit", out var limitProp))
            {
                if (!limitProp.TryGetInt32(out var l) || l < 1 || l > 100)
                    throw new RouterException("invalid_argument", "limit must be an integer between 1 and 100");
                limit = l;
            }
        }

        var cacheFile = _cache.Load();
        var configs = cacheFile?.Configs ?? new List<FreeConfigEntry>();
        var total = configs.Count;

        // Invariant: no raw credentials, UUIDs or share URIs in output rows
        var items = configs
            .Skip(offset)
            .Take(limit)
            .Select(c => new
            {
                id = c.Id,
                name = c.BuildShortName(),
                protocol = string.IsNullOrWhiteSpace(c.Protocol) ? "vless" : c.Protocol.ToLowerInvariant(),
                status = c.Status.ToString().ToLowerInvariant(),
                latencyMs = c.LatencyMs > 0 ? (int?)c.LatencyMs : null
            })
            .ToList();

        return new { items, total };
    }

    public async Task<object> RefreshAsync(JsonElement parameters, CancellationToken ct)
    {
        RouterBackend.EnsureEmptyParameters(parameters);

        var existingFile = _cache.Load();
        var existingConfigs = existingFile?.Configs ?? new List<FreeConfigEntry>();

        List<FreeConfigEntry> entries;
        try
        {
            entries = await _aggregator.FetchPoolAsync(ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[FreeConfigFeature] Refresh failed; preserving existing pool");
            return new { total = existingConfigs.Count };
        }

        // Guard against empty pool result clobbering existing valid cache (preserve pool)
        if ((entries == null || entries.Count == 0) && existingConfigs.Count > 0)
        {
            _logger.Warning("[FreeConfigFeature] Refresh returned 0 entries; preserving {Count} existing configs", existingConfigs.Count);
            return new { total = existingConfigs.Count };
        }

        entries ??= new List<FreeConfigEntry>();

        _cache.Save(new FreeConfigCache.CacheFile
        {
            Configs = entries,
            LastAggregatedAt = DateTime.UtcNow
        });
        _logger.Information("[FreeConfigFeature] Pool refreshed with {Count} entries", entries.Count);
        return new { total = entries.Count };
    }

    public async Task<object> TestAsync(JsonElement parameters, CancellationToken ct)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "id");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var id = idProp.GetString()!;
        var cacheFile = _cache.Load();
        var entry = cacheFile?.Configs?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        if (entry == null)
            throw new RouterException("not_found", "Free config not found in cache");

        // Finding 1 fix: if already verified, use TcpPingOnlyAsync to preserve Verified status for subsequent apply.
        if (entry.Status == FreeConfigStatus.Verified)
        {
            await _tester.TcpPingOnlyAsync(entry, ct);
        }
        else
        {
            await _tester.TestOneAsync(entry, ct);
        }
        _cache.Save(cacheFile);

        var reachable = entry.Status is FreeConfigStatus.Ok
                                      or FreeConfigStatus.Slow
                                      or FreeConfigStatus.Verified;

        return new
        {
            id,
            reachable,
            latencyMs = entry.LatencyMs > 0 ? (int?)entry.LatencyMs : null
        };
    }

    public async Task<object> VerifyAsync(JsonElement parameters, Action<object>? progress, CancellationToken ct)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "id");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var id = idProp.GetString()!;
        var cacheFile = _cache.Load();
        var entry = cacheFile?.Configs?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        if (entry == null)
            throw new RouterException("not_found", "Free config not found in cache");

        progress?.Invoke(new { id, stage = "verify_free_start", completed = 0, total = 1 });

        var verifier = new FreeConfigDeepVerifier(_logger);
        await verifier.VerifyOneAsync(entry, ct);
        _cache.Save(cacheFile);

        progress?.Invoke(new { id, stage = "verify_free_complete", completed = 1, total = 1 });

        var isOk = entry.Status == FreeConfigStatus.Verified;
        return new
        {
            id,
            ok = isOk,
            latencyMs = entry.LatencyMs > 0 ? (int?)entry.LatencyMs : null,
            status = entry.Status.ToString().ToLowerInvariant()
        };
    }

    public void Apply(JsonElement parameters)
    {
        RouterBackend.EnsureAllowedProperties(parameters, "revision", "id");

        if (!parameters.TryGetProperty("revision", out var revProp) || revProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing revision parameter");

        if (!parameters.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new RouterException("invalid_argument", "Missing id parameter");

        var revision = revProp.GetString()!;
        var id = idProp.GetString()!;

        _storage.ValidateRevision(revision);

        var cacheFile = _cache.Load();
        var entry = cacheFile?.Configs?.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        if (entry == null)
            throw new RouterException("not_found", "Free config not found in cache");

        if (entry.Status != FreeConfigStatus.Verified)
            throw new RouterException("invalid_argument", "Free config must be verified before apply");

        var newEntry = entry.ToVlessServerEntry();
        // Ensure no emoji in name
        newEntry.Name = $"[free] {entry.BuildShortName()}";

        var settings = _storage.GetSettings();
        settings.Vless ??= new VlessConfig();
        settings.Vless.Servers ??= new List<VlessServerEntry>();

        var existing = settings.Vless.Servers.FirstOrDefault(s =>
            string.Equals(s.Server, newEntry.Server, StringComparison.OrdinalIgnoreCase) &&
            s.Port == newEntry.Port &&
            string.Equals(s.Uuid, newEntry.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            settings.Vless.ActiveServer = existing.Name;
        }
        else
        {
            settings.Vless.Servers.Add(newEntry);
            settings.Vless.ActiveServer = newEntry.Name;
        }

        settings.App.ConfigMode = "generated";
        _storage.SaveSettings(settings, revision);
        _logger.Information("[FreeConfigFeature] Applied verified free config {Name}", newEntry.Name);
    }
}
