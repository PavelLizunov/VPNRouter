using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    public string WriteConfigToDisk(string configJson)
    {
        _currentConfigPath = WriteJsonToDisk(configJson);
        return _currentConfigPath;
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
            var url = $"http://{_settings.ClashApi}/configs?force=true";
            var body = $"{{\"path\":\"{_currentConfigPath.Replace("\\", "\\\\")}\"}}";
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Put, new Uri(url),
                Headers: ClashAuthHeaders(),
                Body: bodyBytes,
                BodyContentType: "application/json",
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();

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
            var response = _http.SendAsync(new HttpRequest(
                HttpMethod.Get, new Uri($"http://{_settings.ClashApi}/configs"),
                Headers: ClashAuthHeaders(),
                Timeout: TimeSpan.FromSeconds(3)), cts.Token).GetAwaiter().GetResult();
            return response.IsSuccess();
        }
        catch { return false; }
    }

    private Dictionary<string, string>? ClashAuthHeaders()
        => string.IsNullOrEmpty(_settings.ClashApiSecret)
            ? null
            : new Dictionary<string, string> { ["Authorization"] = $"Bearer {_settings.ClashApiSecret}" };
}
