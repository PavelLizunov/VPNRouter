// Phase 2 — 2D-3 (v3.0 refactor): production <see cref="IHttpClient"/> impl.
//
// Wraps a single process-wide <see cref="HttpClient"/> backed by
// <see cref="SocketsHttpHandler"/> with `PooledConnectionLifetime = 5 min`.
// The 5-min lifetime forces the handler to retire pooled connections so
// long-running VPNRouter sessions (Service mode runs for days) pick up
// DNS rotation. Without it, a stale A-record for `api.github.com` could
// stick around for the whole uptime of the process — exactly the bug
// .NET 8's IHttpClientFactory guidance is meant to prevent.
//
// We deliberately keep the singleton instead of `IHttpClientFactory`:
// VPNRouter has only 1 HTTP origin pattern per service (UpdateChecker
// hits api.github.com, SubscriptionFetcher hits arbitrary user-provided
// URLs, etc.); factory's per-named-client lifecycle would add ceremony
// without measurable benefit. The `PooledConnectionLifetime` knob does
// the same DNS-refresh job for our scale.
//
// Retry policy: opt-in via <see cref="HttpRequest.RetryCount"/> > 0.
// Exponential backoff (base * 2^n) with ±25 % jitter to avoid thundering
// herd. Only retries on 5xx / 429 / network-level <see cref="HttpRequestException"/>.
// 4xx is treated as success-of-transport — caller decides what to do.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// Production <see cref="IHttpClient"/> backed by a single
/// long-lived <see cref="HttpClient"/>. Use the parameterless ctor in
/// production; constructor injection is supported for tests that need a
/// custom handler (e.g. recording proxy).
/// </summary>
public sealed class PolicyHttpClient : IHttpClient, IDisposable
{
    // .NET 8 best practice: SocketsHttpHandler with PooledConnectionLifetime
    // so long-lived process refreshes DNS. Default 30 s timeout is the
    // floor — per-request override beats it down further if needed.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PoolDnsRefresh = TimeSpan.FromMinutes(5);

    private static readonly Lazy<PolicyHttpClient> _shared = new(() => new PolicyHttpClient());

    /// <summary>Process-wide default instance (lazy singleton).</summary>
    public static PolicyHttpClient Shared => _shared.Value;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    /// <summary>
    /// Default ctor — uses a process-shared <see cref="SocketsHttpHandler"/>
    /// with the policy bundle described on <see cref="IHttpClient"/>.
    /// </summary>
    public PolicyHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = PoolDnsRefresh,
            AutomaticDecompression = DecompressionMethods.All,
        };

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = DefaultTimeout,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("VPNRouter");
        _ownsClient = true;
    }

    /// <summary>
    /// Test-friendly ctor: wrap a caller-supplied <see cref="HttpClient"/>.
    /// The supplied client is NOT disposed by <see cref="Dispose"/> — the
    /// caller owns its lifetime.
    /// </summary>
    public PolicyHttpClient(HttpClient httpClient)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = false;
    }

    /// <inheritdoc />
    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempt = 0;
        var baseDelay = request.RetryBaseDelay ?? DefaultRetryBaseDelay;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var httpRequest = BuildHttpRequestMessage(request);
            using var perRequestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (request.Timeout is { } perRequestTimeout)
                perRequestCts.CancelAfter(perRequestTimeout);

            var startedAt = Environment.TickCount64;
            HttpResponseMessage? httpResponse = null;
            try
            {
                httpResponse = await _client.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseContentRead,
                    perRequestCts.Token).ConfigureAwait(false);

                var body = await httpResponse.Content
                    .ReadAsByteArrayAsync(perRequestCts.Token)
                    .ConfigureAwait(false);

                var duration = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
                var statusCode = (int)httpResponse.StatusCode;

                if (ShouldRetry(statusCode, attempt, request.RetryCount))
                {
                    await DelayBeforeRetryAsync(baseDelay, attempt, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                var headers = CollectHeaders(httpResponse);
                return new HttpResponse(statusCode, headers, body, duration);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller-driven cancel — propagate as-is.
                throw;
            }
            catch (OperationCanceledException) when (request.Timeout is not null)
            {
                // Per-request timeout fired. Surface as TimeoutException so
                // callers can disambiguate from generic OperationCanceled.
                throw new TimeoutException(
                    $"HTTP request to {request.Uri} timed out after {request.Timeout.Value.TotalMilliseconds:F0} ms.");
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < request.RetryCount)
            {
                // Network-level transient — retry. After the final allowed
                // attempt the filter `attempt < RetryCount` is false and the
                // exception propagates out naturally.
                _ = ex;
                await DelayBeforeRetryAsync(baseDelay, attempt, ct).ConfigureAwait(false);
                attempt++;
            }
            finally
            {
                httpResponse?.Dispose();
            }
        }
    }

    private static HttpRequestMessage BuildHttpRequestMessage(HttpRequest request)
    {
        var msg = new HttpRequestMessage(request.Method, request.Uri);

        if (request.Body is { Length: > 0 })
        {
            var content = new ByteArrayContent(request.Body);
            if (!string.IsNullOrEmpty(request.BodyContentType))
            {
                if (MediaTypeHeaderValue.TryParse(request.BodyContentType, out var parsed))
                    content.Headers.ContentType = parsed;
            }
            msg.Content = content;
        }

        if (request.Headers is not null)
        {
            foreach (var kvp in request.Headers)
            {
                // Try header set; fall back to content header for ones we
                // can't add to the request (e.g. "Content-Type" when no
                // body), silently ignored — call site controls the dict.
                if (!msg.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value))
                {
                    msg.Content?.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }
        }

        return msg;
    }

    private static IReadOnlyDictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyHeaders(response.Headers, dict);
        if (response.Content is not null)
            CopyHeaders(response.Content.Headers, dict);
        return dict;
    }

    private static void CopyHeaders(HttpHeaders source, Dictionary<string, string> sink)
    {
        foreach (var (name, values) in source)
            sink[name] = string.Join(", ", values);
    }

    /// <summary>
    /// Status codes that warrant a retry: 5xx and 429. 4xx (excluding 429)
    /// are caller-level issues and not retried.
    /// </summary>
    private static bool ShouldRetry(int statusCode, int attempt, int retryCount)
    {
        if (attempt >= retryCount) return false;
        if (statusCode == 429) return true;
        return statusCode >= 500 && statusCode < 600;
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException
        || ex is IOException
        || ex is SocketException;

    private static async Task DelayBeforeRetryAsync(TimeSpan baseDelay, int attempt, CancellationToken ct)
    {
        // Exponential backoff with ±25 % jitter so concurrent callers don't
        // all retry at exactly the same wall-clock instant (thundering herd).
        var factor = Math.Pow(2, attempt);
        var raw = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
        var jitter = (Random.Shared.NextDouble() * 0.5) - 0.25; // [-0.25, +0.25)
        var withJitter = TimeSpan.FromMilliseconds(raw.TotalMilliseconds * (1 + jitter));
        await Task.Delay(withJitter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the underlying <see cref="HttpClient"/> only if this
    /// instance owns it (i.e. created via the parameterless ctor). When
    /// the client was injected, the caller retains ownership.
    /// </summary>
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
