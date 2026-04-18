using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// v2.14.1 — fetches pre-aggregated pool.json from GitHub Releases.
///
/// Pool is produced by the <c>build-free-pool.yml</c> GitHub Actions workflow
/// every 6 hours: fetches all 14 sources, parses, dedups, GeoIP-enriches, publishes
/// as a single JSON file. Client saves ~10 minutes of local processing per refresh.
///
/// No validation (TCP/TLS/HTTP) is done on server — that's per-user from their network.
/// The pool provides METADATA only (host, port, SNI, country) + raw vless:// URI.
///
/// ETag-based conditional GET: client only downloads when pool has changed.
/// </summary>
public sealed class FreeConfigPoolFetcher
{
    private const string PoolUrl = "https://github.com/PavelLizunov/VPNRouter/releases/download/free-pool-latest/pool.json";

    private readonly string _cachePath;
    private readonly string _etagPath;
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public FreeConfigPoolFetcher(ILogger logger)
    {
        _logger = logger;
        _cachePath = Path.Combine(AppPaths.CacheDir, "pool.json");
        _etagPath  = _cachePath + ".etag";
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders =
            {
                { "User-Agent", "VPNRouter/2.14 pool-fetcher" },
            },
        };
    }

    /// <summary>
    /// Try to fetch pool.json with ETag-conditional GET.
    /// Returns the list of entries on success (either from network or local cache).
    /// Returns null if pool is unavailable AND no local cache exists — caller should
    /// fall back to direct source fetch.
    /// </summary>
    public async Task<List<FreeConfigEntry>?> FetchPoolAsync(CancellationToken ct = default)
    {
        try
        {
            var etag = File.Exists(_etagPath) ? await File.ReadAllTextAsync(_etagPath, ct) : null;

            using var req = new HttpRequestMessage(HttpMethod.Get, PoolUrl);
            if (!string.IsNullOrEmpty(etag))
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.Information("Pool fetch: 304 Not Modified, using local cache");
                return LoadFromLocalCache();
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning("Pool fetch: HTTP {code}, falling back to local cache", (int)resp.StatusCode);
                return LoadFromLocalCache();
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            Directory.CreateDirectory(AppPaths.CacheDir);
            await File.WriteAllTextAsync(_cachePath, body, ct);

            if (resp.Headers.ETag?.Tag is { } newEtag)
                await File.WriteAllTextAsync(_etagPath, newEtag, ct);

            var entries = ParsePool(body);
            _logger.Information("Pool fetch: downloaded {n} entries", entries.Count);
            return entries;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning("Pool fetch error: {err} — falling back to local cache", ex.Message);
            return LoadFromLocalCache();
        }
    }

    private List<FreeConfigEntry>? LoadFromLocalCache()
    {
        if (!File.Exists(_cachePath))
        {
            _logger.Information("Pool cache: no local copy");
            return null;
        }
        try
        {
            var entries = ParsePool(File.ReadAllText(_cachePath));
            _logger.Information("Pool cache: loaded {n} entries from disk", entries.Count);
            return entries;
        }
        catch (Exception ex)
        {
            _logger.Warning("Pool cache corrupted: {err}", ex.Message);
            try { File.Delete(_cachePath); } catch { }
            try { File.Delete(_etagPath); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Parse pool.json into FreeConfigEntry list.
    /// Schema: { updatedAt, version, sourceCount, totalConfigs, servers: [{id,host,port,uuid,sni,transport,security,country,resolvedIp,source,raw,firstSeen}] }
    /// </summary>
    private static List<FreeConfigEntry> ParsePool(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return new List<FreeConfigEntry>();

        var result = new List<FreeConfigEntry>(servers.GetArrayLength());
        foreach (var s in servers.EnumerateArray())
        {
            try
            {
                var entry = new FreeConfigEntry
                {
                    Id          = GetString(s, "id") ?? "",
                    Host        = GetString(s, "host") ?? "",
                    Port        = s.TryGetProperty("port", out var p) ? p.GetInt32() : 443,
                    Uuid        = GetString(s, "uuid") ?? "",
                    Sni         = GetString(s, "sni") ?? "",
                    Transport   = GetString(s, "transport") ?? "tcp",
                    Security    = GetString(s, "security") ?? "reality",
                    CountryCode = GetString(s, "country"),
                    ResolvedIp  = GetString(s, "resolvedIp"),
                    SourceUrl   = GetString(s, "source") ?? "",
                    RawUri      = GetString(s, "raw") ?? "",
                    FirstSeenAt = TryGetDate(s, "firstSeen") ?? DateTime.UtcNow,
                    Status      = FreeConfigStatus.Unknown,
                };
                if (!string.IsNullOrEmpty(entry.Id) && !string.IsNullOrEmpty(entry.RawUri))
                    result.Add(entry);
            }
            catch { /* skip malformed entry */ }
        }
        return result;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? TryGetDate(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && DateTime.TryParse(v.GetString(), out var dt)
            ? dt : null;
}
