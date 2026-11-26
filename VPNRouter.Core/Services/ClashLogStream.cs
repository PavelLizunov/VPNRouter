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

    // One log record is normally a few KiB. A larger WebSocket message is
    // drained and discarded instead of growing a StringBuilder without bound.
    internal const int MaxMessageBytes = 256 * 1024;

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
        var message = new ClashLogMessageBuffer(MaxMessageBytes);
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

            // Oversized messages keep being received (drained) until their
            // final fragment so the next record starts on a clean buffer.
            message.Append(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage)
                continue;

            var text = message.Complete();
            if (text is not null)
                HandleMessage(text);
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

/// <summary>
/// Accumulates one WebSocket message with a hard byte ceiling. When the
/// ceiling is crossed the buffered bytes are discarded, but <see cref="Complete"/>
/// is still called at the final fragment so the following message is parsed.
/// </summary>
internal sealed class ClashLogMessageBuffer
{
    private readonly int _maxBytes;
    private readonly int _initialBytes;
    private byte[] _buffer;
    private int _length;
    private bool _oversized;

    internal ClashLogMessageBuffer(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
        _initialBytes = Math.Min(maxBytes, 16 * 1024);
        _buffer = new byte[_initialBytes];
    }

    internal int BufferedBytes => _length;

    internal bool IsOversized => _oversized;

    internal void Append(ReadOnlySpan<byte> chunk)
    {
        if (_oversized)
            return;

        if (chunk.Length > _maxBytes - _length)
        {
            _oversized = true;
            _length = 0;
            return;
        }

        if (chunk.Length > _buffer.Length - _length)
            Array.Resize(ref _buffer, Math.Min(_maxBytes, Math.Max(_buffer.Length * 2, _length + chunk.Length)));

        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
    }

    /// <summary>
    /// Returns the decoded message, or null when it exceeded the ceiling.
    /// Always resets the buffer for the next message.
    /// </summary>
    internal string? Complete()
    {
        if (_oversized)
        {
            Reset();
            return null;
        }

        var text = Encoding.UTF8.GetString(_buffer, 0, _length);
        Reset();
        return text;
    }

    internal void Reset()
    {
        if (_buffer.Length > _initialBytes)
            _buffer = new byte[_initialBytes];
        _length = 0;
        _oversized = false;
    }
}
