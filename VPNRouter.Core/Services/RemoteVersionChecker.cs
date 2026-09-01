#nullable enable
// =============================================================================
// RemoteVersionChecker — v2.37.0-r37 (2026-05-25)
//
// Lightweight GitHub-API version probe with 6-hour TTL cache.
//
// Used by Zapret and TgProxy auto-update-on-start flows: on every "magic
// button" click we check if a newer upstream release exists, and if so
// kick off the full download. The 6h TTL keeps us well within GitHub's
// 60 req/hr unauthenticated rate-limit even with multi-thousand active
// installs world-wide.
//
// Cache file: %ProgramData%\VPNRouter\cache\remote_versions.json
//   {
//     "Flowseal/zapret-discord-youtube": {
//       "LatestTag": "v3.9.2",
//       "LastCheckUtc": "2026-05-25T18:00:00Z"
//     },
//     "siberia-min/telegram-mtproto-proxy-binary": { ... }
//   }
//
// All file/network ops are best-effort: cache corruption / network errors
// degrade to "no info" (caller falls back to existing IsInstalled() guard
// — never breaks the start flow).
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class RemoteVersionCacheEntry
{
    public string LatestTag { get; set; } = string.Empty;
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;
}

public static class RemoteVersionChecker
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static string CachePath
        => Path.Combine(AppPaths.CacheDir, "remote_versions.json");

    private static readonly object _lock = new();

    /// <summary>
    /// Fetch the latest release tag for a GitHub repo, using a 6-hour cache
    /// so we don't hammer GitHub on every start. Returns null on network
    /// failure or rate-limit — caller should treat that as "no info"
    /// (existing IsInstalled() guard handles the empty case).
    /// </summary>
    /// <param name="ownerRepo">"Owner/Repo" form, e.g. "Flowseal/zapret-discord-youtube".</param>
    /// <param name="userAgent">User-Agent header required by GitHub API.</param>
    public static async Task<string?> GetLatestTagAsync(
        string ownerRepo,
        string userAgent,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerRepo)) return null;

        // 1. Fast-path: hit the cache.
        var cached = TryLoadEntry(ownerRepo, logger);
        if (cached != null
            && !string.IsNullOrEmpty(cached.LatestTag)
            && (DateTime.UtcNow - cached.LastCheckUtc) < CacheTtl)
        {
            logger?.Debug("[RemoteVersionChecker] Cache hit for {Repo}: {Tag} ({AgeMin} min old)",
                ownerRepo, cached.LatestTag,
                (int)(DateTime.UtcNow - cached.LastCheckUtc).TotalMinutes);
            return cached.LatestTag;
        }

        // 2. Fetch from GitHub.
        string? tag = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{ownerRepo}/releases/latest";
            logger?.Debug("[RemoteVersionChecker] Fetching {Url}", CanaryPolicy.RedactUrl(url));

            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.Information(
                    "[RemoteVersionChecker] HTTP {Code} from {Url} — keeping last-known cache value",
                    (int)resp.StatusCode, CanaryPolicy.RedactUrl(url));
                // Return cached value (even if stale) if we have one.
                return cached?.LatestTag;
            }

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagProp)
                && tagProp.ValueKind == JsonValueKind.String)
            {
                tag = tagProp.GetString();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Debug(ex,
                "[RemoteVersionChecker] Failed to fetch latest tag for {Repo} — using cached value",
                ownerRepo);
            return cached?.LatestTag;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            logger?.Debug("[RemoteVersionChecker] Empty tag_name from {Repo} — using cached value",
                ownerRepo);
            return cached?.LatestTag;
        }

        // 3. Update cache.
        SaveEntry(ownerRepo, new RemoteVersionCacheEntry
        {
            LatestTag = tag!,
            LastCheckUtc = DateTime.UtcNow,
        }, logger);

        logger?.Information("[RemoteVersionChecker] Latest tag for {Repo}: {Tag}", ownerRepo, tag);
        return tag;
    }

    /// <summary>
    /// Normalize a tag for comparison: strip leading "v", trim whitespace.
    /// Useful when GitHub tags are "v3.9.2" but local version.txt holds "3.9.2".
    /// </summary>
    public static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var t = tag.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase) && t.Length > 1)
            t = t.Substring(1);
        return t;
    }

    /// <summary>
    /// Returns true when remote tag differs from local version (after
    /// normalization). Empty/null on either side returns false (no info —
    /// don't trigger spurious updates).
    /// </summary>
    public static bool IsNewer(string? remoteTag, string? localTag)
    {
        var r = NormalizeTag(remoteTag);
        var l = NormalizeTag(localTag);
        if (string.IsNullOrEmpty(r) || string.IsNullOrEmpty(l)) return false;
        return !string.Equals(r, l, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, RemoteVersionCacheEntry> LoadAll(ILogger? logger)
    {
        try
        {
            if (!File.Exists(CachePath))
                return new Dictionary<string, RemoteVersionCacheEntry>(StringComparer.Ordinal);
            var json = File.ReadAllText(CachePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, RemoteVersionCacheEntry>>(json);
            return dict ?? new Dictionary<string, RemoteVersionCacheEntry>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[RemoteVersionChecker] LoadAll failed — starting fresh");
            return new Dictionary<string, RemoteVersionCacheEntry>(StringComparer.Ordinal);
        }
    }

    private static RemoteVersionCacheEntry? TryLoadEntry(string ownerRepo, ILogger? logger)
    {
        lock (_lock)
        {
            var all = LoadAll(logger);
            return all.TryGetValue(ownerRepo, out var entry) ? entry : null;
        }
    }

    private static void SaveEntry(string ownerRepo, RemoteVersionCacheEntry entry, ILogger? logger)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.CacheDir);
                var all = LoadAll(logger);
                all[ownerRepo] = entry;
                var json = JsonSerializer.Serialize(all,
                    new JsonSerializerOptions { WriteIndented = true });
                var tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[RemoteVersionChecker] SaveEntry failed (best-effort)");
            }
        }
    }
}
