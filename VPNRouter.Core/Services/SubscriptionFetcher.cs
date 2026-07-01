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
    // 3G-2 (v3.0 refactor): replaced per-class `static readonly HttpClient`
    // with the shared IHttpClient seam. Static-class workaround for ctor
    // injection: settable property defaulting to PolicyHttpClient.Shared.
    // Tests assign FakeHttpClient before calling FetchAsync; production
    // leaves it at the default (User-Agent + 5min DNS-refresh + retry
    // policy all come from PolicyHttpClient).
    /// <summary>
    /// HTTP seam — tests may override to inject <c>FakeHttpClient</c>.
    /// Defaults to <see cref="PolicyHttpClient.Shared"/>.
    /// </summary>
    public static IHttpClient Http { get; set; } = PolicyHttpClient.Shared;

    /// <summary>
    /// Fetch and parse subscription URL into a list of VLESS server entries.
    /// Returns empty list on error (never throws).
    /// </summary>
    public static async Task<List<VlessServerEntry>> FetchAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var (entries, _, _) = await FetchWithDiagnosticsAsync(url, logger, ct);
        return entries;
    }

    /// <summary>
    /// Same as <see cref="FetchAsync"/> but also returns the number of
    /// entries that were silently dropped because they matched a known
    /// placeholder fingerprint (<see cref="PlaceholderDefense"/>). Used by
    /// <see cref="RefreshEntryAsync"/> to surface a dedicated warning so
    /// users understand *why* their fetched server count is lower than
    /// the provider's apparent list size.
    /// </summary>
    internal static async Task<(List<VlessServerEntry> Entries, int DroppedPlaceholders, string? UserInfo)>
        FetchWithDiagnosticsAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var result = new List<VlessServerEntry>();
        var droppedPlaceholders = 0;
        string? userInfo = null; // P2: Subscription-Userinfo response header (quota/expiry)

        if (string.IsNullOrWhiteSpace(url))
            return (result, 0, userInfo);

        try
        {
            logger?.Information("[Subscription] Fetching {Url}", url);

            // 3G-2: bundled User-Agent + retry come from PolicyHttpClient
            // policy; per-request 15s timeout preserved for back-compat.
            var httpResp = await Http.SendAsync(
                new HttpRequest(HttpMethod.Get, new Uri(url),
                    Timeout: TimeSpan.FromSeconds(15)),
                ct);
            if (!httpResp.IsSuccess())
            {
                logger?.Warning("[Subscription] HTTP {Status} from {Url}", httpResp.StatusCode, url);
                return (result, 0, userInfo);
            }
            // P2: capture Subscription-Userinfo (case-insensitive — header key-folding
            // varies by IHttpClient impl). Providers put quota + expiry here.
            if (httpResp.Headers != null)
            {
                foreach (var kv in httpResp.Headers)
                {
                    if (string.Equals(kv.Key, "subscription-userinfo", StringComparison.OrdinalIgnoreCase))
                    { userInfo = kv.Value; break; }
                }
            }
            var response = httpResp.AsString();
            if (string.IsNullOrWhiteSpace(response))
            {
                logger?.Warning("[Subscription] Empty response from {Url}", url);
                return (result, 0, userInfo);
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

        return (result, droppedPlaceholders, userInfo);
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
    /// placeholder fingerprint (<see cref="PlaceholderDefense"/>). The caller
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

        // P6 (2026-06-21): Clash / Clash-Meta YAML subscriptions ship a
        // `proxies:` sequence instead of a URI list. Detect + map each proxy to
        // its share-link URI, then reuse the same per-line parser below (and its
        // placeholder guard). Tolerant: unsupported proxy types are skipped.
        string[] lines;
        if (ClashYamlParser.LooksLikeClashYaml(decoded))
        {
            var clashUris = ClashYamlParser.ParseProxiesToUris(decoded, logger);
            logger?.Information("[Subscription] Clash YAML detected — {N} proxies mapped to share URIs", clashUris.Count);
            lines = clashUris.ToArray();
        }
        else
        {
            lines = decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

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
                if (PlaceholderDefense.IsPlaceholder(entry))
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
                // Scrub before logging: a failing awg:// / vless:// line carries
                // private_key / uuid / keys, and the raw vpnrouter.log is written
                // BEFORE diagnostics-export redaction runs. ScrubSecrets collapses
                // the proxy URI (incl. awg://) so no secret reaches the log file.
                logger?.Warning(ex, "[Subscription] Failed to parse line: {Line}",
                    CrashReporter.ScrubSecrets(line));
            }
        }

        // Deduplicate by Server:Port:UUID:Flow:Username (Flow differs for TCP/UDP
        // split pairs; Username distinguishes NaiveProxy servers that share a
        // host:port but carry no UUID/Flow — without it two distinct naive creds
        // on the same endpoint would collapse to one. Empty for every non-naive
        // protocol, so their dedup behaviour is unchanged).
        var seen = new HashSet<string>();
        var deduped = new List<VlessServerEntry>(result.Count);
        foreach (var e in result)
        {
            var key = $"{e.Server}:{e.Port}:{e.Uuid}:{e.Flow}:{e.Username}:{e.Password}";
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
        var (servers, droppedPlaceholders, userInfo) = await FetchWithDiagnosticsAsync(entry.Url, logger, ct);
        if (ct.IsCancellationRequested) return 0;

        // P2: persist the provider's quota/expiry header when present (independent of
        // server parsing; never wipe a good cached value with null on a transient fail).
        if (userInfo != null) entry.UserInfo = userInfo;

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
