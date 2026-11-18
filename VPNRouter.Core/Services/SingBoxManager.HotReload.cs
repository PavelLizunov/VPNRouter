using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    private static readonly IHttpClient LocalControllerHttp =
        new PolicyHttpClient(new HttpClient(ClashSingBoxApi.CreateControllerHandler()));
    private volatile HttpRequest? _clashConfigsRequest;

    internal void SetClashController((Uri Endpoint, string? Secret) controller) =>
        _clashConfigsRequest = ConfigsRequest(controller);

    private HttpRequest CurrentConfigsRequest => _clashConfigsRequest ??
        ConfigsRequest((ClashSingBoxApi.LocalController(_settings.ClashApi), _settings.ClashApiSecret));

    private static HttpRequest ConfigsRequest((Uri Endpoint, string? Secret) controller) =>
        new(HttpMethod.Get, new Uri(controller.Endpoint, "/configs"),
            Headers: string.IsNullOrEmpty(controller.Secret) ? null :
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {controller.Secret}" },
            Timeout: TimeSpan.FromSeconds(3));

    private async Task<HttpResponse> SendClashRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (ReferenceEquals(_http, LocalControllerHttp))
            return await _http.SendAsync(request, ct).ConfigureAwait(false);

        var addresses = await ClashSingBoxApi.ResolveControlTargetsAsync(request.Uri, ct).ConfigureAwait(false);
        var directUri = new UriBuilder(request.Uri) { Host = addresses[0].ToString() }.Uri;
        return await _http.SendAsync(request with { Uri = directUri }, ct).ConfigureAwait(false);
    }

    public string WriteConfigToDisk(string configJson)
    {
        lock (_lifecycleGate)
        {
            if (_businessPolicy != null)
                throw new InvalidOperationException("Business configuration requires its captured policy.");
            _currentConfigPath = WriteJsonToDisk(configJson);
            return _currentConfigPath;
        }
    }

    private bool TryHotReload()
    {
        if (_handle == null || _handle.HasExited)
        {
            _logger.Debug("[SingBoxManager] Hot-reload skipped — sing-box process not alive");
            return false;
        }

        try
        {
            var configs = CurrentConfigsRequest;
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = SendClashRequestAsync(configs with
            {
                Method = HttpMethod.Put,
                Uri = new Uri(configs.Uri, "?force=true"),
                Body = bodyBytes,
                BodyContentType = "application/json"
            }, cts.Token).GetAwaiter().GetResult();

            if (response.IsSuccess())
            {
                _logger.Information("[SingBoxManager] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    response.StatusCode);
                return true;
            }

            _logger.Warning("[SingBoxManager] Hot-reload HTTP {Code}: {Body}",
                response.StatusCode, response.AsString());
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("[SingBoxManager] Hot-reload timed out after 3s");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[SingBoxManager] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
        }
    }

    private bool IsClashApiAlive()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = SendClashRequestAsync(CurrentConfigsRequest, cts.Token).GetAwaiter().GetResult();
            return response.IsSuccess();
        }
        catch { return false; }
    }
}
