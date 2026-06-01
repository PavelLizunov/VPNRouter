using System.IO.Compression;
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
///
/// v2.39.0 (audit #4 fix): the primary fetch is the COMPRESSED asset
/// <c>pool.json.gz</c> (~3.9 MB) instead of raw <c>pool.json</c> (~27 MB) — a 7x
/// smaller download that no longer times out on slow / mobile / RU networks and
/// cuts memory pressure. Decompression is bounded (defeats gzip bombs), the
/// payload is validated before it replaces the last-known-good local cache
/// (atomic temp+rename), and the raw asset stays as a legacy fallback.
/// </summary>
public sealed class FreeConfigPoolFetcher
{
    private const string ReleaseBase =
        "https://github.com/PavelLizunov/VPNRouter/releases/download/free-pool-latest/";
    private const string PoolGzUrl = ReleaseBase + "pool.json.gz"; // primary (~3.9 MB)
    private const string PoolUrl   = ReleaseBase + "pool.json";    // legacy raw fallback (~27 MB)

    // Bomb / runaway guards. The live pool is ~3.9 MB gz -> ~27 MB json; cap well
    // above that but bounded so a hostile or corrupt asset can't exhaust memory/disk.
    internal const long MaxCompressedBytes = 32L * 1024 * 1024;  // reject a .gz larger than 32 MB
    internal const long MaxExpandedBytes   = 128L * 1024 * 1024; // abort decompression past 128 MB

    private readonly string _cachePath;
    private readonly string _etagPath;     // raw pool.json etag
    private readonly string _gzEtagPath;   // pool.json.gz etag
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public FreeConfigPoolFetcher(ILogger logger)
        : this(logger, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { }

    /// <summary>Test seam: inject a message handler (e.g. a fake) to drive the fetch flow.</summary>
    internal FreeConfigPoolFetcher(ILogger logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _cachePath  = Path.Combine(AppPaths.CacheDir, "pool.json");
        _etagPath   = _cachePath + ".etag";
        _gzEtagPath = _cachePath + ".gz.etag";
        // AutomaticDecompression is OFF on purpose: we fetch the .gz ASSET and
        // decompress it ourselves with a bounded reader. Letting HttpClient
        // transparently inflate would bypass the expanded-size guard.
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "User-Agent", "VPNRouter/2.39 pool-fetcher" } },
        };
    }

    private enum Outcome { Success, NotModified, Failed }

    /// <summary>
    /// Try to fetch the pool with ETag-conditional GET. Prefers the compressed
    /// asset; falls back to the raw asset, then to the local cache.
    /// Returns null only if the pool is unavailable AND no local cache exists —
    /// caller should then fall back to direct source fetch.
    /// </summary>
    public async Task<List<FreeConfigEntry>?> FetchPoolAsync(CancellationToken ct = default)
    {
        // 1) compressed primary
        var (outcome, entries) = await TryFetchAsync(PoolGzUrl, gzip: true, _gzEtagPath, ct);
        if (outcome == Outcome.Success) return entries;
        if (outcome == Outcome.NotModified) return LoadFromLocalCache();

        // 2) raw legacy fallback (e.g. an old release without the .gz asset)
        _logger.Information("Pool fetch: compressed asset unavailable, trying raw pool.json");
        (outcome, entries) = await TryFetchAsync(PoolUrl, gzip: false, _etagPath, ct);
        if (outcome == Outcome.Success) return entries;
        if (outcome == Outcome.NotModified) return LoadFromLocalCache();

        // 3) last-known-good
        return LoadFromLocalCache();
    }

    private async Task<(Outcome, List<FreeConfigEntry>?)> TryFetchAsync(
        string url, bool gzip, string etagPath, CancellationToken ct)
    {
        try
        {
            var etag = File.Exists(etagPath) ? await File.ReadAllTextAsync(etagPath, ct) : null;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag))
                req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.Information("Pool fetch: 304 Not Modified ({which}), using local cache",
                    gzip ? "gz" : "raw");
                return (Outcome.NotModified, null);
            }
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning("Pool fetch: HTTP {code} for {which} asset", (int)resp.StatusCode, gzip ? "gz" : "raw");
                return (Outcome.Failed, null);
            }
            if (resp.Content.Headers.ContentLength is { } len && len > MaxCompressedBytes)
            {
                _logger.Warning("Pool fetch: asset too large ({len} bytes) — rejecting", len);
                return (Outcome.Failed, null);
            }

            // Decompress (bounded) into a temp file, validate, THEN atomically
            // replace the last-known-good cache. A truncated/garbage download
            // never clobbers the previous good pool.
            Directory.CreateDirectory(AppPaths.CacheDir);
            var tmp = _cachePath + ".tmp";
            try
            {
                await using (var net = await resp.Content.ReadAsStreamAsync(ct))
                await using (var outFile = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await DecompressBoundedAsync(net, gzip, outFile, MaxExpandedBytes, ct);
                }

                List<FreeConfigEntry> parsed;
                await using (var read = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                    parsed = ParsePool(read);

                if (parsed.Count == 0)
                    throw new InvalidDataException("pool parsed to 0 entries — treating as invalid");

                File.Move(tmp, _cachePath, overwrite: true);
                if (resp.Headers.ETag?.Tag is { } newEtag)
                    await File.WriteAllTextAsync(etagPath, newEtag, ct);

                _logger.Information("Pool fetch: downloaded {n} entries ({which})", parsed.Count, gzip ? "gz" : "raw");
                return (Outcome.Success, parsed);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning("Pool fetch error ({which}): {err}", gzip ? "gz" : "raw", ex.Message);
            return (Outcome.Failed, null);
        }
    }

    /// <summary>
    /// Stream-copy <paramref name="source"/> (gunzipping if <paramref name="gzip"/>)
    /// into <paramref name="destination"/>, aborting if the expanded size exceeds
    /// <paramref name="maxExpandedBytes"/> — defeats decompression bombs. The
    /// source stream is left open (the caller owns it).
    /// </summary>
    internal static async Task DecompressBoundedAsync(
        Stream source, bool gzip, Stream destination, long maxExpandedBytes, CancellationToken ct)
    {
        Stream reader = gzip ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : source;
        try
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            {
                total += n;
                if (total > maxExpandedBytes)
                    throw new InvalidDataException(
                        $"pool expanded beyond {maxExpandedBytes} bytes (possible decompression bomb)");
                await destination.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        finally
        {
            if (gzip) await reader.DisposeAsync();
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
            using var fs = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entries = ParsePool(fs);
            _logger.Information("Pool cache: loaded {n} entries from disk", entries.Count);
            return entries;
        }
        catch (Exception ex)
        {
            _logger.Warning("Pool cache corrupted: {err}", ex.Message);
            try { File.Delete(_cachePath); } catch { }
            try { File.Delete(_etagPath); } catch { }
            try { File.Delete(_gzEtagPath); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Parse pool.json into FreeConfigEntry list.
    /// Schema: { updatedAt, version, sourceCount, totalConfigs, servers: [{id,host,port,uuid,sni,transport,security,country,resolvedIp,source,raw,firstSeen}] }
    /// </summary>
    internal static List<FreeConfigEntry> ParsePool(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParsePool(doc.RootElement);
    }

    internal static List<FreeConfigEntry> ParsePool(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParsePool(doc.RootElement);
    }

    private static List<FreeConfigEntry> ParsePool(JsonElement root)
    {
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
