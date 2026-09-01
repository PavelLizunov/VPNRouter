using System.Net;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.Diagnostics;

namespace VPNRouter.Core.Services.FreeConfigs;

/// <summary>
/// Downloads raw source text and extracts vless:// lines.
/// Handles plain text AND base64-encoded subscription format.
/// </summary>
public sealed class FreeConfigFetcher
{
    private readonly IHttpClient _http;
    private readonly ILogger _logger;

    internal const int MaxSourceBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(10);

    public FreeConfigFetcher(ILogger logger)
        : this(logger, PolicyHttpClient.Shared) { }

    internal FreeConfigFetcher(ILogger logger, IHttpClient http)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Fetches one source and returns list of raw vless:// URIs (deduped, trimmed).
    /// Returns empty on transport, timeout, HTTP, or size failure; caller cancellation propagates.
    /// </summary>
    public async Task<List<string>> FetchAsync(FreeConfigSource source, CancellationToken ct = default)
    {
        if (!source.Enabled) return new List<string>();

        try
        {
            var request = new HttpRequest(
                HttpMethod.Get,
                new Uri(source.Url, UriKind.Absolute),
                Timeout: PerAttemptTimeout,
                RetryCount: 1)
            {
                MaxResponseBytes = MaxSourceBytes,
            };
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode != (int)HttpStatusCode.OK)
            {
                _logger.Warning("FreeConfigFetcher: {src} HTTP {code}",
                    source.Name, response.StatusCode);
                return new List<string>();
            }

            if (response.Body.Length > MaxSourceBytes)
            {
                _logger.Warning("FreeConfigFetcher: {src} body exceeds {max} bytes",
                    source.Name, MaxSourceBytes);
                return new List<string>();
            }

            var lines = ExtractVlessLines(Encoding.UTF8.GetString(response.Body));
            _logger.Information("FreeConfigFetcher: {src} → {count} vless URIs",
                source.Name, lines.Count);
            return lines;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = ShortMsg(DiagnosticsRedactor.RedactLogText(ex.Message));
            _logger.Warning("FreeConfigFetcher: {src} {type}: {detail}",
                source.Name, ex.GetType().Name, detail);
            return new List<string>();
        }
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
