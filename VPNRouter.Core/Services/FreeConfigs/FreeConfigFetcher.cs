using System.Net;
using Serilog;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Downloads raw source text and extracts vless:// lines.
/// Handles plain text AND base64-encoded subscription format.
/// </summary>
public sealed class FreeConfigFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public FreeConfigFetcher(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestHeaders =
            {
                { "User-Agent", "VPNRouter/2.13 (+github.com/PavelLizunov/VPNRouter)" },
            },
        };
    }

    /// <summary>
    /// Fetches one source and returns list of raw vless:// URIs (deduped, trimmed).
    /// Returns empty list on any network error — never throws.
    /// </summary>
    public async Task<List<string>> FetchAsync(FreeConfigSource source, CancellationToken ct = default)
    {
        if (!source.Enabled) return new List<string>();

        try
        {
            using var resp = await _http.GetAsync(source.Url, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warning("FreeConfigFetcher: {src} returned HTTP {code}", source.Name, (int)resp.StatusCode);
                return new List<string>();
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var lines = ExtractVlessLines(body);

            _logger.Information("FreeConfigFetcher: {src} → {count} vless URIs", source.Name, lines.Count);
            return lines;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning("FreeConfigFetcher: {src} failed: {err}", source.Name, ex.Message);
            return new List<string>();
        }
    }

    /// <summary>
    /// Extract vless:// URIs from text. If text is one-line base64 blob, decode first.
    /// </summary>
    internal static List<string> ExtractVlessLines(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new List<string>();

        // Try base64 decode if body looks like one blob (>80% valid base64 chars, no whitespace).
        var maybeDecoded = TryDecodeBase64(body);
        if (!string.IsNullOrEmpty(maybeDecoded))
            body = maybeDecoded;

        var result = new List<string>(capacity: 256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in body.Split('\n', '\r'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length < 20) continue;
            if (!trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }

    private static string? TryDecodeBase64(string body)
    {
        var trimmed = body.Trim();

        // Raw already contains vless:// — not base64.
        if (trimmed.Contains("vless://", StringComparison.OrdinalIgnoreCase)) return null;

        // Heuristic: length divisible by 4 (or close), no weird chars.
        if (trimmed.Length < 60) return null;

        // Restore padding.
        var padded = trimmed.Replace("\n", "").Replace("\r", "").Replace(" ", "");
        var pad = (4 - padded.Length % 4) % 4;
        padded += new string('=', pad);

        try
        {
            var bytes = Convert.FromBase64String(padded);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text.Contains("vless://", StringComparison.OrdinalIgnoreCase))
                return text;
        }
        catch
        {
            // Not valid base64 — leave body as-is.
        }

        return null;
    }
}
