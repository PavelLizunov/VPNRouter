using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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

            // Try base64 decode. If it fails, treat response as plain text.
            string decoded;
            try
            {
                var bytes = Convert.FromBase64String(response.Trim());
                decoded = Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Not base64 — might be plain VLESS URIs
                decoded = response;
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

            logger?.Information("[Subscription] Fetched {Count} servers from {Url}", result.Count, url);
        }
        catch (Exception ex)
        {
            logger?.Error(ex, "[Subscription] Fetch failed for {Url}", url);
        }

        return result;
    }
}
