using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Resolves DNS + GeoIP country code for free configs.
/// Uses ip-api.com batch endpoint (100 IPs/query, 45 req/min unauthenticated).
/// Caches IP→country results in memory and optionally on disk (not implemented — memory-only for MVP).
/// </summary>
public sealed class FreeConfigGeoIp
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _ipToCountry = new();
    private readonly SemaphoreSlim _rateLimit = new(1, 1);
    private DateTime _lastBatchAt = DateTime.MinValue;

    // ip-api.com: 45 requests/minute for unauthenticated free tier = 1 req per 1.33s.
    // We respect that with a safety margin — 1.5s between batches (40 batches/min).
    // Each batch is 100 IPs so 40 batches × 100 = 4000 IPs/min throughput.
    private static readonly TimeSpan MinDelayBetweenBatches = TimeSpan.FromMilliseconds(1500);

    /// <summary>Optional progress reporter for UI: (stage, done, total).</summary>
    public IProgress<(string stage, int done, int total)>? Progress { get; set; }

    public FreeConfigGeoIp(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Resolves host→IP for each config (DNS lookup), then batch-queries ip-api.com for country codes.
    /// Mutates cfg.ResolvedIp and cfg.CountryCode in place.
    /// </summary>
    public async Task EnrichAsync(IReadOnlyList<FreeConfigEntry> configs, CancellationToken ct = default)
    {
        // Step 1: DNS resolve in parallel (limited).
        using var sem = new SemaphoreSlim(30);
        var total = configs.Count;
        var done = 0;

        var resolveTasks = configs.Select(async cfg =>
        {
            if (cfg.ResolvedIp != null)
            {
                var d = Interlocked.Increment(ref done);
                Progress?.Report(("dns", d, total));
                return;
            }

            if (IPAddress.TryParse(cfg.Host, out var ip))
            {
                cfg.ResolvedIp = ip.ToString();
                var d = Interlocked.Increment(ref done);
                Progress?.Report(("dns", d, total));
                return;
            }

            await sem.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var entries = await Dns.GetHostAddressesAsync(cfg.Host, cts.Token);
                var v4 = entries.FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork);
                cfg.ResolvedIp = v4?.ToString();
            }
            catch
            {
                // Unresolvable — leave as null, GeoIP lookup will skip it.
            }
            finally
            {
                sem.Release();
                var d = Interlocked.Increment(ref done);
                Progress?.Report(("dns", d, total));
            }
        });
        await Task.WhenAll(resolveTasks);

        // Step 2: Collect unique IPs without cached country yet.
        var uncached = configs
            .Where(c => !string.IsNullOrEmpty(c.ResolvedIp))
            .Select(c => c.ResolvedIp!)
            .Distinct()
            .Where(ip => !_ipToCountry.ContainsKey(ip))
            .ToList();

        // Step 3: Batch ip-api.com calls (100 IPs per request).
        var batches = uncached.Chunk(100).ToList();
        var batchDone = 0;
        Progress?.Report(("geoip", 0, batches.Count));

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            await RespectRateLimitAsync(ct);
            await QueryBatchAsync(batch, ct);
            batchDone++;
            Progress?.Report(("geoip", batchDone, batches.Count));
        }

        // Step 4: Assign country codes from cache.
        foreach (var cfg in configs)
        {
            if (!string.IsNullOrEmpty(cfg.ResolvedIp) && _ipToCountry.TryGetValue(cfg.ResolvedIp, out var cc))
                cfg.CountryCode = cc;
        }
    }

    private async Task RespectRateLimitAsync(CancellationToken ct)
    {
        await _rateLimit.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastBatchAt;
            if (elapsed < MinDelayBetweenBatches)
                await Task.Delay(MinDelayBetweenBatches - elapsed, ct);
            _lastBatchAt = DateTime.UtcNow;
        }
        finally
        {
            _rateLimit.Release();
        }
    }

    private async Task QueryBatchAsync(string[] ips, CancellationToken ct)
    {
        try
        {
            // ip-api.com batch endpoint: POST http://ip-api.com/batch?fields=query,countryCode
            using var req = new HttpRequestMessage(HttpMethod.Post, "http://ip-api.com/batch?fields=query,countryCode")
            {
                Content = new StringContent(
                    "[" + string.Join(",", ips.Select(ip => $"\"{ip}\"")) + "]",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning("GeoIP: ip-api.com returned HTTP {code}", (int)resp.StatusCode);
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("query", out var qp)) continue;
                var ip = qp.GetString();
                if (ip is null) continue;

                var cc = el.TryGetProperty("countryCode", out var ccp)
                    ? ccp.GetString() ?? ""
                    : "";
                if (!string.IsNullOrEmpty(cc))
                    _ipToCountry[ip] = cc;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only rethrow if it's the USER's cancellation. HttpClient timeout also throws
            // TaskCanceledException (a subtype of OperationCanceledException) but should be
            // swallowed as a normal batch failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient timeout (15s) or our own CancelAfter. Treat as network failure — skip this batch.
            _logger.Warning("GeoIP batch timed out after 15s — skipping batch of {n} IPs", ips.Length);
        }
        catch (Exception ex)
        {
            _logger.Warning("GeoIP batch failed: {err}", ex.Message);
        }
    }
}
