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

public static class SubscriptionFetcher
{
    public static IHttpClient Http { get; set; } = PolicyHttpClient.Shared;

    private static readonly IReadOnlyDictionary<string, string> SubscriptionHeaders =
        new Dictionary<string, string>
        {
            ["X-VPNRouter-Capabilities"] = "detour-v1"
        };

    public static async Task<List<VlessServerEntry>> FetchAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var (entries, _, _) = await FetchWithDiagnosticsAsync(url, logger, ct);
        return entries;
    }

    internal static async Task<(List<VlessServerEntry> Entries, int DroppedPlaceholders, string? UserInfo)>
        FetchWithDiagnosticsAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var result = new List<VlessServerEntry>();
        var droppedPlaceholders = 0;
        string? userInfo = null;

        if (string.IsNullOrWhiteSpace(url))
            return (result, 0, userInfo);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            logger?.Warning("[Subscription] Refusing non-http(s) subscription URL: {Url}", CanaryPolicy.RedactUrl(url));
            return (result, 0, userInfo);
        }

        try
        {
            logger?.Information("[Subscription] Fetching {Url}", CanaryPolicy.RedactUrl(url));

            var httpResp = await Http.SendAsync(
                new HttpRequest(HttpMethod.Get, parsedUri,
                    Headers: SubscriptionHeaders,
                    Timeout: TimeSpan.FromSeconds(15)),
                ct);
            if (!httpResp.IsSuccess())
            {
                logger?.Warning("[Subscription] HTTP {Status} from {Url}", httpResp.StatusCode, CanaryPolicy.RedactUrl(url));
                return (result, 0, userInfo);
            }
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
                logger?.Warning("[Subscription] Empty response from {Url}", CanaryPolicy.RedactUrl(url));
                return (result, 0, userInfo);
            }

            result = ParseBody(response, out droppedPlaceholders, logger);

            if (droppedPlaceholders > 0)
            {
                logger?.Warning(
                    "[Subscription] Dropped {DroppedCount} entries with placeholder credentials from {Url} " +
                    "(likely test/sample URLs scraped by provider). User's other servers preserved.",
                    droppedPlaceholders, CanaryPolicy.RedactUrl(url));
            }

            logger?.Information("[Subscription] Fetched {Count} servers from {Url}", result.Count, CanaryPolicy.RedactUrl(url));
        }
        catch (Exception ex)
        {
            logger?.Error("[Subscription] Fetch failed for {Url}: {ExceptionType}",
                CanaryPolicy.RedactUrl(url), ex.GetType().Name);
        }

        return (result, droppedPlaceholders, userInfo);
    }

    internal static List<VlessServerEntry> ParseBody(string responseBody, ILogger? logger = null) =>
        ParseBody(responseBody, out _, logger);

    internal static List<VlessServerEntry> ParseBody(
        string responseBody, out int droppedPlaceholders, ILogger? logger = null)
    {
        droppedPlaceholders = 0;
        var result = new List<VlessServerEntry>();
        if (string.IsNullOrWhiteSpace(responseBody)) return result;

        string decoded;
        var trimmed = responseBody.Trim();

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
        else
        {
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            }
            catch (FormatException)
            {
                decoded = trimmed;
            }
        }

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
            if (!ServerUriParser.IsSupportedScheme(line))
                continue;

            try
            {
                var entry = ServerUriParser.Parse(line);

                if (PlaceholderDefense.IsPlaceholder(entry))
                {
                    droppedPlaceholders++;
                    continue;
                }

                result.Add(entry);
            }
            catch (PlaceholderConfigException)
            {
                droppedPlaceholders++;
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "[Subscription] Failed to parse line: {Line}",
                    CrashReporter.ScrubSecrets(line));
            }
        }

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

    public static async Task<int> RefreshEntryAsync(
        SubscriptionEntry entry, ILogger? logger = null, CancellationToken ct = default)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) return 0;

        var (servers, droppedPlaceholders, userInfo) = await FetchWithDiagnosticsAsync(entry.Url, logger, ct);
        if (ct.IsCancellationRequested) return 0;

        // Never overwrite a cached quota with null on a transient failure.
        if (userInfo != null) entry.UserInfo = userInfo;

        if (droppedPlaceholders > 0 && logger != null)
        {
            logger.Warning(
                "[Subscription] Refresh for {Url} dropped {DroppedCount} placeholder entries " +
                "(likely test/sample URLs scraped by provider). User's other servers preserved.",
                CanaryPolicy.RedactUrl(entry.Url), droppedPlaceholders);
        }

        if (servers.Count > 0)
        {
            entry.Servers = servers;
            entry.LastServerCount = servers.Count;
            entry.LastRefreshedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            logger?.Warning("[Subscription] Refresh returned 0 servers for {Url}, keeping {Cached} cached server(s)",
                CanaryPolicy.RedactUrl(entry.Url), entry.Servers?.Count ?? 0);
        }

        return servers.Count;
    }
}
