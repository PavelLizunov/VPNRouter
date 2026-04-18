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

    private const int MaxAttempts = 2;
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    public FreeConfigFetcher(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            // No global timeout — we use per-request linked CTS below (so retries are real).
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders =
            {
                { "User-Agent", "VPNRouter/2.13 (+github.com/PavelLizunov/VPNRouter)" },
            },
        };
    }

    /// <summary>
    /// Fetches one source and returns list of raw vless:// URIs (deduped, trimmed).
    /// Returns empty list on any network error — never throws.
    /// Retries once on network failure (total 2 attempts × 10s = max ~20s per source).
    /// </summary>
    public async Task<List<string>> FetchAsync(FreeConfigSource source, CancellationToken ct = default)
    {
        if (!source.Enabled) return new List<string>();

        string? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var perAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAttemptCts.CancelAfter(PerAttemptTimeout);

            try
            {
                _logger.Debug("FreeConfigFetcher: {src} attempt {n}/{max}", source.Name, attempt, MaxAttempts);

                using var resp = await _http.GetAsync(source.Url, HttpCompletionOption.ResponseContentRead, perAttemptCts.Token);
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Warning("FreeConfigFetcher: {src} HTTP {code} (attempt {n})", source.Name, (int)resp.StatusCode, attempt);
                    lastError = $"HTTP {(int)resp.StatusCode}";
                    if (attempt < MaxAttempts) continue;
                    return new List<string>();
                }

                var body = await resp.Content.ReadAsStringAsync(perAttemptCts.Token);
                var lines = ExtractVlessLines(body);

                _logger.Information("FreeConfigFetcher: {src} → {count} vless URIs (attempt {n})", source.Name, lines.Count, attempt);
                return lines;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // User cancellation — propagate.
                throw;
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout — retry.
                lastError = $"timeout after {PerAttemptTimeout.TotalSeconds}s";
                _logger.Warning("FreeConfigFetcher: {src} {err} (attempt {n}/{max})", source.Name, lastError, attempt, MaxAttempts);
            }
            catch (Exception ex)
            {
                lastError = $"{ex.GetType().Name}: {ShortMsg(ex.Message)}";
                _logger.Warning("FreeConfigFetcher: {src} {err} (attempt {n}/{max})", source.Name, lastError, attempt, MaxAttempts);
            }
        }

        _logger.Warning("FreeConfigFetcher: {src} GAVE UP after {n} attempts — last: {err}",
            source.Name, MaxAttempts, lastError);
        return new List<string>();
    }

    private static string ShortMsg(string s) => s.Length > 120 ? s[..120] + "…" : s;

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
