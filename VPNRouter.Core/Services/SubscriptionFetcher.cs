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
        var result = new List<VlessServerEntry>();

        if (string.IsNullOrWhiteSpace(url))
            return result;

        try
        {
            logger?.Information("[Subscription] Fetching {Url}", url);

            var response = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(response))
            {
                logger?.Warning("[Subscription] Empty response from {Url}", url);
                return result;
            }

            // Extract base64 content — supports:
            // 1. JSON wrapper: {"config":"base64..."} (ninitux.com format)
            // 2. Raw base64 (v2rayNG/Streisand format)
            // 3. Plain VLESS URIs (one per line)
            string decoded;
            var trimmed = response.Trim();

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
                if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var entry = VlessUriParser.Parse(line);
                    result.Add(entry);
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

            logger?.Information("[Subscription] Fetched {Count} servers from {Url}", result.Count, url);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Subscription] Fetch failed for {Url}", url);
        }

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

        var servers = await FetchAsync(entry.Url, logger, ct);
        if (ct.IsCancellationRequested) return 0;

        entry.Servers = servers;
        entry.LastServerCount = servers.Count;
        entry.LastRefreshedAt = DateTimeOffset.UtcNow;
        return servers.Count;
    }
}
