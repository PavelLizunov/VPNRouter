using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Resolves DNS + GeoIP country code for free configs.
/// Uses offline IFreeConfigCountryLookup if available, or falls back to ip-api.com batch endpoint.
/// </summary>
public sealed class FreeConfigGeoIp
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly IFreeConfigCountryLookup? _offlineLookup;
    private readonly ConcurrentDictionary<string, string> _ipToCountry = new();
    private readonly SemaphoreSlim _rateLimit = new(1, 1);
    private DateTime _lastBatchAt = DateTime.MinValue;

    // ip-api.com: 45 requests/minute for unauthenticated free tier = 1 req per 1.33s.
    // We respect that with a safety margin — 1.5s between batches (40 batches/min).
    // Each batch is 100 IPs so 40 batches × 100 = 4000 IPs/min throughput.
    private static readonly TimeSpan MinDelayBetweenBatches = TimeSpan.FromMilliseconds(1500);

    /// <summary>Optional progress reporter for UI: (stage, done, total).</summary>
    public IProgress<(string stage, int done, int total)>? Progress { get; set; }

    public FreeConfigGeoIp(ILogger logger, IFreeConfigCountryLookup? offlineLookup = null)
    {
        _logger = logger;
        _offlineLookup = offlineLookup;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Resolves host→IP for each config (DNS lookup with host deduplication), then enriches with country codes.
    /// Mutates cfg.ResolvedIp and cfg.CountryCode in place.
    /// </summary>
    public async Task EnrichAsync(IReadOnlyList<FreeConfigEntry> configs, CancellationToken ct = default)
    {
        // Step 1: Handle literal IPs directly and collect distinct unresolvable hostnames
        var hostToIp = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hostsToResolve = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cfg in configs)
        {
            if (cfg.ResolvedIp != null) continue;

            if (IPAddress.TryParse(cfg.Host, out var ip))
            {
                cfg.ResolvedIp = ip.ToString();
            }
            else if (!string.IsNullOrWhiteSpace(cfg.Host))
            {
                hostsToResolve.Add(cfg.Host.Trim());
            }
        }

        // Step 2: Resolve unique hostnames once (reducing DNS queries by ~90%)
        using var sem = new SemaphoreSlim(30);
        var uniqueList = hostsToResolve.ToList();
        var total = uniqueList.Count;
        var done = 0;

        var resolveTasks = uniqueList.Select(async host =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var entries = await Dns.GetHostAddressesAsync(host, cts.Token);
                var v4 = entries.FirstOrDefault(e => e.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null)
                {
                    hostToIp[host] = v4.ToString();
                }
            }
            catch
            {
                // Unresolvable — skip
            }
            finally
            {
                sem.Release();
                var d = Interlocked.Increment(ref done);
                Progress?.Report(("dns", d, total));
            }
        });
        await Task.WhenAll(resolveTasks);

        // Assign resolved IPs back to all matching configs
        foreach (var cfg in configs)
        {
            if (cfg.ResolvedIp == null && !string.IsNullOrWhiteSpace(cfg.Host) && hostToIp.TryGetValue(cfg.Host.Trim(), out var resolved))
            {
                cfg.ResolvedIp = resolved;
            }
        }

        // Step 3: Fast offline GeoIP lookup if provided
        if (_offlineLookup != null)
        {
            foreach (var cfg in configs)
            {
                if (!string.IsNullOrEmpty(cfg.ResolvedIp) && IPAddress.TryParse(cfg.ResolvedIp, out var parsedIp))
                {
                    cfg.CountryCode = _offlineLookup.LookupCountry(parsedIp);
                }
            }
            return;
        }

        // Step 4: Online batch lookup fallback (ip-api.com)
        var uncached = configs
            .Where(c => !string.IsNullOrEmpty(c.ResolvedIp))
            .Select(c => c.ResolvedIp!)
            .Distinct()
            .Where(ip => !_ipToCountry.ContainsKey(ip))
            .ToList();

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

        // Assign country codes from memory cache
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
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(ips);
            using var req = new HttpRequestMessage(HttpMethod.Post, "http://ip-api.com/batch?fields=query,countryCode")
            {
                Content = new ByteArrayContent(jsonBytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
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
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("GeoIP batch timed out after 15s — skipping batch of {n} IPs", ips.Length);
        }
        catch (Exception ex)
        {
            _logger.Warning("GeoIP batch failed: {err}", ex.Message);
        }
    }
}
