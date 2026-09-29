using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

public static class RuleSetCacheManager
{
    public static readonly TimeSpan MaxAgeForUseAsIs = TimeSpan.FromDays(7);

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    public const string CacheSubdir = "rulesets";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string?> EnsureLocalAsync(
        string url,
        string filename,
        ILogger? logger = null,
        HttpClient? httpClient = null,
        string? cacheDir = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must be non-empty", nameof(url));
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains(Path.DirectorySeparatorChar) || filename.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Filename must be a leaf, no path separators", nameof(filename));

        logger ??= Log.Logger;

        var dir = Path.Combine(cacheDir ?? AppPaths.CacheDir, CacheSubdir);
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            logger.Warning(ex, "[RuleSetCache] cannot create dir {Dir}", dir);
            return null;
        }

        var localPath = Path.Combine(dir, filename);
        var fileLock = FileLocks.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fi = new FileInfo(localPath);
            var existsLocally = fi.Exists && fi.Length > 0;
            var ageOk = existsLocally && (DateTime.UtcNow - fi.LastWriteTimeUtc) < MaxAgeForUseAsIs;

            if (ageOk)
            {
                logger.Debug("[RuleSetCache] using cached {Path} (age {Age})",
                    localPath, DateTime.UtcNow - fi.LastWriteTimeUtc);
                return localPath;
            }

            var ownsClient = false;
            if (httpClient == null)
            {
                httpClient = new HttpClient();
                ownsClient = true;
            }
            try
            {
                httpClient.Timeout = FetchTimeout;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(FetchTimeout);

                logger.Information("[RuleSetCache] fetching {Url} (timeout {Timeout})", CanaryPolicy.RedactUrl(url), FetchTimeout);
                var response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType != null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Invalid Content-Type: {mediaType}");

                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes[0] == (byte)'<')
                    throw new InvalidDataException("Downloaded rule-set is corrupt, truncated, or HTML");

                var tmp = localPath + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(tmp, bytes, cts.Token).ConfigureAwait(false);
                    File.Move(tmp, localPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp))
                        try { File.Delete(tmp); } catch { }
                }

                logger.Information("[RuleSetCache] cached {Path} ({Bytes} bytes)", localPath, bytes.Length);
                return localPath;
            }
            catch (Exception ex)
            {
                if (existsLocally)
                {
                    logger.Warning(
                        "[RuleSetCache] refresh failed ({Err}); falling back to stale {Path} (age {Age})",
                        ex.Message, localPath, DateTime.UtcNow - fi.LastWriteTimeUtc);
                    return localPath;
                }
                logger.Warning(
                    "[RuleSetCache] fetch failed ({Err}) and no cached copy at {Path}; rule-set will be omitted from config",
                    ex.Message, localPath);
                return null;
            }
            finally
            {
                if (ownsClient)
                    httpClient.Dispose();
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    public static string? EnsureLocal(
        string url,
        string filename,
        ILogger? logger = null,
        HttpClient? httpClient = null,
        string? cacheDir = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must be non-empty", nameof(url));
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains(Path.DirectorySeparatorChar) || filename.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Filename must be a leaf, no path separators", nameof(filename));

        var deadline = TimeSpan.FromSeconds(FetchTimeout.TotalSeconds * 2 + 1);
        try
        {
            using var cts = new CancellationTokenSource(deadline);
            return Task.Run(
                () => EnsureLocalAsync(url, filename, logger, httpClient, cacheDir, cts.Token),
                cts.Token).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            (logger ?? Log.Logger).Warning(ex, "[RuleSetCache] sync wrapper threw");
            return null;
        }
    }
}
