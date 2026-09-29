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

public interface IHttpClient
{
    Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken ct = default);

    Task<IHttpStreamingResponse> SendStreamingAsync(HttpRequest request, CancellationToken ct = default);
}

public interface IHttpStreamingResponse : IAsyncDisposable
{
    int StatusCode { get; }

    IReadOnlyDictionary<string, string> Headers { get; }

    long? ContentLength { get; }

    Stream Body { get; }
}

public sealed record HttpRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    byte[]? Body = null,
    string? BodyContentType = null,
    TimeSpan? Timeout = null,
    int RetryCount = 0,
    TimeSpan? RetryBaseDelay = null)
{
    public long? MaxResponseBytes { get; init; }
}

public sealed record HttpResponse(
    int StatusCode,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    TimeSpan Duration);

public static class HttpResponseExtensions
{
    public static string AsString(this HttpResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    public static bool IsSuccess(this HttpResponse response) =>
        response.StatusCode >= 200 && response.StatusCode < 300;

    public static bool IsSuccess(this IHttpStreamingResponse response) =>
        response.StatusCode >= 200 && response.StatusCode < 300;
}
