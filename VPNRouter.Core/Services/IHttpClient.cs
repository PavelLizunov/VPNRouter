// Phase 2 — 2D-3 (v3.0 refactor): single HTTP seam for all Core services.
//
// Audit D (plans/v3.0-architecture-roadmap.md §4): 6 `static readonly
// HttpClient` fields scattered across UpdateChecker, SubscriptionFetcher,
// VlessDeepVerifier, FreeConfigPoolFetcher, ZapretUpdater, etc. Each
// carries its own connection pool + DNS cache, no shared retry policy, no
// mocking seam → Audit E flagged these services as "untested HTTP path".
//
// Solution: thin `IHttpClient` interface + `PolicyHttpClient` concrete
// using a single shared `HttpClient` backed by `SocketsHttpHandler` with
// `PooledConnectionLifetime = 5 min` (DNS refresh per .NET 8 guidance) +
// `FakeHttpClient` for tests. POC migration target: `UpdateChecker.cs`.
// Phase 2G converts the other 5+ call sites.
//
// Brief: plans/phase2-2D-ihttpclient-2026-05-17.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

/// <summary>
/// Abstraction over <see cref="HttpClient"/> with a policy bundle:
/// <list type="bullet">
///   <item>30-second default timeout (overridable per request via <see cref="HttpRequest.Timeout"/>).</item>
///   <item>Shared connection pool with <c>PooledConnectionLifetime = 5 min</c>
///         so long-lived processes pick up DNS changes (per .NET 8 best practice).</item>
///   <item>Opt-in retry-on-transient-failure (off by default; callers set
///         <see cref="HttpRequest.RetryCount"/> &gt; 0 to enable).</item>
/// </list>
///
/// <para>Concrete production impl: <see cref="PolicyHttpClient"/>. Tests use
/// the in-repo <c>FakeHttpClient</c> with route matchers + request capture.</para>
/// </summary>
/// <remarks>
/// Body type is <see cref="byte"/>[] not <see cref="System.IO.Stream"/> for
/// testability simplicity — VPNRouter never downloads &gt;50 MB in one
/// shot (full update bundle is ~25 MB).
/// </remarks>
public interface IHttpClient
{
    /// <summary>
    /// Issue an HTTP request and await the full response body.
    /// </summary>
    /// <param name="request">Request envelope: method, URI, optional headers, body, timeout, retry policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Full response envelope including buffered body bytes.</returns>
    /// <exception cref="OperationCanceledException">Cancelled via <paramref name="ct"/> or timeout elapsed.</exception>
    /// <exception cref="HttpRequestException">Transport-level failure that survived all retries.</exception>
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default);
}

/// <summary>
/// HTTP request envelope. Immutable record so tests and call sites can
/// reuse / fan out without mutation footguns.
/// </summary>
/// <param name="Method">HTTP verb (GET / POST / etc.).</param>
/// <param name="Uri">Target URI; absolute.</param>
/// <param name="Headers">Per-request extra headers; <see cref="PolicyHttpClient"/> also adds the bundled <c>User-Agent</c>.</param>
/// <param name="Body">Optional request body. Pair with <paramref name="BodyContentType"/>.</param>
/// <param name="BodyContentType">MIME type of <paramref name="Body"/>; required when body is non-null.</param>
/// <param name="Timeout">Per-request timeout override; <c>null</c> = use the client default (30 s).</param>
/// <param name="RetryCount">Number of additional attempts on transient failure (5xx, network errors); <c>0</c> = no retry.</param>
/// <param name="RetryBaseDelay">Exponential backoff base; <c>null</c> = 200 ms. Effective delay per attempt: <c>base * 2^(attempt-1)</c> with ±25 % jitter.</param>
public sealed record HttpRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Body = null,
    string? BodyContentType = null,
    TimeSpan? Timeout = null,
    int RetryCount = 0,
    TimeSpan? RetryBaseDelay = null);

/// <summary>
/// HTTP response envelope with buffered body.
/// </summary>
/// <param name="StatusCode">HTTP status code (200, 404, 500, ...).</param>
/// <param name="Headers">Combined response + content headers, key-folded by the underlying client. Multi-value headers are joined with commas.</param>
/// <param name="Body">Body bytes (already buffered + decompressed).</param>
/// <param name="Duration">Wall-clock time from <see cref="IHttpClient.SendAsync"/> dispatch to response complete.</param>
public sealed record HttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    TimeSpan Duration);

/// <summary>
/// Convenience helpers for <see cref="HttpResponse"/>. Kept as a static
/// extension class so call sites read like a fluent pipeline:
/// <c>var json = (await http.SendAsync(req)).AsString();</c>.
/// </summary>
public static class HttpResponseExtensions
{
    /// <summary>Decode body as UTF-8 string.</summary>
    public static string AsString(this HttpResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    /// <summary>
    /// Decode body as JSON of <typeparamref name="T"/> via
    /// <see cref="System.Text.Json"/>. Throws if the body is empty or
    /// deserialises to <c>null</c>.
    /// </summary>
    public static T AsJson<T>(this HttpResponse response, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var value = JsonSerializer.Deserialize<T>(response.Body, options);
        if (value is null)
            throw new InvalidOperationException(
                $"HTTP body deserialized to null for type {typeof(T).Name} (body length: {response.Body.Length})");
        return value;
    }

    /// <summary>2xx status code check.</summary>
    public static bool IsSuccess(this HttpResponse response) =>
        response.StatusCode >= 200 && response.StatusCode < 300;
}
