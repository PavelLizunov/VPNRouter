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
    private readonly Func<string, CancellationToken, Task<bool>> _checkContents;

    public GeoDataDownloader(ILogger? logger = null)
        : this(PolicyHttpClient.Shared, logger)
    {
    }

    public GeoDataDownloader(IHttpClient http, ILogger? logger = null)
        : this(http, logger, RuleSetFile.CheckContentsAsync) { }

    internal GeoDataDownloader(IHttpClient http, ILogger? logger, Func<string, CancellationToken, Task<bool>> checkContents)
    {
        _checkContents = checkContents;
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

    public static bool AreGeoFilesAvailable() => AreGeoFilesAvailable(RuleSetFile.CheckContentsAsync);

    internal static bool AreGeoFilesAvailable(Func<string, CancellationToken, Task<bool>> checkContents)
    {
        try
        {
            return IsUsableAsync(AppPaths.GeoIpRuPath, MinGeoIpSize, checkContents, CancellationToken.None).GetAwaiter().GetResult()
                && IsUsableAsync(AppPaths.GeoSiteRuPath, MinGeoSiteSize, checkContents, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool UseGeoBypass(bool enabled, Action<string>? onMissingRuleSet,
        Func<string, CancellationToken, Task<bool>>? checkContents)
    {
        if (!enabled) return false;
        if (AreGeoFilesAvailable(checkContents ?? RuleSetFile.CheckContentsAsync)) return true;
        onMissingRuleSet?.Invoke("The enabled RU bypass requires readable geoip and geosite rule-sets.");
        return false;
    }

    private static async Task<bool> IsUsableAsync(string path, long minSize,
        Func<string, CancellationToken, Task<bool>> checkContents, CancellationToken ct) =>
        File.Exists(path) && new FileInfo(path).Length >= minSize && RuleSetFile.IsValidBinary(path)
        && await checkContents(path, ct).ConfigureAwait(false);

    internal async Task<bool> EnsureFileAsync(string url, string destPath, long minSize, string label, CancellationToken ct)
    {
        bool usable = false;
        try
        {
            usable = await IsUsableAsync(destPath, minSize, _checkContents, ct).ConfigureAwait(false);
            if (usable && DateTime.UtcNow - File.GetLastWriteTimeUtc(destPath) < RefreshAfter)
                return true;

            _logger.Information("[GeoData] Downloading {Label} from {Url}", label, CanaryPolicy.RedactUrl(url));
            await using var response = await _http.SendStreamingAsync(
                new HttpRequest(HttpMethod.Get, new Uri(url)), ct).ConfigureAwait(false);
            if (!response.IsSuccess())
                throw new HttpRequestException($"HTTP {response.StatusCode} downloading {label}");
            await InstallCandidateAsync(response.Body, destPath, minSize, _checkContents, ct).ConfigureAwait(false);
            _logger.Information("[GeoData] Updated {Label}", label);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[GeoData] Could not update {Label}. Existing file usable: {Usable}", label, usable);
            return usable;
        }
    }

    private static async Task InstallCandidateAsync(Stream source, string destPath, long minSize,
        Func<string, CancellationToken, Task<bool>> checkContents, CancellationToken ct)
    {
        var tmpPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (fs.Position + count > RuleSetFile.MaxFileBytes)
                        throw new InvalidDataException("Geo rule-set exceeds the size limit");
                    await fs.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
            }
            if (!await IsUsableAsync(tmpPath, minSize, checkContents, ct).ConfigureAwait(false))
                throw new InvalidDataException("Cannot load the candidate geo rule-set");
            File.Move(tmpPath, destPath, overwrite: true);
        }
        finally
        {
            File.Delete(tmpPath);
        }
    }
}
