#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VPNRouter.Core.Services;

public sealed class PolicyHttpClient : IHttpClient, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PoolDnsRefresh = TimeSpan.FromMinutes(5);

    internal const long MaxResponseBytes = 32L * 1024 * 1024;

    private static readonly Lazy<PolicyHttpClient> _shared = new(() => new PolicyHttpClient());

    public static PolicyHttpClient Shared => _shared.Value;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

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

    public PolicyHttpClient(HttpClient httpClient)
    {
        _client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = false;
    }

    public async Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxResponseBytes is <= 0 or > MaxResponseBytes)
            throw new ArgumentOutOfRangeException(
                nameof(HttpRequest.MaxResponseBytes),
                $"MaxResponseBytes must be between 1 and {MaxResponseBytes}.");
        var maxResponseBytes = request.MaxResponseBytes ?? MaxResponseBytes;

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
                    HttpCompletionOption.ResponseHeadersRead,
                    perRequestCts.Token).ConfigureAwait(false);

                var body = await ReadBoundedAsync(
                    httpResponse.Content, maxResponseBytes, perRequestCts.Token)
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
                throw;
            }
            catch (OperationCanceledException) when (
                request.Timeout is not null && attempt < request.RetryCount)
            {
                await DelayBeforeRetryAsync(baseDelay, attempt, ct).ConfigureAwait(false);
                attempt++;
            }
            catch (OperationCanceledException) when (request.Timeout is not null)
            {
                throw new TimeoutException(
                    $"HTTP request timed out after {request.Timeout.Value.TotalMilliseconds:F0} ms.");
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < request.RetryCount)
            {
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

    public async Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var httpRequest = BuildHttpRequestMessage(request);

        var perRequestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.Timeout is { } perRequestTimeout)
            perRequestCts.CancelAfter(perRequestTimeout);

        HttpResponseMessage? response = null;
        try
        {
            response = await _client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                perRequestCts.Token).ConfigureAwait(false);

            var bodyStream = await response.Content
                .ReadAsStreamAsync(perRequestCts.Token)
                .ConfigureAwait(false);

            var headers = CollectHeaders(response);
            var contentLength = response.Content.Headers.ContentLength;
            var statusCode = (int)response.StatusCode;

            var owned = new PolicyStreamingResponse(
                statusCode,
                headers,
                contentLength,
                bodyStream,
                response,
                httpRequest,
                perRequestCts);
            response = null;
            httpRequest = null!;
            perRequestCts = null!;
            return owned;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (request.Timeout is not null)
        {
            throw new TimeoutException(
                $"HTTP streaming request timed out after {request.Timeout.Value.TotalMilliseconds:F0} ms.");
        }
        finally
        {
            if (response is not null)
                response.Dispose();
            httpRequest?.Dispose();
            perRequestCts?.Dispose();
        }
    }

    private sealed class PolicyStreamingResponse : IHttpStreamingResponse
    {
        private readonly HttpResponseMessage _response;
        private readonly HttpRequestMessage _request;
        private readonly CancellationTokenSource _perRequestCts;
        private int _disposed;

        public PolicyStreamingResponse(
            int statusCode,
            IReadOnlyDictionary<string, string> headers,
            long? contentLength,
            Stream body,
            HttpResponseMessage response,
            HttpRequestMessage request,
            CancellationTokenSource perRequestCts)
        {
            StatusCode = statusCode;
            Headers = headers;
            ContentLength = contentLength;
            Body = body;
            _response = response;
            _request = request;
            _perRequestCts = perRequestCts;
        }

        public int StatusCode { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public long? ContentLength { get; }
        public Stream Body { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { await Body.DisposeAsync().ConfigureAwait(false); }
            catch { }

            try { _response.Dispose(); } catch { }
            try { _request.Dispose(); } catch { }
            try { _perRequestCts.Dispose(); } catch { }
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

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new InvalidDataException(
                $"HTTP response body declared as {declared} bytes, exceeding the {maxBytes}-byte limit.");

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > maxBytes)
                throw new InvalidDataException(
                    $"HTTP response body exceeded {maxBytes} bytes (possible decompression bomb or oversized response).");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

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
        var factor = Math.Pow(2, attempt);
        var raw = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
        var jitter = (Random.Shared.NextDouble() * 0.5) - 0.25;
        var withJitter = TimeSpan.FromMilliseconds(raw.TotalMilliseconds * (1 + jitter));
        await Task.Delay(withJitter, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
