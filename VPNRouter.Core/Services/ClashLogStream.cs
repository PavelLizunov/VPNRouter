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

/// <summary>
/// B0b of the server-health backlog: subscribes to sing-box's Clash API
/// <c>/logs</c> WebSocket and feeds each log message through
/// <see cref="ConnectionHealthClassifier"/> into <see cref="ConnectionHealthState"/>.
///
/// <para><strong>Observe-only.</strong> It only records classified events; it never
/// toasts or fails over. Calibration data for backlog C/B.</para>
///
/// <para><strong>Why the live stream and not file-tail:</strong> the independent
/// review (§B11) noted Clash <c>/connections</c> exposes no close reason, but the
/// <c>/logs</c> stream emits each entry as <c>{ "type", "payload" }</c> — the live,
/// structured source. A WebSocket avoids singbox.log rotation / encoding /
/// partial-line races.</para>
///
/// <para><strong>Loopback-only.</strong> <see cref="BuildLogsUri"/> reuses
/// <see cref="ClashSingBoxApi.IsLoopbackHost"/>; a non-loopback Clash base is
/// refused, mirroring the proxy-control client's hard guard.</para>
///
/// <para>The receive loop reconnects with capped exponential backoff and is fully
/// cancellable; <see cref="Stop"/> signals it without blocking.</para>
/// </summary>
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

    /// <param name="clashBaseUrl">Clash API HTTP base, e.g. "http://127.0.0.1:9090".
    /// Must be loopback (hard guard).</param>
    /// <param name="state">Aggregator that receives classified events.</param>
    /// <param name="proxyEndpoints">Optional accessor for active proxy socket
    /// endpoints ("ip:port") — lets the classifier attribute mid-stream
    /// <see cref="ConnHealthCategory.ProxyStreamError"/>. May be null; the primary
    /// relay-open failure-rate signal does not need it.</param>
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

    /// <summary>Convert the Clash HTTP base URL into the ws(s) <c>/logs</c> endpoint,
    /// enforcing the same loopback-only guard as <see cref="ClashSingBoxApi"/>.
    /// P1 clash_api secret (2026-07-10): WebSocket clients can't send an
    /// Authorization header through ClientWebSocket portably — the Clash API's
    /// documented WS auth is the <c>?token=</c> query parameter.</summary>
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

    /// <summary>URI without query string so a <c>?token=</c> secret never reaches the log.</summary>
    internal static string RedactLogsUri(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";

    /// <summary>Start the background subscribe/reconnect loop. A second call while
    /// already running is ignored.</summary>
    public void Start()
    {
        // Only no-op if a loop is genuinely still RUNNING. After Stop() the loop
        // task completes but _loop stays non-null; gating on `is not null` would
        // then make a later Start() a silent no-op against a cancelled token (a
        // fail-silent dead stream). Allow restart once the prior loop finished.
        if (_loop is { IsCompleted: false })
            return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    /// <summary>Signal the loop to stop (non-blocking). Safe to call repeatedly.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { /* already disposed */ }
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
                backoff = MinBackoff; // reset after a successful connect
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[ConnHealth] Clash /logs stream dropped; retry in {Sec}s", backoff.TotalSeconds);
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
                break; // drop -> outer loop reconnects with backoff
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue; // accumulate a fragmented message before parsing

            HandleMessage(sb.ToString());
            sb.Clear();
        }
    }

    /// <summary>Parse one Clash <c>/logs</c> JSON message and record the classified
    /// event. Internal for unit testing without a live socket.</summary>
    internal void HandleMessage(string json)
    {
        if (!TryExtractPayload(json, out var payload))
            return;
        var ev = ConnectionHealthClassifier.Classify(payload, _proxyEndpoints());
        if (ev is not null)
            _state.Record(ev);
    }

    /// <summary>Extract the <c>payload</c> string from a Clash <c>/logs</c> message
    /// (<c>{ "type": "...", "payload": "..." }</c>). Returns false on malformed JSON
    /// or a missing/empty payload.</summary>
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
            // partial/garbled frame — drop it; the stream keeps going
        }
        return false;
    }
}
