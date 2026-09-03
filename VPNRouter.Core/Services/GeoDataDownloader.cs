using System.Net.Http;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Downloads sing-box geo rule sets (geoip-ru.srs, geosite-ru.srs) for
/// routing Russian traffic directly (bypass VPN).
///
/// Sources (official SagerNet sing-box compatible rule sets):
///   geoip:   https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs
///            (all RU IP CIDR ranges)
///   geosite: https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-tld-ru.srs
///            (all .ru/.moscow/.tatar/IDN domains by TLD suffix)
///
/// MetaCubeX rule sets use an older format that's incompatible with sing-box 1.13.
/// SagerNet's official rule-set branch is the only source matching our sing-box version.
///
/// We use TLD-based geosite (not category-ru) because category-ru only contains
/// Yandex/Mail trackers (~30 entries), while tld-ru covers all Russian domains.
///
/// Files are saved to AppPaths.GeoDir and reused. Update is on-demand
/// (currently downloads only if file is missing).
/// </summary>
public class GeoDataDownloader
{
    private const string GeoIpUrl = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs";
    private const string GeoSiteUrl = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-tld-ru.srs";

    // Sanity-check thresholds — guard against truncated downloads / 404 HTML.
    // SagerNet sizes: geoip-ru ~50 KB, geosite-tld-ru ~150 bytes (just TLD list)
    private const long MinGeoIpSize = 10 * 1024;   // 10 KB minimum
    private const long MinGeoSiteSize = 100;       // 100 bytes minimum (TLD list is tiny)

    /// <summary>
    /// v2.31.6-r18: refresh-on-startup interval for geo files. Pre-r18 the
    /// downloader was strictly idempotent — once a file existed and passed
    /// sanity-size check, it was never re-fetched, even years later. RU
    /// CIDR ranges drift quarterly (new operators, migrations); the .ru
    /// TLD list is essentially static but theoretically grows. 7 days is
    /// a conservative refresh — most users restart VPNRouter several times
    /// a week, so this turns into "always reasonably fresh" without
    /// hammering SagerNet. Failed refresh keeps the existing file (best-
    /// effort: stale geo is better than disabled bypass).
    /// </summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    // v3.0 Phase 4 (2026-05-18): IHttpClient streaming seam replaces the
    // static `_http` field below. Production callers continue to use the
    // process-shared PolicyHttpClient via the parameterless overload;
    // tests inject a FakeHttpClient with `SetupStream(...)` so the
    // download path can be exercised without real network.
    private readonly IHttpClient _http;
    private readonly ILogger _logger;

    /// <summary>
    /// Production ctor — uses the process-shared <see cref="PolicyHttpClient"/>.
    /// </summary>
    public GeoDataDownloader(ILogger? logger = null)
        : this(PolicyHttpClient.Shared, logger)
    {
    }

    /// <summary>
    /// Test / DI ctor — caller supplies a custom <see cref="IHttpClient"/>
    /// (typically <c>FakeHttpClient</c> in tests).
    /// </summary>
    public GeoDataDownloader(IHttpClient http, ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Ensure both geo files are present locally. Downloads if missing.
    /// Returns true if both files are available after the call.
    /// Logs warnings on failure but does not throw — caller decides whether
    /// to enable bypass without geo files.
    /// </summary>
    public async Task<bool> EnsureGeoFilesAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.GeoDir);

        var geoIpOk = await EnsureFileAsync(GeoIpUrl, AppPaths.GeoIpRuPath, MinGeoIpSize, "geoip-ru", ct);
        var geoSiteOk = await EnsureFileAsync(GeoSiteUrl, AppPaths.GeoSiteRuPath, MinGeoSiteSize, "geosite-ru", ct);

        return geoIpOk && geoSiteOk;
    }

    private static (bool available, DateTime checkedAt) _cachedAvailability = (false, DateTime.MinValue);
    private static readonly TimeSpan AvailabilityCacheTtl = TimeSpan.FromSeconds(15);
    private static readonly object _availabilityLock = new();

    /// <summary>Invalidate the memory cache for geo-data availability.</summary>
    public static void InvalidateAvailabilityCache()
    {
        lock (_availabilityLock)
        {
            _cachedAvailability = (false, DateTime.MinValue);
        }
    }

    /// <summary>
    /// Returns true if both geo files exist on disk and pass sanity size check.
    /// Result is cached in memory with a short TTL to eliminate redundant disk stat probes.
    /// </summary>
    public static bool AreGeoFilesAvailable()
    {
        var now = DateTime.UtcNow;
        lock (_availabilityLock)
        {
            if (now - _cachedAvailability.checkedAt < AvailabilityCacheTtl)
                return _cachedAvailability.available;
        }

        var available = CheckGeoFilesOnDisk();

        lock (_availabilityLock)
        {
            _cachedAvailability = (available, now);
        }

        return available;
    }

    private static bool CheckGeoFilesOnDisk()
    {
        try
        {
            if (!File.Exists(AppPaths.GeoIpRuPath) || !File.Exists(AppPaths.GeoSiteRuPath))
                return false;

            var ipSize = new FileInfo(AppPaths.GeoIpRuPath).Length;
            var siteSize = new FileInfo(AppPaths.GeoSiteRuPath).Length;
            return ipSize >= MinGeoIpSize && siteSize >= MinGeoSiteSize;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureFileAsync(string url, string destPath, long minSize, string label, CancellationToken ct)
    {
        try
        {
            if (File.Exists(destPath))
            {
                var fi = new FileInfo(destPath);
                var existing = fi.Length;
                if (existing < minSize)
                {
                    _logger.Warning("[GeoData] {Label} exists but too small ({Size} bytes) — re-downloading", label, existing);
                    File.Delete(destPath);
                    InvalidateAvailabilityCache();
                }
                else
                {
                    // v2.31.6-r18: refresh on startup if stale (>7 days).
                    // Pre-r18 was strictly "exists + size OK ⇒ skip" → files
                    // never refreshed for the lifetime of the install.
                    var age = DateTime.UtcNow - fi.LastWriteTimeUtc;
                    if (age < RefreshAfter)
                    {
                        _logger.Debug("[GeoData] {Label} fresh ({Size} bytes, {AgeHours:F1}h old)", label, existing, age.TotalHours);
                        return true;
                    }
                    _logger.Information(
                        "[GeoData] {Label} stale ({AgeDays:F1} days old, threshold {ThreshDays:F0}) — refreshing",
                        label, age.TotalDays, RefreshAfter.TotalDays);
                    // fall through to download — keep old file as fallback
                    // if download fails (best-effort refresh).
                }
            }

            _logger.Information("[GeoData] Downloading {Label} from {Url}", label, url);

            // Refresh path: download to a .tmp first, then atomic-rename
            // so a half-finished download doesn't replace a working file.
            var tmpPath = destPath + ".tmp";
            try
            {
                // v3.0 Phase 4: IHttpClient.SendStreamingAsync replaces the
                // legacy `_http.GetAsync(..., ResponseHeadersRead)` call. The
                // wrapper's await-using disposes the response + body stream
                // even if CopyToAsync below throws mid-transfer, so the
                // socket is freed without a half-read connection leak.
                await using (var response = await _http.SendStreamingAsync(
                    new HttpRequest(HttpMethod.Get, new Uri(url)),
                    ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccess())
                        throw new HttpRequestException(
                            $"HTTP {response.StatusCode} downloading {label}");

                    await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Body.CopyToAsync(fs, ct);
                }

                var size = new FileInfo(tmpPath).Length;
                if (size < minSize)
                {
                    _logger.Warning("[GeoData] {Label} download too small ({Size} bytes) — corrupted?", label, size);
                    try { File.Delete(tmpPath); } catch { }
                    // If a previous valid file exists, keep it — caller will
                    // see existing file via File.Exists(destPath) on next
                    // call. Return whether existing file is still valid.
                    return File.Exists(destPath) && new FileInfo(destPath).Length >= minSize;
                }

                // Atomic replace (Windows allows File.Move with overwrite as of .NET Core 3+).
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmpPath, destPath);
                InvalidateAvailabilityCache();

                _logger.Information("[GeoData] {Label} downloaded ({Size} bytes)", label, size);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                // Refresh failed but we may already have a valid stale file.
                if (File.Exists(destPath) && new FileInfo(destPath).Length >= minSize)
                {
                    _logger.Warning(ex, "[GeoData] Failed to refresh {Label} — keeping existing stale copy", label);
                    return true;
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GeoData] Failed to download {Label}", label);
            return false;
        }
    }
}
