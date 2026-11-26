using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    private static readonly IHttpClient LocalControllerHttp =
        new PolicyHttpClient(new HttpClient(ClashSingBoxApi.CreateControllerHandler()));
    private volatile HttpRequest? _clashControllerRequest;

    // Verification is bounded so the lifecycle lock is never held for long.
    private static readonly TimeSpan TagVerificationBudget = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan TagVerificationPollDelay = TimeSpan.FromMilliseconds(150);

    internal void SetClashController((Uri Endpoint, string? Secret) controller) =>
        _clashControllerRequest = ControllerRequest(controller, "/configs");

    private HttpRequest CurrentControllerRequest => _clashControllerRequest ??
        ControllerRequest((ClashSingBoxApi.LocalController(_settings.ClashApi), _settings.ClashApiSecret), "/configs");

    private static HttpRequest ControllerRequest((Uri Endpoint, string? Secret) controller, string path) =>
        new(HttpMethod.Get, new Uri(controller.Endpoint, path),
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
        _currentConfigPath = WriteJsonToDisk(configJson);
        return _currentConfigPath;
    }

    /// <summary>
    /// Confirms that the restarted controller serves every proxy outbound tag the
    /// applied document declares. A legacy <c>PUT /configs</c> 204 is never evidence:
    /// only a real process restart followed by this probe counts.
    /// </summary>
    internal bool VerifyAppliedConfigTags(string configJson, bool logFailure = true)
    {
        HashSet<string> expected;
        try
        {
            expected = ReadProxyTags(configJson);
        }
        catch (JsonException)
        {
            _logger.Warning("[SingBoxManager] Restart verification skipped — the applied document is not valid JSON");
            return false;
        }

        if (expected.Count == 0)
            return true;

        // The applied document may declare its own loopback controller (custom
        // configs and service replacements); verify the controller it names.
        HttpRequest controllerRequest;
        try
        {
            controllerRequest = ControllerRequest(ClashSingBoxApi.ReadLocalController(configJson), "/configs");
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or
            JsonException or InvalidDataException)
        {
            controllerRequest = CurrentControllerRequest;
        }
        var proxiesUri = new Uri(controllerRequest.Uri, "/proxies");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TagVerificationBudget)
        {
            var remaining = TagVerificationBudget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            try
            {
                using var cts = new CancellationTokenSource(
                    remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
                var response = SendClashRequestAsync(
                    controllerRequest with { Uri = proxiesUri }, cts.Token).GetAwaiter().GetResult();
                if (response.IsSuccess())
                {
                    if (ServesTags(response.AsString(), expected))
                        return true;
                    _logger.Debug("[SingBoxManager] Controller is up but the applied outbound tags are not served yet");
                }
            }
            catch (OperationCanceledException)
            {
                // Keep polling until the bounded budget expires.
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[SingBoxManager] Controller tag probe unavailable ({Msg})", ex.Message);
            }

            Thread.Sleep(TagVerificationPollDelay);
        }

        if (logFailure)
        {
            _logger.Warning(
                "[SingBoxManager] Restart verification failed — controller did not serve the applied outbound tags within {Sec:F1}s",
                TagVerificationBudget.TotalSeconds);
        }
        else
        {
            _logger.Debug(
                "[SingBoxManager] Deferred controller confirmation is still incomplete after {Sec:F1}s",
                TagVerificationBudget.TotalSeconds);
        }
        return false;
    }

    private static bool ServesTags(string body, IReadOnlyCollection<string> expected)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("proxies", out var proxies) ||
            proxies.ValueKind != JsonValueKind.Object)
            return false;

        var served = proxies.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        return expected.All(served.Contains);
    }

    private static HashSet<string> ReadProxyTags(string configJson)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(configJson);
        var root = document.RootElement;
        AddProxyTags(root, "outbounds", tags);
        AddProxyTags(root, "endpoints", tags);
        return tags;
    }

    private static void AddProxyTags(JsonElement root, string arrayName, HashSet<string> tags)
    {
        if (!root.TryGetProperty(arrayName, out var outbounds) ||
            outbounds.ValueKind != JsonValueKind.Array)
            return;

        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (outbound.ValueKind != JsonValueKind.Object)
                continue;
            var type = outbound.TryGetProperty("type", out var typeNode) && typeNode.ValueKind == JsonValueKind.String
                ? typeNode.GetString()
                : null;
            if (string.IsNullOrEmpty(type) || !IsProxyOutboundType(type))
                continue;
            if (outbound.TryGetProperty("tag", out var tagNode) && tagNode.ValueKind == JsonValueKind.String &&
                tagNode.GetString() is { Length: > 0 } tag)
                tags.Add(tag);
        }
    }

    private static bool IsProxyOutboundType(string type) => type.ToLowerInvariant() is
        "vless" or "vmess" or "hysteria2" or "tuic" or "shadowsocks" or "naive" or "ssh" or
        "turnrelay" or "trojan" or "socks" or "http" or "wireguard";

    private bool IsClashApiAlive()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = SendClashRequestAsync(CurrentControllerRequest, cts.Token).GetAwaiter().GetResult();
            return response.IsSuccess();
        }
        catch { return false; }
    }
}
