#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests.Fakes;

public sealed class FakeHttpClient : IHttpClient
{
    private readonly object _lock = new();
    private readonly List<RouteRule> _routes = new();
    private readonly List<StreamRouteRule> _streamRoutes = new();
    private readonly List<HttpRequest> _sentRequests = new();
    private readonly List<HttpRequest> _sentStreamingRequests = new();
    private TimeSpan _defaultDuration = TimeSpan.FromMilliseconds(1);

    public IReadOnlyList<HttpRequest> SentRequests
    {
        get
        {
            lock (_lock) return _sentRequests.ToArray();
        }
    }

    public IReadOnlyList<HttpRequest> SentStreamingRequests
    {
        get
        {
            lock (_lock) return _sentStreamingRequests.ToArray();
        }
    }

    public FakeHttpClient WithDefaultDuration(TimeSpan duration)
    {
        lock (_lock) _defaultDuration = duration;
        return this;
    }

    public FakeHttpClient Setup(string urlPattern, HttpResponse response)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(response);

        lock (_lock)
            _routes.Add(new RouteRule(urlPattern, response, null, null));
        return this;
    }

    public FakeHttpClient Setup(string urlPattern, string body, int statusCode = 200) =>
        Setup(urlPattern, new HttpResponse(
            statusCode,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(body),
            _defaultDuration));

    public FakeHttpClient ThrowOn(string urlPattern, Exception exception)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
            _routes.Add(new RouteRule(urlPattern, null, exception, null));
        return this;
    }

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

    public FakeHttpClient SetupStream(
        string urlPattern,
        byte[] body,
        int statusCode = 200,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(body);

        lock (_lock)
            _streamRoutes.Add(new StreamRouteRule(
                urlPattern,
                body,
                statusCode,
                headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Exception: null));
        return this;
    }

    public FakeHttpClient ThrowOnStream(string urlPattern, Exception exception)
    {
        if (string.IsNullOrEmpty(urlPattern))
            throw new ArgumentException("URL pattern must be non-empty.", nameof(urlPattern));
        ArgumentNullException.ThrowIfNull(exception);

        lock (_lock)
            _streamRoutes.Add(new StreamRouteRule(
                urlPattern,
                Body: null,
                StatusCode: 0,
                Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                exception));
        return this;
    }

    public Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        StreamRouteRule? match;
        lock (_lock)
        {
            _sentStreamingRequests.Add(request);
            match = _streamRoutes.LastOrDefault(r =>
                request.Uri.ToString().Contains(r.Pattern, StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
            throw new InvalidOperationException(
                $"FakeHttpClient: no streaming route registered for {request.Method} {request.Uri}. " +
                "Call SetupStream(...) before exercising the SUT.");

        if (match.Exception is not null)
            return Task.FromException<IHttpStreamingResponse>(match.Exception);

        IHttpStreamingResponse resp = new FakeStreamingResponse(
            match.StatusCode,
            match.Headers,
            match.Body!);
        return Task.FromResult(resp);
    }

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

    private sealed record StreamRouteRule(
        string Pattern,
        byte[]? Body,
        int StatusCode,
        IReadOnlyDictionary<string, string> Headers,
        Exception? Exception);

    private sealed class FakeStreamingResponse : IHttpStreamingResponse
    {
        private readonly MemoryStream _body;
        private int _disposed;

        public FakeStreamingResponse(
            int statusCode,
            IReadOnlyDictionary<string, string> headers,
            byte[] body)
        {
            StatusCode = statusCode;
            Headers = headers;
            ContentLength = body.LongLength;
            _body = new MemoryStream(body, writable: false);
        }

        public int StatusCode { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public long? ContentLength { get; }
        public Stream Body => _body;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;
            _body.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
