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

public sealed class UnsupportedSubscriptionChainException : InvalidOperationException
{
    public UnsupportedSubscriptionChainException()
        : base("No servers could be imported. The subscription uses an unsupported proxy chain.") { }
}

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
        var result = await FetchWithDiagnosticsAsync(url, logger, ct);
        if (result.ChainImportFailed) throw new UnsupportedSubscriptionChainException();
        return result.Entries;
    }

    internal static async Task<(List<VlessServerEntry> Entries, int DroppedPlaceholders, string? UserInfo, bool ChainImportFailed)>
        FetchWithDiagnosticsAsync(string url, ILogger? logger = null, CancellationToken ct = default)
    {
        var result = new List<VlessServerEntry>();
        var droppedPlaceholders = 0;
        var skippedChains = false;
        string? userInfo = null;

        if (string.IsNullOrWhiteSpace(url))
            return (result, 0, userInfo, false);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            logger?.Warning("[Subscription] Refusing non-http(s) subscription URL");
            return (result, 0, userInfo, false);
        }

        try
        {
            logger?.Information("[Subscription] Fetching server list");

            var httpResp = await Http.SendAsync(
                new HttpRequest(HttpMethod.Get, parsedUri,
                    Headers: SubscriptionHeaders,
                    Timeout: TimeSpan.FromSeconds(15)),
                ct);
            if (!httpResp.IsSuccess())
            {
                logger?.Warning("[Subscription] Fetch returned HTTP {Status}", httpResp.StatusCode);
                return (result, 0, userInfo, false);
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
                logger?.Warning("[Subscription] Empty response");
                return (result, 0, userInfo, false);
            }

            result = ParseBody(response, out droppedPlaceholders, out skippedChains, logger);

            if (droppedPlaceholders > 0)
            {
                logger?.Warning(
                    "[Subscription] Dropped {DroppedCount} entries with placeholder credentials",
                    droppedPlaceholders);
            }

            logger?.Information("[Subscription] Fetched {Count} servers", result.Count);
        }
        catch (Exception ex)
        {
            logger?.Error("[Subscription] Fetch failed ({ExceptionType})", ex.GetType().Name);
        }

        return (result, droppedPlaceholders, userInfo, skippedChains && result.Count == 0);
    }

    internal static List<VlessServerEntry> ParseBody(string responseBody, ILogger? logger = null) =>
        ParseBody(responseBody, out _, logger);

    internal static List<VlessServerEntry> ParseBody(
        string responseBody, out int droppedPlaceholders, ILogger? logger = null) =>
        ParseBody(responseBody, out droppedPlaceholders, out _, logger);

    private static List<VlessServerEntry> ParseBody(
        string responseBody, out int droppedPlaceholders, out bool skippedChains, ILogger? logger)
    {
        var lines = DecodeToShareUris(responseBody, logger, out skippedChains);
        if (skippedChains)
            logger?.Warning("[Subscription] Skipped entries with an unsupported proxy chain");
        var result = DeduplicateServers(ParseShareUris(lines, logger, out droppedPlaceholders), logger);

        if (result.Count >= 500)
            logger?.Warning("[Subscription] Large subscription: {Count} servers — may impact performance", result.Count);

        return result;
    }

    private static IEnumerable<string> DecodeToShareUris(string responseBody, ILogger? logger, out bool skippedChains)
    {
        skippedChains = false;
        if (string.IsNullOrWhiteSpace(responseBody)) return Array.Empty<string>();
        var trimmed = responseBody.Trim();
        string decoded;

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
                else if (SingBoxJsonSubscription.LooksLikeSingBoxConfig(doc.RootElement))
                {
                    var uris = SingBoxJsonSubscription.ParseOutboundsToUris(doc.RootElement, out skippedChains, logger);
                    logger?.Information("[Subscription] sing-box JSON detected — {N} proxy outbounds mapped to share URIs", uris.Count);
                    return uris;
                }
                else
                {
                    logger?.Warning("[Subscription] JSON response has no 'config' field");
                    return Array.Empty<string>();
                }
            }
            catch (Exception)
            {
                logger?.Warning("[Subscription] Failed to parse JSON response");
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

        if (ClashYamlParser.LooksLikeClashYaml(decoded))
        {
            var clashUris = ClashYamlParser.ParseProxiesToUris(decoded, out skippedChains, logger);
            logger?.Information("[Subscription] Clash YAML detected — {N} proxies mapped to share URIs", clashUris.Count);
            return clashUris;
        }

        return decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<VlessServerEntry> ParseShareUris(
        IEnumerable<string> lines, ILogger? logger, out int droppedPlaceholders)
    {
        droppedPlaceholders = 0;
        var result = new List<VlessServerEntry>();

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
            catch (Exception)
            {
                logger?.Warning("[Subscription] Skipped invalid server URI");
            }
        }

        return result;
    }

    private static List<VlessServerEntry> DeduplicateServers(List<VlessServerEntry> result, ILogger? logger)
    {
        var deduped = result.DistinctBy(e =>
            (e.Server, e.Port, e.Uuid, e.Flow, e.Username, e.Password, e.OutboundId, e.DetourVia)).ToList();
        if (deduped.Count < result.Count)
            logger?.Information("[Subscription] Deduplicated {Before}→{After} servers",
                result.Count, deduped.Count);
        return deduped;
    }

    public static async Task<int> RefreshEntryAsync(
        SubscriptionEntry entry, ILogger? logger = null, CancellationToken ct = default)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Url)) return 0;

        var (servers, droppedPlaceholders, userInfo, chainImportFailed) = await FetchWithDiagnosticsAsync(entry.Url, logger, ct);
        if (ct.IsCancellationRequested) return 0;

        // Never overwrite a cached quota with null on a transient failure.
        if (userInfo != null) entry.UserInfo = userInfo;

        if (droppedPlaceholders > 0 && logger != null)
        {
            logger.Warning(
                "[Subscription] Refresh dropped {DroppedCount} placeholder entries", droppedPlaceholders);
        }

        if (servers.Count > 0 || chainImportFailed)
        {
            // A parsed feed can supersede cached rows that older versions imported without their detours.
            entry.Servers = servers;
            entry.LastServerCount = servers.Count;
            entry.LastRefreshedAt = DateTimeOffset.UtcNow;
            if (chainImportFailed) throw new UnsupportedSubscriptionChainException();
        }
        else
        {
            logger?.Warning("[Subscription] Refresh returned 0 servers, keeping {Cached} cached server(s)",
                entry.Servers?.Count ?? 0);
        }

        return servers.Count;
    }
}

// Native names refer to rows in one document. Resolve them before assigning app IDs,
// since imported rows from multiple subscriptions can later share the same server list.
internal sealed class NativeSubscriptionRows
{
    private sealed class Row(string? name, string? via, bool canChain, string? uri)
    {
        public string? Name { get; } = name;
        public string? Via { get; } = via;
        public string? ShareUri { get; } = uri;
        public string? DisplayName => ShareUri is null ? null :
            Uri.UnescapeDataString(ShareUri[(ShareUri.LastIndexOf('#') + 1)..]);
        public string? OutboundId { get; set; }
        public string? DetourId { get; set; }

        private bool? _usable;
        public bool IsUsableHop => _usable ??= canChain && ShareUri is not null && VlessUriParser.TryParse(ShareUri) is not null;
    }

    private readonly List<Row> _rows = new();

    internal void Add(string? nativeName, string? via, bool canChain, string? uri) =>
        _rows.Add(new Row(nativeName, via, canChain, uri));

    internal static bool IsValidHost(string? host) =>
        Uri.CheckHostName(host) != UriHostNameType.Unknown;

    internal List<string> ToShareUris(out bool skippedChains, bool allowChains = true)
    {
        skippedChains = false;
        if (_rows.All(row => row.Via is null))
            return _rows.Where(row => row.ShareUri is not null).Select(row => row.ShareUri!).ToList();

        var byName = _rows.ToLookup(row => row.Name, StringComparer.Ordinal);
        // The app selects a target by display name, case-insensitively.
        var byDisplayName = _rows.Where(row => row.ShareUri is not null)
            .ToLookup(row => row.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var target in _rows.Where(row => row.Via is not null))
        {
            var matches = byName[target.Via];
            if (!allowChains || string.IsNullOrEmpty(target.Via) || !target.IsUsableHop ||
                string.IsNullOrEmpty(target.DisplayName) || byDisplayName[target.DisplayName].Count() != 1 ||
                matches.Count() != 1 || matches.First() is not { Via: null, IsUsableHop: true } upstream)
            {
                skippedChains = true;
                continue;
            }

            upstream.OutboundId ??= Guid.NewGuid().ToString("N");
            target.DetourId = upstream.OutboundId;
            target.OutboundId = Guid.NewGuid().ToString("N");
        }

        var uris = new List<string>();
        foreach (var row in _rows)
        {
            if (row.ShareUri is null || (row.Via is not null && row.DetourId is null)) continue;
            var uri = row.ShareUri;
            if (row.OutboundId is not null)
            {
                // Both native VLESS mappers emit a query followed by an escaped name fragment.
                var metadata = "&outbound=" + row.OutboundId;
                if (row.DetourId is not null) metadata += "&detour=" + row.DetourId;
                uri = uri.Insert(uri.IndexOf('#'), metadata);
            }
            uris.Add(uri);
        }
        return uris;
    }
}
