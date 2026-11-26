#nullable enable
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

    // /connections carries live connection metadata; a runaway response is
    // treated as a failed snapshot instead of being buffered without limit.
    internal const long MaxConnectionsResponseBytes = 4L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger _logger;
    private readonly bool _ownsHttpClient;

    public ClashSingBoxApi(
        HttpClient? httpClient = null,
        string baseUrl = "http://127.0.0.1:9090",
        ILogger? logger = null,
        string? secret = null) : this(httpClient, baseUrl, logger, secret, localBind: false) { }

    internal static ClashSingBoxApi ForController(Uri endpoint, string? secret, ILogger? logger) =>
        new(null, endpoint.AbsoluteUri, logger, secret, localBind: true);

    private ClashSingBoxApi(HttpClient? httpClient, string baseUrl, ILogger? logger, string? secret, bool localBind)
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

        if (!localBind && !IsLoopbackHost(uri.Host))
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
            _http = localBind ? new HttpClient(CreateControllerHandler()) : new HttpClient();
            _http.Timeout = Timeout.InfiniteTimeSpan;
            _ownsHttpClient = true;
        }

        if (!string.IsNullOrEmpty(secret))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
    }

    internal static SocketsHttpHandler CreateControllerHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (context, ct) => new NetworkStream(
            await OpenControlSocketAsync(context.InitialRequestMessage.RequestUri!, ct).ConfigureAwait(false), ownsSocket: true)
    };

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
                .GetAsync(
                    $"{_baseUrl}/connections",
                    HttpCompletionOption.ResponseHeadersRead,
                    deadlineCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return failureSnapshot;

            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength > MaxConnectionsResponseBytes)
            {
                return failureSnapshot;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(deadlineCts.Token)
                .ConfigureAwait(false);

            var parsed = await ParseConnectionsSummaryAsync(
                    stream, MaxConnectionsResponseBytes, deadlineCts.Token)
                .ConfigureAwait(false);

            if (!parsed.Ok)
                return failureSnapshot;

            return new ConnectionsSnapshot(
                ActiveCount: parsed.ActiveCount,
                TotalUploadBytes: parsed.Upload,
                TotalDownloadBytes: parsed.Download,
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

    internal static Uri LocalController(string? bindAddress)
    {
        var address = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1:9090" : bindAddress.Trim();
        if (address.StartsWith(':')) address = "127.0.0.1" + address;
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("The VPN control endpoint is invalid.");

        var host = uri.IdnHost.Trim('[', ']');
        if (host == "0.0.0.0") host = "127.0.0.1";
        else if (host == "::") host = "::1";
        return new UriBuilder(Uri.UriSchemeHttp, host, uri.Port).Uri;
    }

    internal static async Task<IPAddress[]> ResolveControlTargetsAsync(Uri endpoint, CancellationToken ct)
    {
        var host = endpoint.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return Socket.OSSupportsIPv6
                ? (Socket.OSSupportsIPv4 ? [IPAddress.IPv6Loopback, IPAddress.Loopback] : [IPAddress.IPv6Loopback])
                : [IPAddress.Loopback];
        if (IPAddress.TryParse(host, out var literal) && IPAddress.IsLoopback(literal))
            return [literal];

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var resolved = literal != null ? new[] { literal } :
            await Dns.GetHostAddressesAsync(host, deadline.Token).ConfigureAwait(false);
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
        var targets = resolved.Where(a => IPAddress.IsLoopback(a) || local.Contains(a)).ToArray();
        return targets.Length > 0 ? targets : throw new InvalidDataException("A local VPN control endpoint is required.");
    }

    internal static async Task<Socket> OpenControlSocketAsync(Uri endpoint, CancellationToken ct)
    {
        var targets = await ResolveControlTargetsAsync(endpoint, ct).ConfigureAwait(false);
        // Windows can spend the entire health-ping budget refusing the first address family.
        // Race local candidates so an IPv4-only or IPv6-only listener gets the full HTTP deadline.
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = targets.Select(ip => ConnectLocalAsync(ip, endpoint.Port, race.Token)).ToList();
        try
        {
            while (pending.Count > 0)
            {
                var attempt = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(attempt);
                try { return await attempt.ConfigureAwait(false); }
                catch (SocketException) { }
            }
            ct.ThrowIfCancellationRequested();
            throw new IOException("The local VPN control endpoint is unavailable.");
        }
        finally
        {
            race.Cancel();
            foreach (var attempt in pending)
            {
                try { (await attempt.ConfigureAwait(false)).Dispose(); }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task<Socket> ConnectLocalAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // A removed local address must never become a remote control peer.
            socket.Bind(new IPEndPoint(ip, 0));
            await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static (Uri Endpoint, string? Secret) ReadLocalController(string configJson)
    {
        using var config = JsonDocument.Parse(configJson);
        var clash = config.RootElement.GetProperty("experimental").GetProperty("clash_api");
        var bind = clash.GetProperty("external_controller").GetString();
        var secret = clash.TryGetProperty("secret", out var property) ? property.GetString() : null;
        return (LocalController(bind), secret);
    }

    internal static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
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
        var summary = new ConnectionsSummaryReader();
        ConnectionsParseResult result = default;
        try
        {
            var reader = new Utf8JsonReader(json);
            if (summary.Read(ref reader)) result = summary.GetResult();
        }
        catch (JsonException) { }

        (download, upload, activeCount) = (result.Download, result.Upload, result.ActiveCount);
        return result.Ok;
    }

    internal readonly record struct ConnectionsParseResult(
        bool Ok, long Download, long Upload, int ActiveCount);

    internal static async Task<ConnectionsParseResult> ParseConnectionsSummaryAsync(
        Stream stream, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        // The reader needs a complete token per chunk. Clash metadata tokens are small.
        var capacity = (int)Math.Min(64 * 1024, maxBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            var summary = new ConnectionsSummaryReader();
            var state = new JsonReaderState();
            var buffered = 0;
            long totalRead = 0;
            var isFinal = false;
            while (true)
            {
                {
                    var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), isFinal, state);
                    if (!summary.Read(ref reader)) return default;
                    if (isFinal) return summary.GetResult();
                    state = reader.CurrentState;
                    var consumed = (int)reader.BytesConsumed;
                    buffered -= consumed;
                    buffer.AsSpan(consumed, buffered).CopyTo(buffer);
                }

                var available = capacity - buffered;
                if (available == 0) return default;
                // Read one extra byte at the ceiling to distinguish EOF from an oversized body.
                var remaining = maxBytes - totalRead;
                var requested = remaining < available ? (int)remaining + 1 : available;
                var read = await stream.ReadAsync(buffer.AsMemory(buffered, requested), ct).ConfigureAwait(false);
                if (read > remaining) return default;
                totalRead += read;
                buffered += read;
                isFinal = read == 0;
            }
        }
        catch (JsonException)
        {
            return default;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class ConnectionsSummaryReader
    {
        private enum Field { Other, Download, Upload, Connections }

        private Field _pending;
        private bool _started;
        private bool _completed;
        private bool _hasConnections;
        private bool _inConnections;
        private long? _download;
        private long? _upload;
        private int _activeCount;

        public ConnectionsParseResult GetResult() =>
            _completed && _hasConnections && _download.HasValue && _upload.HasValue
                ? new(true, _download.Value, _upload.Value, _activeCount)
                : default;

        public bool Read(ref Utf8JsonReader reader)
        {
            while (reader.Read())
            {
                var token = reader.TokenType;
                var depth = reader.CurrentDepth;
                if (!_started)
                {
                    if (token != JsonTokenType.StartObject) return false;
                    _started = true;
                }
                else if (_completed)
                {
                    return false;
                }
                else if (_pending != Field.Other)
                {
                    if (!ReadField(ref reader)) return false;
                    _pending = Field.Other;
                }
                else if (_inConnections)
                {
                    if (depth == 1 && token == JsonTokenType.EndArray)
                        _inConnections = false;
                    else if (depth == 2 && token is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                        _activeCount++;
                }
                else if (depth == 1 && token == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("downloadTotal"u8)) _pending = Field.Download;
                    else if (reader.ValueTextEquals("uploadTotal"u8)) _pending = Field.Upload;
                    else if (reader.ValueTextEquals("connections"u8)) _pending = Field.Connections;
                }
                else if (depth == 0 && token == JsonTokenType.EndObject)
                {
                    _completed = true;
                }
            }
            return true;
        }

        private bool ReadField(ref Utf8JsonReader reader)
        {
            if (_pending == Field.Connections)
            {
                if (_hasConnections || reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.Null))
                    return false;
                _hasConnections = true;
                _inConnections = reader.TokenType == JsonTokenType.StartArray;
                return true;
            }

            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0)
                return false;

            if (_pending == Field.Download)
            {
                if (_download.HasValue) return false;
                _download = value;
            }
            else
            {
                if (_upload.HasValue) return false;
                _upload = value;
            }
            return true;
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
