using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Fetches VLESS server list from a subscription URL.
/// The server returns base64-encoded text with one VLESS URI per line.
/// Compatible with v2rayNG / Streisand / Hiddify subscription format.
/// </summary>
public static class SubscriptionFetcher
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static SubscriptionFetcher()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "VPNRouter");
    }

    /// <summary>
    /// Fetch and parse subscription URL into a list of VLESS server entries.
    /// Returns empty list on error (never throws).
    /// </summary>
    public static async Task<List<VlessServerEntry>> FetchAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var (entries, _) = await FetchWithDiagnosticsAsync(url, logger, ct);
        return entries;
    }

    /// <summary>
    /// Same as <see cref="FetchAsync"/> but also returns the number of
    /// entries that were silently dropped because they matched a known
    /// placeholder fingerprint (<see cref="PlaceholderGuard"/>). Used by
    /// <see cref="RefreshEntryAsync"/> to surface a dedicated warning so
    /// users understand *why* their fetched server count is lower than
    /// the provider's apparent list size.
    /// </summary>
    internal static async Task<(List<VlessServerEntry> Entries, int DroppedPlaceholders)>
        FetchWithDiagnosticsAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var result = new List<VlessServerEntry>();
        var droppedPlaceholders = 0;

        if (string.IsNullOrWhiteSpace(url))
            return (result, 0);

        try
        {
            logger?.Information("[Subscription] Fetching {Url}", url);

            var response = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(response))
            {
                logger?.Warning("[Subscription] Empty response from {Url}", url);
                return (result, 0);
            }

            // v2.31.5+: parsing extracted to ParseBody for unit-testability
            // without an HTTP round-trip. Behaviour-preserving — FetchAsync
            // is still the only production caller and the user-visible
            // pipeline (HTTP → parse → dedup → list) is identical.
            result = ParseBody(response, out droppedPlaceholders, logger);

            if (droppedPlaceholders > 0)
            {
                logger?.Warning(
                    "[Subscription] Dropped {DroppedCount} entries with placeholder credentials from {Url} " +
                    "(likely test/sample URLs scraped by provider). User's other servers preserved.",
                    droppedPlaceholders, url);
            }

            logger?.Information("[Subscription] Fetched {Count} servers from {Url}", result.Count, url);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Subscription] Fetch failed for {Url}", url);
        }

        return (result, droppedPlaceholders);
    }

    /// <summary>
    /// Extract server entries from a subscription response body. Supports
    /// the three formats that providers in the wild use:
    /// <list type="number">
    ///   <item>JSON wrapper <c>{"config":"base64..."}</c> (ninitux.com).</item>
    ///   <item>Raw base64-encoded URI list (v2rayNG / Streisand / Hiddify).</item>
    ///   <item>Plain URIs separated by newlines.</item>
    /// </list>
    ///
    /// <para>Falls back gracefully: malformed JSON → tries trimmed body
    /// directly; non-base64 → tries plain URIs. Returns empty list on
    /// fully-unparseable input rather than throwing — the call site
    /// (FetchAsync) treats "0 entries returned" as transient and keeps
    /// the cached list, which is the right move when the only signal
    /// is "we got bytes back but couldn't make sense of them".</para>
    ///
    /// <para>Internal so <see cref="VPNRouter.Tests"/> can hit the
    /// parser branches directly via <c>InternalsVisibleTo</c>; not part
    /// of the public Core API.</para>
    /// </summary>
    internal static List<VlessServerEntry> ParseBody(string responseBody, ILogger? logger = null) =>
        ParseBody(responseBody, out _, logger);

    /// <summary>
    /// Overload of <see cref="ParseBody(string, ILogger?)"/> that also reports
    /// how many entries were silently dropped because they matched a known
    /// placeholder fingerprint (<see cref="PlaceholderGuard"/>). The caller
    /// (e.g. <see cref="RefreshEntryAsync"/>) can use this count to log a
    /// dedicated warning so users see *why* a subscription "lost" entries.
    ///
    /// <para>Why drop instead of throw: subscriptions in the wild scrape
    /// sample vless:// URLs from forums / Telegram channels. One placeholder
    /// in a list of seven shouldn't blow away the other six working servers.
    /// Lossy filtering keeps the user connected with the clean entries while
    /// still flagging the bad ones via the log path.</para>
    /// </summary>
    internal static List<VlessServerEntry> ParseBody(
        string responseBody, out int droppedPlaceholders, ILogger? logger = null)
    {
        droppedPlaceholders = 0;
        var result = new List<VlessServerEntry>();
        if (string.IsNullOrWhiteSpace(responseBody)) return result;

        // Extract base64 content — supports:
        // 1. JSON wrapper: {"config":"base64..."} (ninitux.com format)
        // 2. Raw base64 (v2rayNG/Streisand format)
        // 3. Plain VLESS URIs (one per line)
        string decoded;
        var trimmed = responseBody.Trim();

        // Try JSON with "config" field first
        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("config", out var configEl))
                {
                    var b64 = configEl.GetString() ?? "";
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    logger?.Debug("[Subscription] Parsed JSON wrapper, config decoded ({Len} chars)", decoded.Length);
                }
                else
                {
                    logger?.Warning("[Subscription] JSON response has no 'config' field");
                    return result;
                }
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[Subscription] Failed to parse JSON response");
                decoded = trimmed;
            }
        }
        // Try raw base64
        else
        {
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            }
            catch (FormatException)
            {
                // Not base64 — plain VLESS URIs
                decoded = trimmed;
            }
        }

        var lines = decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // v2.30.1-r3: accept any supported share-link scheme
            // (vless://, hysteria2://, hy2://, tuic://, ss://). The
            // ServerUriParser dispatches to the right per-protocol
            // parser internally.
            if (!ServerUriParser.IsSupportedScheme(line))
                continue;

            try
            {
                var entry = ServerUriParser.Parse(line);

                // v2.32.3 placeholder filter: silently drop entries that
                // match known placeholder bait (e.g. the PlaceholderVlessUri
                // pubkey from pre-r10 Android smoke builds — see
                // PlaceholderDefense.KnownFingerprints for the canonical list).
                // Subscription providers sometimes scrape sample URLs from
                // forums / Telegram and re-publish them — one bad entry
                // shouldn't kill the whole import. The post-loop counter
                // feeds a single aggregated warning at the caller so the
                // user can find out via log why their server count dropped.
                //
                // Defence-in-depth: ServerUriParser / VlessUriParser already
                // throws PlaceholderConfigException for known fingerprints
                // (Phase 2a). We catch that separately below — but also keep
                // this explicit check in case a future protocol parser is
                // added without the upstream gate, or the parser path is
                // bypassed by a future shortcut.
                if (PlaceholderGuard.IsPlaceholder(entry))
                {
                    droppedPlaceholders++;
                    continue;
                }

                result.Add(entry);
            }
            catch (PlaceholderConfigException)
            {
                // Parser already rejected the entry as placeholder bait —
                // count it for the aggregated warning instead of dropping
                // it into the generic "Failed to parse" bucket (which
                // implies a user-fixable typo).
                droppedPlaceholders++;
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[Subscription] Failed to parse line: {Line}", line);
            }
        }

        // Deduplicate by Server:Port:UUID:Flow (Flow differs for TCP/UDP split pairs)
        var seen = new HashSet<string>();
        var deduped = new List<VlessServerEntry>(result.Count);
        foreach (var e in result)
        {
            var key = $"{e.Server}:{e.Port}:{e.Uuid}:{e.Flow}";
            if (seen.Add(key)) deduped.Add(e);
        }
        if (deduped.Count < result.Count)
            logger?.Information("[Subscription] Deduplicated {Before}→{After} servers",
                result.Count, deduped.Count);
        result = deduped;

        if (result.Count >= 500)
            logger?.Warning("[Subscription] Large subscription: {Count} servers — may impact performance", result.Count);

        return result;
    }

    /// <summary>
    /// Refresh a single SubscriptionEntry: fetch servers, update timestamps.
    /// Returns the number of servers fetched (0 on failure).
    /// </summary>
    public static async Task<int> RefreshEntryAsync(
        SubscriptionEntry entry, ILogger? logger = null, CancellationToken ct = default)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) return 0;

        // Use the diagnostics overload so RefreshEntryAsync can surface
        // the placeholder count itself. FetchWithDiagnosticsAsync already
        // emits the canonical warning at the fetch site; we also emit a
        // parallel warning here so callers that subscribe a *separate*
        // logger to RefreshEntryAsync (e.g. UI status panel) still see
        // the placeholder drop. Both messages stay aligned.
        var (servers, droppedPlaceholders) = await FetchWithDiagnosticsAsync(entry.Url, logger, ct);
        if (ct.IsCancellationRequested) return 0;

        if (droppedPlaceholders > 0 && logger != null)
        {
            logger.Warning(
                "[Subscription] Refresh for {Url} dropped {DroppedCount} placeholder entries " +
                "(likely test/sample URLs scraped by provider). User's other servers preserved.",
                entry.Url, droppedPlaceholders);
        }

        // Only overwrite the cached server list on a successful fetch. If
        // FetchAsync returns 0 (network error, DNS failure, provider 500,
        // transient glitch) we keep the previously-cached servers so the VPN
        // still comes up. Without this, any network blip between user
        // starting VPNRouter and the subscription provider permanently wipes
        // their config and forces them to re-fetch before they can connect.
        if (servers.Count > 0)
        {
            entry.Servers = servers;
            entry.LastServerCount = servers.Count;
            entry.LastRefreshedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            logger?.Warning("[Subscription] Refresh returned 0 servers for {Url}, keeping {Cached} cached server(s)",
                entry.Url, entry.Servers?.Count ?? 0);
        }

        return servers.Count;
    }
}
