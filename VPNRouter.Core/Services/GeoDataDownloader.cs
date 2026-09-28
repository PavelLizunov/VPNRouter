using System.Net.Http;
using Serilog;

namespace VPNRouter.Core.Services;

public class GeoDataDownloader
{
    private const string GeoIpUrl = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-ru.srs";
    private const string GeoSiteUrl = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-tld-ru.srs";

    private const long MinGeoIpSize = 10 * 1024;
    private const long MinGeoSiteSize = 100;

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private readonly IHttpClient _http;
    private readonly ILogger _logger;

    public GeoDataDownloader(ILogger? logger = null)
        : this(PolicyHttpClient.Shared, logger)
    {
    }

    public GeoDataDownloader(IHttpClient http, ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? Log.Logger;
    }

    public async Task<bool> EnsureGeoFilesAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppPaths.GeoDir);

        var geoIpOk = await EnsureFileAsync(GeoIpUrl, AppPaths.GeoIpRuPath, MinGeoIpSize, "geoip-ru", ct);
        var geoSiteOk = await EnsureFileAsync(GeoSiteUrl, AppPaths.GeoSiteRuPath, MinGeoSiteSize, "geosite-ru", ct);

        return geoIpOk && geoSiteOk;
    }

    public static bool AreGeoFilesAvailable()
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
                }
                else
                {
                    var age = DateTime.UtcNow - fi.LastWriteTimeUtc;
                    if (age < RefreshAfter)
                    {
                        _logger.Debug("[GeoData] {Label} fresh ({Size} bytes, {AgeHours:F1}h old)", label, existing, age.TotalHours);
                        return true;
                    }
                    _logger.Information(
                        "[GeoData] {Label} stale ({AgeDays:F1} days old, threshold {ThreshDays:F0}) — refreshing",
                        label, age.TotalDays, RefreshAfter.TotalDays);
                }
            }

            _logger.Information("[GeoData] Downloading {Label} from {Url}", label, CanaryPolicy.RedactUrl(url));

            // Download to .tmp and rename so a partial download never replaces a working file.
            var tmpPath = destPath + ".tmp";
            try
            {
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
                    return File.Exists(destPath) && new FileInfo(destPath).Length >= minSize;
                }

                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmpPath, destPath);

                _logger.Information("[GeoData] {Label} downloaded ({Size} bytes)", label, size);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
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
