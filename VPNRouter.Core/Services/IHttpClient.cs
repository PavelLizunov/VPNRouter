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
using System.IO;
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

    /// <summary>
    /// Streaming variant of <see cref="SendAsync"/> for large-file downloads
    /// (ZIP archives, binaries, &gt;50 MB payloads). Returns the response
    /// stream + status code + headers WITHOUT buffering the full body.
    ///
    /// <para>Caller MUST dispose the returned
    /// <see cref="IHttpStreamingResponse"/> before disposing the client
    /// (preferably via <c>await using</c>). Disposal aborts the underlying
    /// HTTP connection if the body has not been fully read, so no half-read
    /// kernel buffers leak.</para>
    ///
    /// <para>Cancellation: the caller's <paramref name="ct"/> is linked to
    /// the underlying request. Cancelling at any point — including
    /// mid-body-read — aborts the stream and frees the connection.</para>
    ///
    /// <para>Unlike <see cref="SendAsync"/>, this method does NOT loop on
    /// retry: streaming retries require re-issuing the request from byte 0
    /// which the caller has to coordinate with file-on-disk state. Use
    /// <see cref="HttpRequest.Timeout"/> for a hard upper bound on the
    /// response-headers phase only.</para>
    /// </summary>
    /// <param name="request">Request envelope: method, URI, optional headers, body, timeout.
    ///   <c>RetryCount</c> on the envelope is IGNORED — streaming responses can't
    ///   be transparently retried (mid-stream restart would corrupt the file).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Streaming response wrapper. Dispose to release the underlying connection.</returns>
    /// <exception cref="OperationCanceledException">Cancelled via <paramref name="ct"/> before headers arrived.</exception>
    /// <exception cref="TimeoutException">Per-request timeout fired before headers arrived.</exception>
    /// <exception cref="HttpRequestException">Transport-level failure during the headers phase.</exception>
    Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default);
}

/// <summary>
/// Progressive HTTP response: status + headers are buffered, the body is a
/// live <see cref="Stream"/> that reads from the network on demand.
///
/// <para>Returned by <see cref="IHttpClient.SendStreamingAsync"/>. Dispose
/// chains down: closing this wrapper closes the body stream, the
/// <see cref="HttpResponseMessage"/>, and (in concrete impls) any
/// per-request scratch state. The owning <c>IHttpClient</c> is NOT
/// disposed — its lifetime is process-scoped.</para>
///
/// <para>Reading <see cref="Body"/> after dispose throws
/// <see cref="ObjectDisposedException"/>; copying <see cref="Body"/> to a
/// destination stream and then disposing this wrapper is the canonical
/// usage pattern.</para>
/// </summary>
public interface IHttpStreamingResponse : IAsyncDisposable
{
    /// <summary>HTTP status code (200, 404, 500, ...).</summary>
    int StatusCode { get; }

    /// <summary>
    /// Combined response + content headers, key-folded by the underlying
    /// client. Multi-value headers are joined with commas. Buffered upfront
    /// so reading them does not consume the body stream.
    /// </summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Body length declared by the server (Content-Length), or <c>null</c>
    /// if the server used chunked encoding / no Content-Length header.
    /// </summary>
    long? ContentLength { get; }

    /// <summary>
    /// Live response body. Reading consumes bytes from the network.
    /// Disposal of the enclosing <see cref="IHttpStreamingResponse"/>
    /// closes this stream.
    /// </summary>
    Stream Body { get; }
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

    // Phase 7 Wave 34 (2026-05-19): removed `AsJson<T>(HttpResponse,
    // JsonSerializerOptions?)` extension. It was added by Phase 2D as a
    // convenience but never actually called — every IHttpClient consumer
    // (GitHubReleaseSource, SideloadSource, SubscriptionFetcher,
    // FreeConfigPoolFetcher, etc.) deserializes directly on
    // `response.Body` or `response.AsString()` with a context-bound
    // JsonTypeInfo<T>. Keeping a generic `Deserialize<T>(byte[],
    // JsonSerializerOptions)` shape with zero callers just to satisfy
    // a possible-future use case would carry an unsuppressable IL2026/
    // IL3050 AOT warning forever. If a future caller wants this
    // convenience, they should add a typed overload (e.g.
    // `AsJson(this HttpResponse, JsonTypeInfo<T>)`) that's AOT-clean.

    /// <summary>2xx status code check.</summary>
    public static bool IsSuccess(this HttpResponse response) =>
        response.StatusCode >= 200 && response.StatusCode < 300;

    /// <summary>2xx status code check for streaming responses.</summary>
    public static bool IsSuccess(this IHttpStreamingResponse response) =>
        response.StatusCode >= 200 && response.StatusCode < 300;
}
