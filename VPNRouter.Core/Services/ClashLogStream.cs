#nullable enable
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VPNRouter.Core.Services;

public sealed class ClashLogStream : IDisposable
{
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly Uri _logsUri;
    private readonly ConnectionHealthState _state;
    private readonly Func<IReadOnlySet<string>?> _proxyEndpoints;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ClashLogStream(
        string clashBaseUrl,
        ConnectionHealthState state,
        Func<IReadOnlySet<string>?>? proxyEndpoints = null,
        ILogger? logger = null,
        string? secret = null)
    {
        _logsUri = BuildLogsUri(clashBaseUrl, secret);
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _proxyEndpoints = proxyEndpoints ?? (() => null);
        _logger = logger ?? Log.Logger;
    }

    internal static Uri BuildLogsUri(string clashBaseUrl, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(clashBaseUrl))
            throw new ArgumentException("Clash API base URL cannot be empty.", nameof(clashBaseUrl));

        var normalized = clashBaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                $"Clash API base URL must be an absolute http(s) URL; got '{clashBaseUrl}'.", nameof(clashBaseUrl));

        if (!ClashSingBoxApi.IsLoopbackHost(uri.Host))
            throw new ArgumentException(
                $"Clash API base URL must point at a loopback host; '{uri.Host}' is not loopback. " +
                "Remote Clash control is a security risk.", nameof(clashBaseUrl));

        var scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var token = string.IsNullOrEmpty(secret)
            ? string.Empty
            : $"&token={Uri.EscapeDataString(secret)}";
        return new Uri($"{scheme}://{uri.Authority}/logs?level=info{token}");
    }

    internal static string RedactLogsUri(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";

    internal static void LogStreamFailure(ILogger logger, Exception ex, TimeSpan backoff)
    {
        logger.Debug(
            "[ConnHealth] Clash /logs stream dropped ({ErrorType}); retry in {Sec}s",
            ex.GetType().Name,
            backoff.TotalSeconds);
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false })
            return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(_logsUri, ct).ConfigureAwait(false);
                _logger.Information("[ConnHealth] Clash /logs stream connected ({Uri})", RedactLogsUri(_logsUri));
                backoff = MinBackoff;
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogStreamFailure(_logger, ex, backoff);
            }

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
        }
        _logger.Debug("[ConnHealth] Clash /logs stream stopped");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;

            HandleMessage(sb.ToString());
            sb.Clear();
        }
    }

    internal void HandleMessage(string json)
    {
        if (!TryExtractPayload(json, out var payload))
            return;
        var ev = ConnectionHealthClassifier.Classify(payload, _proxyEndpoints());
        if (ev is not null)
            _state.Record(ev);
    }

    internal static bool TryExtractPayload(string json, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("payload", out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                payload = p.GetString() ?? string.Empty;
                return payload.Length > 0;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }
}
