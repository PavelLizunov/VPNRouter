using System.Net.Http.Headers;
using System.Text;

namespace VPNRouter.App.Services;

internal sealed class BusinessClientHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    internal BusinessClientHttp(HttpClient? http = null)
    {
        _ownsClient = http == null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal async Task<string> FetchAsync(Uri source, CancellationToken ct)
    {
        RequireHttps(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd("VPNRouterBusiness");
        request.Headers.Add("X-VPNRouter-Capabilities", "detour-v1");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("Subscription not received.");
        const int limit = 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Response too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new IOException("Response too large.");
            buffer.Write(chunk, 0, read);
        }
        var body = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
        BusinessClientConfiguration.ValidateResponse(body);
        return body;
    }

    internal async Task SendAsync(Uri endpoint, string token, string path, CancellationToken ct)
    {
        RequireHttps(endpoint);
        BusinessPrivateFiles.RefuseLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Path.GetExtension(path) != ".zip" || stream.Length is <= 0 or > 24 * 1024 * 1024)
            throw new IOException("Invalid report size.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-VPNRouter-Report-Format", "vpnrouter-business-support/1");
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = stream.Length;
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IOException("Report not acknowledged.");
    }

    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Business service must use HTTPS.");
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
