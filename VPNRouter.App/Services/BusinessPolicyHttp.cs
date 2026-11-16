using System.Net;
using System.Net.Http.Headers;
using VPNRouter.Core.Services;

namespace VPNRouter.App.Services;

internal sealed class BusinessPolicyHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _timeProvider;

    internal BusinessPolicyHttp(HttpClient? http = null, TimeSpan? deadline = null, TimeProvider? timeProvider = null)
    {
        _ownsClient = http == null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _deadline = deadline ?? TimeSpan.FromSeconds(10);
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(deadline));
    }

    internal async Task<byte[]> FetchAsync(BusinessPolicyConfiguration configuration, CancellationToken ct)
        => await FetchCoreAsync(configuration, configuration.Endpoint, false, ct).ConfigureAwait(false)
            ?? throw Failure();

    internal Task<byte[]?> FetchPhoneAsync(BusinessPolicyConfiguration configuration, CancellationToken ct)
    {
        var endpoint = new UriBuilder(configuration.Endpoint)
        {
            Path = configuration.Endpoint.AbsolutePath.TrimEnd('/') + "/telephony"
        }.Uri;
        return FetchCoreAsync(configuration, endpoint, true, ct);
    }

    private async Task<byte[]?> FetchCoreAsync(BusinessPolicyConfiguration configuration,
        Uri endpoint, bool allowMissing, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(_deadline, _timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ReadToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("VPNRouterBusiness");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != endpoint) throw Failure();
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode != HttpStatusCode.OK ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength > BusinessPolicy.MaximumResponseBytes)
                throw Failure();

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = (int)Math.Min(chunk.Length, BusinessPolicy.MaximumResponseBytes - buffer.Length + 1);
                var read = await input.ReadAsync(chunk.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > BusinessPolicy.MaximumResponseBytes) throw Failure();
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0) throw Failure();
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            throw Failure();
        }
    }

    private static IOException Failure() => new("Business policy response is unavailable or invalid.");

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
