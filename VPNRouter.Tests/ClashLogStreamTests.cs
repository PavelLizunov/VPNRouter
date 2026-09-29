#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ClashLogStreamTests
{
    private const string EofJson =
        "{\"type\":\"error\",\"payload\":\"+0300 2026-06-19 15:50:54 ERROR [810041638 108ms] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF\"}";
    private const string LocalCloseJson =
        "{\"type\":\"error\",\"payload\":\"+0300 2026-06-19 15:56:18 ERROR [2130031130 26m27s] connection: connection upload closed: raw read: An existing connection was forcibly closed by the remote host.\"}";
    private const string DnsJson =
        "{\"type\":\"info\",\"payload\":\"+0300 2026-06-19 01:34:57 INFO [1 4ms] dns: exchanged A example.com. 14 IN A 203.0.113.5\"}";

    [Theory]
    [InlineData("http://127.0.0.1:9090", "ws://127.0.0.1:9090/logs?level=info")]
    [InlineData("http://127.0.0.1:9090/", "ws://127.0.0.1:9090/logs?level=info")]
    [InlineData("http://localhost:9090", "ws://localhost:9090/logs?level=info")]
    [InlineData("https://127.0.0.1:9090", "wss://127.0.0.1:9090/logs?level=info")]
    public void BuildLogsUri_ConvertsSchemeAndAppendsLogs(string baseUrl, string expected)
        => Assert.Equal(expected, ClashLogStream.BuildLogsUri(baseUrl).ToString());

    [Theory]
    [InlineData("http://1.2.3.4:9090")]
    [InlineData("http://192.168.0.10:9090")]
    public void BuildLogsUri_RejectsNonLoopback(string baseUrl)
        => Assert.Throws<System.ArgumentException>(() => ClashLogStream.BuildLogsUri(baseUrl));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://127.0.0.1:9090")]
    [InlineData("127.0.0.1:9090")]
    public void BuildLogsUri_RejectsInvalid(string baseUrl)
        => Assert.Throws<System.ArgumentException>(() => ClashLogStream.BuildLogsUri(baseUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildLogsUri_NullOrEmptySecret_ProducesNoToken(string? secret)
    {
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", secret);
        Assert.Equal("ws://127.0.0.1:9090/logs?level=info", uri.ToString());
        Assert.DoesNotContain("token", uri.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("p@ss&word=123+456?#/:;,$%")]
    [InlineData("!*'();:@&=+$,/?#[] ")]
    public void BuildLogsUri_EncodesReservedCharacters(string secret)
    {
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", secret);
        var expectedEscaped = Uri.EscapeDataString(secret);
        Assert.Equal($"ws://127.0.0.1:9090/logs?level=info&token={expectedEscaped}", uri.AbsoluteUri);
        Assert.Contains($"&token={expectedEscaped}", uri.AbsoluteUri);
        Assert.DoesNotContain("&word=", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("секрет \U0001F511")]
    [InlineData("токен-123-слово")]
    [InlineData("パスワード\U0001F512")]
    public void BuildLogsUri_EncodesNonAsciiCharacters(string secret)
    {
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", secret);
        var expectedEscaped = Uri.EscapeDataString(secret);
        Assert.Equal($"ws://127.0.0.1:9090/logs?level=info&token={expectedEscaped}", uri.AbsoluteUri);
        Assert.DoesNotContain(secret, uri.AbsoluteUri);
    }

    [Fact]
    public void TryExtractPayload_ValidMessage_ReturnsPayload()
    {
        Assert.True(ClashLogStream.TryExtractPayload(EofJson, out var payload));
        Assert.Contains("using outbound/vless[proxy]: EOF", payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"info\"}")]
    [InlineData("{\"type\":\"info\",\"payload\":\"\"}")]
    [InlineData("{\"type\":\"info\",\"payload\":123}")]
    public void TryExtractPayload_MalformedOrMissing_ReturnsFalse(string json)
        => Assert.False(ClashLogStream.TryExtractPayload(json, out _));

    private static (ClashLogStream stream, ConnectionHealthState state) NewStream()
    {
        var state = new ConnectionHealthState();
        var stream = new ClashLogStream("http://127.0.0.1:9090", state);
        return (stream, state);
    }

    [Fact]
    public void HandleMessage_RelayOpenEof_RecordedAsFail()
    {
        var (stream, state) = NewStream();
        stream.HandleMessage(EofJson);
        Assert.Equal(1, state.Snapshot().RelayOpenFails);
    }

    [Fact]
    public void HandleMessage_LocalClose_NotCountedAsFail()
    {
        var (stream, state) = NewStream();
        stream.HandleMessage(LocalCloseJson);
        var snap = state.Snapshot();
        Assert.Equal(1, snap.LocalCloses);
        Assert.Equal(0, snap.RelayOpenFails);
    }

    [Fact]
    public void HandleMessage_NonConnectionLine_RecordsNothing()
    {
        var (stream, state) = NewStream();
        stream.HandleMessage(DnsJson);
        var snap = state.Snapshot();
        Assert.Equal(0, snap.RelayOpenFails);
        Assert.Equal(0, snap.LocalCloses);
        Assert.Equal(0, snap.Other);
    }

    [Fact]
    public void HandleMessage_Malformed_DoesNotThrow()
    {
        var (stream, _) = NewStream();
        stream.HandleMessage("garbage{");
        stream.HandleMessage("");
    }

    [Fact]
    public void RedactLogsUri_StripsQuery()
    {
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", "s3cret");
        var redacted = ClashLogStream.RedactLogsUri(uri);
        Assert.Equal("ws://127.0.0.1:9090/logs", redacted);
        Assert.DoesNotContain("token", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cret", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("p@ss&word=123+456?#/:;,$%")]
    [InlineData("секрет \U0001F511")]
    [InlineData("simpleSecret123")]
    public void RedactLogsUri_ExcludesOriginalAndEncodedToken(string secret)
    {
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", secret);
        var redacted = ClashLogStream.RedactLogsUri(uri);
        var encoded = Uri.EscapeDataString(secret);

        Assert.Equal("ws://127.0.0.1:9090/logs", redacted);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("token", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("simpleSecret123")]
    [InlineData("p@ss&word=123+456?#/:;,$%")]
    [InlineData("секрет \U0001F511")]
    public void LogStreamFailure_NestedExceptionContainingTokenOrUri_NeverLeaksIntoRenderPropertiesOrException(string secret)
    {
        var (logger, sink) = BuildCapturingLogger();
        var uri = ClashLogStream.BuildLogsUri("http://127.0.0.1:9090", secret);
        var rawUri = uri.ToString();
        var encodedSecret = Uri.EscapeDataString(secret);

        var innerException = new InvalidOperationException($"Transport connection failed for {rawUri} (token={secret})");
        var outerException = new System.Net.WebSockets.WebSocketException(
            $"WebSocket handshake failed on {rawUri} with secret {secret}",
            innerException);

        ClashLogStream.LogStreamFailure(logger, outerException, TimeSpan.FromSeconds(5));

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Debug, logEvent.Level);

        Assert.Null(logEvent.Exception);

        Assert.True(logEvent.Properties.TryGetValue("ErrorType", out var errorTypeVal));
        var errorType = Assert.IsType<ScalarValue>(errorTypeVal).Value?.ToString();
        Assert.Equal(nameof(System.Net.WebSockets.WebSocketException), errorType);

        Assert.True(logEvent.Properties.TryGetValue("Sec", out var secVal));
        var sec = Convert.ToDouble(Assert.IsType<ScalarValue>(secVal).Value);
        Assert.Equal(5.0, sec);

        var rendered = logEvent.RenderMessage();
        Assert.Contains(nameof(System.Net.WebSockets.WebSocketException), rendered);
        Assert.Contains("5", rendered);
        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedSecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(rawUri, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outerException.Message, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(innerException.Message, rendered, StringComparison.Ordinal);

        foreach (var kvp in logEvent.Properties)
        {
            var propText = kvp.Value.ToString();
            Assert.DoesNotContain(secret, propText, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedSecret, propText, StringComparison.Ordinal);
            Assert.DoesNotContain(rawUri, propText, StringComparison.Ordinal);
        }
    }

    private static (ILogger logger, CapturingSink sink) BuildCapturingLogger()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        private readonly object _gate = new();

        public void Emit(LogEvent logEvent)
        {
            lock (_gate) _events.Add(logEvent);
        }

        public IReadOnlyList<LogEvent> Events
        {
            get { lock (_gate) return _events.ToList(); }
        }
    }
}
