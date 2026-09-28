#nullable enable
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Serilog;
using VPNRouter.Core.Json;

namespace VPNRouter.Core.Services;

public sealed class ClashSingBoxApi : ISingBoxApi, IDisposable
{
    private static readonly TimeSpan ReloadDeadline = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PingDeadline = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public ClashSingBoxApi(
        HttpClient? httpClient = null,
        string baseUrl = "http://127.0.0.1:9090",
        ILogger? logger = null,
        string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Clash API base URL cannot be empty.", nameof(baseUrl));

        var normalized = baseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Clash API base URL must be an absolute http(s) URL; got '{baseUrl}'.",
                nameof(baseUrl));
        }

        if (!IsLoopbackHost(uri.Host))
        {
            throw new ArgumentException(
                $"Clash API base URL must point at a loopback host; '{uri.Host}' is not loopback. " +
                "Remote Clash control is a security risk — sing-box's Clash API listens on 127.0.0.1 by convention.",
                nameof(baseUrl));
        }

        _baseUrl = normalized;
        _logger = logger ?? Log.Logger;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttpClient = true;
        }

        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> ReloadConfigAsync(string configPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            _logger.Warning("[ClashSingBoxApi] ReloadConfigAsync called with empty configPath");
            return false;
        }

        var body = JsonSerializer.Serialize(
            new ClashSetConfigDto(configPath), Json.AppJsonContext.Default.ClashSetConfigDto);
        var url = $"{_baseUrl}/configs?force=true";

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(ReloadDeadline);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PutAsync(url, content, deadlineCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.Information(
                    "[ClashSingBoxApi] Hot-reload succeeded (HTTP {Code}) — TUN stays up",
                    (int)response.StatusCode);
                return true;
            }

            var respBody = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            _logger.Warning(
                "[ClashSingBoxApi] Hot-reload HTTP {Code}: {Body}",
                (int)response.StatusCode, respBody);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.Debug("[ClashSingBoxApi] Hot-reload timed out after {Sec}s", ReloadDeadline.TotalSeconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] Hot-reload unavailable ({Msg})", ex.Message);
            return false;
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/version", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            var doc = await JsonSerializer.DeserializeAsync(
                stream, Json.AppJsonContext.Default.VersionDto, deadlineCts.Token).ConfigureAwait(false);

            return string.IsNullOrEmpty(doc?.Version) ? null : doc.Version;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetVersionAsync failed");
            return null;
        }
    }

    public async Task<ConnectionsSnapshot> GetConnectionsAsync(CancellationToken ct = default)
    {
        var failureSnapshot = new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow) { IsValid = false };

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/connections", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return failureSnapshot;

            var bytes = await response.Content.ReadAsByteArrayAsync(deadlineCts.Token).ConfigureAwait(false);
            if (!ParseConnectionsSummary(bytes, out var down, out var up, out var count))
                return failureSnapshot;

            return new ConnectionsSnapshot(
                ActiveCount: count,
                TotalUploadBytes: up,
                TotalDownloadBytes: down,
                CapturedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return failureSnapshot;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetConnectionsAsync failed");
            return failureSnapshot;
        }
    }

    public async Task<bool> SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            _logger.Warning("[ClashSingBoxApi] SelectProxyAsync called with empty group name");
            return false;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.Warning("[ClashSingBoxApi] SelectProxyAsync called with empty proxy name");
            return false;
        }

        var encodedGroup = Uri.EscapeDataString(group);
        var url = $"{_baseUrl}/proxies/{encodedGroup}";
        var body = JsonSerializer.Serialize(
            new ClashSelectProxyDto(name), Json.AppJsonContext.Default.ClashSelectProxyDto);

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(ReloadDeadline);

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PutAsync(url, content, deadlineCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.Information(
                    "[ClashSingBoxApi] Proxy switch succeeded: {Group} → {Name} (HTTP {Code})",
                    group, name, (int)response.StatusCode);
                return true;
            }

            var respBody = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            _logger.Warning(
                "[ClashSingBoxApi] Proxy switch failed: {Group} → {Name} HTTP {Code}: {Body}",
                group, name, (int)response.StatusCode, respBody);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] SelectProxyAsync failed");
            return false;
        }
    }

    public async Task<string?> GetGroupNowAsync(string group, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            var encodedGroup = Uri.EscapeDataString(group);
            using var response = await _http
                .GetAsync($"{_baseUrl}/proxies/{encodedGroup}", deadlineCts.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: deadlineCts.Token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("now", out var nowEl) &&
                nowEl.ValueKind == JsonValueKind.String)
            {
                var now = nowEl.GetString();
                return string.IsNullOrEmpty(now) ? null : now;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProxyInfo>> ListProxiesAsync(CancellationToken ct = default)
    {
        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(PingDeadline);

            using var response = await _http
                .GetAsync($"{_baseUrl}/proxies", deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return Array.Empty<ProxyInfo>();

            await using var stream = await response.Content.ReadAsStreamAsync(deadlineCts.Token).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync(
                stream, Json.AppJsonContext.Default.ProxiesEnvelopeDto, deadlineCts.Token).ConfigureAwait(false);

            if (dto?.Proxies is null)
                return Array.Empty<ProxyInfo>();

            var list = new List<ProxyInfo>(dto.Proxies.Count);
            foreach (var kv in dto.Proxies)
            {
                var name = kv.Key;
                var meta = kv.Value;
                if (meta is null)
                    continue;

                int? delayMs = null;
                DateTimeOffset? delayAt = null;
                if (meta.History is { Count: > 0 })
                {
                    var last = meta.History[meta.History.Count - 1];
                    delayMs = last.Delay > 0 ? last.Delay : null;
                    if (DateTimeOffset.TryParse(last.Time, out var parsed))
                        delayAt = parsed;
                }

                list.Add(new ProxyInfo(
                    Name: name,
                    Type: meta.Type ?? "unknown",
                    DelayMs: delayMs,
                    DelayMeasuredAt: delayAt));
            }
            return list;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ProxyInfo>();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] ListProxiesAsync failed");
            return Array.Empty<ProxyInfo>();
        }
    }

    public async Task<int?> GetProxyDelayAsync(string proxyTag, string testUrl, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(proxyTag) || string.IsNullOrWhiteSpace(testUrl))
            return null;

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs + 1500));

            var encodedTag = Uri.EscapeDataString(proxyTag);
            var encodedUrl = Uri.EscapeDataString(testUrl);
            var url = $"{_baseUrl}/proxies/{encodedTag}/delay?timeout={timeoutMs}&url={encodedUrl}";

            using var response = await _http.GetAsync(url, deadlineCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Debug("[ClashSingBoxApi] Proxy delay probe {Tag} -> HTTP {Code} (treated as unreachable)",
                    proxyTag, (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(deadlineCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("delay", out var delayEl)
                && delayEl.TryGetInt32(out var delay)
                && delay >= 0)
            {
                return delay;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[ClashSingBoxApi] GetProxyDelayAsync failed for {Tag}", proxyTag);
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    internal static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return IPAddress.IsLoopback(ip);

        return false;
    }

    internal sealed class VersionDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    internal static bool ParseConnectionsSummary(
        ReadOnlySpan<byte> json, out long download, out long upload, out int activeCount)
    {
        download = 0; upload = 0; activeCount = 0;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            var hasDownload = false;
            var hasUpload = false;
            var hasConnections = false;
            var completedRootObject = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    completedRootObject = true;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;

                if (reader.ValueTextEquals("downloadTotal"))
                {
                    if (hasDownload || !reader.Read() || reader.TokenType != JsonTokenType.Number)
                        return false;
                    if (!reader.TryGetInt64(out var downVal) || downVal < 0)
                        return false;
                    download = downVal;
                    hasDownload = true;
                }
                else if (reader.ValueTextEquals("uploadTotal"))
                {
                    if (hasUpload || !reader.Read() || reader.TokenType != JsonTokenType.Number)
                        return false;
                    if (!reader.TryGetInt64(out var upVal) || upVal < 0)
                        return false;
                    upload = upVal;
                    hasUpload = true;
                }
                else if (reader.ValueTextEquals("connections"))
                {
                    if (hasConnections || !reader.Read())
                        return false;

                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        hasConnections = true;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (activeCount == int.MaxValue)
                                return false;
                            activeCount++;
                            reader.Skip();
                        }

                        if (reader.TokenType != JsonTokenType.EndArray)
                            return false;
                    }
                    else if (reader.TokenType == JsonTokenType.Null)
                    {
                        hasConnections = true;
                        activeCount = 0;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    if (!reader.Read())
                        return false;
                    reader.Skip();
                }
            }

            if (!completedRootObject || !hasDownload || !hasUpload || !hasConnections)
            {
                download = 0; upload = 0; activeCount = 0;
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.Comment)
                {
                    download = 0; upload = 0; activeCount = 0;
                    return false;
                }
            }

            return true;
        }
        catch
        {
            download = 0; upload = 0; activeCount = 0;
            return false;
        }
    }

    internal sealed class ConnectionsDto
    {
        [JsonPropertyName("downloadTotal")]
        public long DownloadTotal { get; set; }

        [JsonPropertyName("uploadTotal")]
        public long UploadTotal { get; set; }

        [JsonPropertyName("connections")]
        public List<JsonElement>? Connections { get; set; }
    }

    internal sealed class ProxiesEnvelopeDto
    {
        [JsonPropertyName("proxies")]
        public Dictionary<string, ProxyDto>? Proxies { get; set; }
    }

    internal sealed class ProxyDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("history")]
        public List<ProxyHistoryDto>? History { get; set; }
    }

    internal sealed class ProxyHistoryDto
    {
        [JsonPropertyName("time")]
        public string? Time { get; set; }

        [JsonPropertyName("delay")]
        public int Delay { get; set; }
    }
}

internal sealed record ClashSetConfigDto(
    [property: JsonPropertyName("path")] string Path);

internal sealed record ClashSelectProxyDto(
    [property: JsonPropertyName("name")] string Name);
