using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// v2.31.9-r3 — manages local cache of remote sing-box rule-set files
/// (.srs) so a flaky raw.githubusercontent.com fetch can no longer
/// crash sing-box at startup.
///
/// <para><b>The bug we close (brat 2026-05-05)</b>: <see cref="ConfigGenerator.ApplyAdBlock"/>
/// adds a <c>type:remote</c> rule-set with <c>download_detour:direct</c>
/// pointing at a GitHub raw URL. sing-box treats the initial fetch as
/// MANDATORY — a TLS handshake timeout = <c>FATAL initialize rule-set
/// vpnrouter-adblock</c> = process exit code 1. <see cref="HealthMonitor"/>
/// then loops on the same FATAL until either the network recovers OR
/// the user gives up. brat-2026-05-05 user logged 4+ FATAL crashes in
/// 90 seconds before fluke-success and a "странно вёл себя VPN" report.</para>
///
/// <para><b>The fix</b>: instead of letting sing-box fetch synchronously
/// at startup, we pre-cache the .srs file in <c>%CacheDir%\rulesets\</c>
/// from C# (where we control the timeout, retries, and graceful
/// fallback) and reference it as <c>type:local</c>. If we can't refresh
/// the cache and no stale copy exists, the rule-set is omitted from
/// the generated config entirely — losing ad blocking is FINE; losing
/// the entire VPN is NOT.</para>
///
/// <para><b>Refresh cadence</b>: a cache file fresher than
/// <see cref="MaxAgeForUseAsIs"/> is used as-is, no fetch attempt.
/// Older than that, we try to refresh (with the timeout below); on
/// success update the cache, on failure keep using the stale file.</para>
///
/// <para>This is intentionally simple synchronous-on-first-use code.
/// Phase 4 atomic-replace upgrades or a Phase 2 background refresher
/// could push it to a worker, but it's invoked once per VPN start and
/// has a hard timeout so blocking is bounded.</para>
/// </summary>
public static class RuleSetCacheManager
{
    /// <summary>How long a cached .srs is allowed to be served as-is
    /// without a refresh attempt. Matches the in-config update_interval
    /// of 168h (one week). Beyond this we try a fresh fetch but still
    /// fall back to the stale copy if the fetch fails.</summary>
    public static readonly TimeSpan MaxAgeForUseAsIs = TimeSpan.FromDays(7);

    /// <summary>HTTP request timeout for a single rule-set fetch. Bounded
    /// at 10 seconds because we block VPN start on this; longer means
    /// the user clicks Connect and stares at a frozen UI.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Subdirectory under <c>%CacheDir%</c> where cached
    /// rule-sets live. Created on demand by <see cref="EnsureLocalAsync"/>.</summary>
    public const string CacheSubdir = "rulesets";

    /// <summary>
    /// Ensure a local copy of the rule-set at <paramref name="url"/> is
    /// available, returning its absolute path. Returns null if no
    /// local copy can be obtained AND no fetch succeeds.
    ///
    /// <para>Caller (typically <see cref="ConfigGenerator.ApplyAdBlock"/>)
    /// should treat <c>null</c> as "rule-set unavailable, generate
    /// config without it" — gracefully degrades rather than crashing
    /// sing-box.</para>
    /// </summary>
    /// <param name="url">Remote URL to fetch from.</param>
    /// <param name="filename">Local cache filename (e.g.
    ///   <c>adblock_reject.srs</c>). Stored under
    ///   <c>%CacheDir%/rulesets/</c>.</param>
    /// <param name="logger">Optional logger; falls back to <see cref="Log.Logger"/>.</param>
    /// <param name="httpClient">Optional <see cref="HttpClient"/> override
    ///   for unit tests. Production callers pass null and a default client
    ///   is used.</param>
    /// <param name="cacheDir">Optional cache-dir override for tests.
    ///   Production callers pass null and <see cref="AppPaths.CacheDir"/>
    ///   is used.</param>
    /// <param name="cancellationToken">Cancellation; we never block
    ///   indefinitely, but cancellation lets a Stop request abort the
    ///   in-flight fetch.</param>
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
        var fi = new FileInfo(localPath);
        var existsLocally = fi.Exists && fi.Length > 0;
        var ageOk = existsLocally && (DateTime.UtcNow - fi.LastWriteTimeUtc) < MaxAgeForUseAsIs;

        if (ageOk)
        {
            logger.Debug("[RuleSetCache] using cached {Path} (age {Age})",
                localPath, DateTime.UtcNow - fi.LastWriteTimeUtc);
            return localPath;
        }

        // Need to refresh. Either we have a stale file (use as fallback if fetch fails),
        // or no file at all (fetch is mandatory for a usable rule-set).
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

            logger.Information("[RuleSetCache] fetching {Url} (timeout {Timeout})", url, FetchTimeout);
            var response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length == 0)
                throw new InvalidDataException("empty body");

            // Atomic write: tmp → rename.
            var tmp = localPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, cts.Token).ConfigureAwait(false);
            if (File.Exists(localPath))
                File.Delete(localPath);
            File.Move(tmp, localPath);
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

    /// <summary>
    /// Synchronous helper that wraps <see cref="EnsureLocalAsync"/> for
    /// callers in non-async code paths (notably <see cref="ConfigGenerator"/>
    /// which is invoked from a synchronous static context).
    ///
    /// <para>Bounded by <c>FetchTimeout × 2 + 1s</c> wall-clock; if that
    /// elapses we return null and let the caller skip the rule-set.</para>
    ///
    /// <para>Argument-validation errors (<see cref="ArgumentException"/>)
    /// rethrow as expected; only IO / network exceptions are swallowed
    /// because those are the cases where the caller wants graceful
    /// degradation rather than a startup crash.</para>
    /// </summary>
    public static string? EnsureLocal(
        string url,
        string filename,
        ILogger? logger = null,
        HttpClient? httpClient = null,
        string? cacheDir = null)
    {
        // Validate args synchronously so contract failures don't hide
        // behind the catch-all below.
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL must be non-empty", nameof(url));
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains(Path.DirectorySeparatorChar) || filename.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Filename must be a leaf, no path separators", nameof(filename));

        var deadline = TimeSpan.FromSeconds(FetchTimeout.TotalSeconds * 2 + 1);
        try
        {
            using var cts = new CancellationTokenSource(deadline);
            // Run on a task to avoid sync-over-async deadlock if a UI
            // SynchronizationContext is captured; .GetAwaiter().GetResult()
            // unwraps AggregateException but rethrows underlying exceptions.
            return Task.Run(
                () => EnsureLocalAsync(url, filename, logger, httpClient, cacheDir, cts.Token),
                cts.Token).GetAwaiter().GetResult();
        }
        catch (ArgumentException)
        {
            // Should be caught by the pre-validation above, but be
            // defensive in case a future signature change adds new args.
            throw;
        }
        catch (Exception ex)
        {
            (logger ?? Log.Logger).Warning(ex, "[RuleSetCache] sync wrapper threw");
            return null;
        }
    }
}
