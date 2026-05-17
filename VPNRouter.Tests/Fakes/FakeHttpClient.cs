// Phase 2 — 2D-3 (v3.0 refactor): test double for VPNRouter.Core.Services.IHttpClient.
//
// Lets test classes stub HTTP responses by URL pattern, record every
// request the SUT made, and inject failure modes (transient + permanent).
// Thread-safe enough for parallel xUnit tests: registration is expected
// to happen before the SUT starts firing requests, but capture and lookup
// both lock the internal state.
//
// Used by IHttpClientContractTests (this same project) and (Phase 2G) by
// SubscriptionFetcherTests / FreeConfigPoolFetcherTests / etc. once those
// services migrate to the abstraction.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

/// <summary>
/// In-memory test double for <see cref="IHttpClient"/>. Register canned
/// responses via <see cref="Setup(string, HttpResponse)"/>; inject failures
/// via <see cref="ThrowOn(string, Exception)"/>. Inspect <see cref="SentRequests"/>
/// after the SUT call to assert call shape.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>URL matching is exact-host-plus-prefix (substring on the full
///         URI string) so callers can register
///         <c>"https://api.github.com/repos/foo/releases"</c> and capture
///         any querystring suffix transparently.</item>
///   <item>Last <see cref="Setup(string, HttpResponse)"/> registration for
///         the same pattern wins.</item>
///   <item>If no rule matches, the call throws <see cref="InvalidOperationException"/>
///         with the unmatched URL so tests fail loudly rather than silently
///         hitting the real network.</item>
/// </list>
/// </remarks>
public sealed class FakeHttpClient : IHttpClient
{
    private readonly object _lock = new();
    private readonly List<RouteRule> _routes = new();
    private readonly List<HttpRequest> _sentRequests = new();
    private TimeSpan _defaultDuration = TimeSpan.FromMilliseconds(1);

    /// <summary>Snapshot of all requests the SUT issued, in call order.</summary>
    public IReadOnlyList<HttpRequest> SentRequests
    {
        get
        {
            lock (_lock) return _sentRequests.ToArray();
        }
    }

    /// <summary>
    /// Override the synthetic <see cref="HttpResponse.Duration"/> on canned
    /// responses (default 1 ms). Tests that assert on timing can set this
    /// to a known value.
    /// </summary>
    public FakeHttpClient WithDefaultDuration(TimeSpan duration)
    {
        lock (_lock) _defaultDuration = duration;
        return this;
    }

    /// <summary>
    /// Register a canned response for any request whose URI string contains
    /// <paramref name="urlPattern"/>.
    /// </summary>
    public FakeHttpClient Setup(string urlPattern, HttpResponse response)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(response);

        lock (_lock)
            _routes.Add(new RouteRule(urlPattern, response, null, null));
        return this;
    }

    /// <summary>
    /// Shorthand: register a 200-OK response with the supplied UTF-8 body.
    /// </summary>
    public FakeHttpClient Setup(string urlPattern, string body, int statusCode = 200) =>
        Setup(urlPattern, new HttpResponse(
            statusCode,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(body),
            _defaultDuration));

    /// <summary>
    /// Register a fault: matching requests throw <paramref name="exception"/>
    /// instead of returning a response.
    /// </summary>
    public FakeHttpClient ThrowOn(string urlPattern, Exception exception)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
            _routes.Add(new RouteRule(urlPattern, null, exception, null));
        return this;
    }

    /// <summary>
    /// Register a sequence: each matching request consumes the next
    /// response/exception from the queue. Allows tests to assert that
    /// retry policy re-issues the call (first attempt fails, second
    /// succeeds, etc.).
    /// </summary>
    public FakeHttpClient SetupSequence(string urlPattern, params object[] sequence)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Length == 0)
            throw new ArgumentException("Sequence must contain at least one item.", nameof(sequence));

        var queue = new Queue<object>(sequence);
        lock (_lock)
            _routes.Add(new RouteRule(urlPattern, null, null, queue));
        return this;
    }

    /// <inheritdoc />
    public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        RouteRule? match;
        object? sequenceItem = null;
        lock (_lock)
        {
            _sentRequests.Add(request);
            match = _routes.LastOrDefault(r => request.Uri.ToString().Contains(r.Pattern, StringComparison.OrdinalIgnoreCase));
            if (match?.Sequence is { } queue)
            {
                if (queue.Count == 0)
                    throw new InvalidOperationException(
                        $"FakeHttpClient: sequence for '{match.Pattern}' exhausted. " +
                        "Add more items to SetupSequence(...).");
                sequenceItem = queue.Dequeue();
            }
        }

        if (match is null)
            throw new InvalidOperationException(
                $"FakeHttpClient: no route registered for {request.Method} {request.Uri}. " +
                "Call Setup(...) before exercising the SUT.");

        if (sequenceItem is not null)
        {
            return sequenceItem switch
            {
                HttpResponse response => Task.FromResult(response),
                Exception exception => Task.FromException<HttpResponse>(exception),
                _ => throw new InvalidOperationException(
                    $"FakeHttpClient: sequence item type '{sequenceItem.GetType().Name}' not supported. " +
                    "Use HttpResponse or Exception only."),
            };
        }

        if (match.Exception is not null)
            return Task.FromException<HttpResponse>(match.Exception);

        return Task.FromResult(match.Response!);
    }

    private sealed record RouteRule(
        string Pattern,
        HttpResponse? Response,
        Exception? Exception,
        Queue<object>? Sequence);
}
