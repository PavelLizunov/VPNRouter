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

/// <summary>
/// B0b: pins the testable surface of the Clash /logs stream — URL→ws conversion
/// with the loopback guard, the {type,payload} JSON parse, and message routing into
/// ConnectionHealthState. The live WebSocket receive/reconnect loop is verified by a
/// smoke test on the test machine (needs a running sing-box), not here.
/// </summary>
public sealed class ClashLogStreamTests
{
    private const string EofJson =
        "{\"type\":\"error\",\"payload\":\"+0300 2026-06-19 15:50:54 ERROR [810041638 108ms] connection: open connection to 203.0.113.10:21115 using outbound/vless[proxy]: EOF\"}";
    private const string LocalCloseJson =
        "{\"type\":\"error\",\"payload\":\"+0300 2026-06-19 15:56:18 ERROR [2130031130 26m27s] connection: connection upload closed: raw read: An existing connection was forcibly closed by the remote host.\"}";
    private const string DnsJson =
        "{\"type\":\"info\",\"payload\":\"+0300 2026-06-19 01:34:57 INFO [1 4ms] dns: exchanged A example.com. 14 IN A 203.0.113.5\"}";

    // ---- BuildLogsUri: scheme conversion + loopback guard ----

    [Theory]
    [InlineData("http://127.0.0.1:9090", "ws://127.0.0.1:9090/logs?level=info")]
    [InlineData("http://127.0.0.1:9090/", "ws://127.0.0.1:9090/logs?level=info")]
    [InlineData("http://localhost:9090", "ws://localhost:9090/logs?level=info")]
    [InlineData("https://127.0.0.1:9090", "wss://127.0.0.1:9090/logs?level=info")]
    public void BuildLogsUri_ConvertsSchemeAndAppendsLogs(string baseUrl, string expected)
        => Assert.Equal(expected, ClashLogStream.BuildLogsUri(baseUrl).ToString());

    [Theory]
    [InlineData("http://1.2.3.4:9090")]      // non-loopback — security guard
    [InlineData("http://192.168.0.10:9090")] // LAN host — refused
    public void BuildLogsUri_RejectsNonLoopback(string baseUrl)
        => Assert.Throws<System.ArgumentException>(() => ClashLogStream.BuildLogsUri(baseUrl));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://127.0.0.1:9090")]
    [InlineData("127.0.0.1:9090")] // missing scheme
    public void BuildLogsUri_RejectsInvalid(string baseUrl)
        => Assert.Throws<System.ArgumentException>(() => ClashLogStream.BuildLogsUri(baseUrl));

    // ---- BuildLogsUri token parameter encoding & empty handling ----

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
        // Unencoded reserved query delimiter '&' from within secret must not split queries
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
        Assert.DoesNotContain(secret, uri.AbsoluteUri); // Raw non-ASCII characters are percent-encoded
    }

    // ---- TryExtractPayload ----

    [Fact]
    public void TryExtractPayload_ValidMessage_ReturnsPayload()
    {
        Assert.True(ClashLogStream.TryExtractPayload(EofJson, out var payload));
        Assert.Contains("using outbound/vless[proxy]: EOF", payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"info\"}")]            // no payload field
    [InlineData("{\"type\":\"info\",\"payload\":\"\"}")] // empty payload
    [InlineData("{\"type\":\"info\",\"payload\":123}")]  // non-string payload
    public void TryExtractPayload_MalformedOrMissing_ReturnsFalse(string json)
        => Assert.False(ClashLogStream.TryExtractPayload(json, out _));

    // ---- HandleMessage routes classified events into the state ----

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

    // ---- OBS-1: RedactLogsUri strips ?token= from logged URI ----

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

    [Fact]
    public void RunAsync_InformationCall_UsesRedactLogsUri()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var src = File.ReadAllText(Path.Combine(
            dir!.FullName, "VPNRouter.Core", "Services", "ClashLogStream.cs"));
        Assert.Contains("RedactLogsUri(_logsUri)", src);
        Assert.DoesNotContain("Information(\"[ConnHealth] Clash /logs stream connected ({Uri})\", _logsUri)", src);
    }

    // ---- NIGHT-12: Source guard for TryStartConnectionHealthStream ----

    [Fact]
    public void TryStartConnectionHealthStream_PassesClashApiSecret_CommentsStripped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
            dir = dir.Parent;

        if (dir == null)
        {
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
                dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var vpnEnginePath = Path.Combine(
            dir!.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
        Assert.True(File.Exists(vpnEnginePath), $"VpnEngine.cs not found at {vpnEnginePath}");

        var fullSrc = File.ReadAllText(vpnEnginePath);

        // Bound to the actual TryStartConnectionHealthStream method
        const string methodSignature = "void TryStartConnectionHealthStream(AppSettings settings)";
        var methodIdx = fullSrc.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(methodIdx >= 0, "TryStartConnectionHealthStream method not found in VpnEngine.cs");

        var openBraceIdx = fullSrc.IndexOf('{', methodIdx);
        Assert.True(openBraceIdx > methodIdx, "Opening brace for TryStartConnectionHealthStream not found");

        var depth = 0;
        var closeBraceIdx = -1;
        for (int i = openBraceIdx; i < fullSrc.Length; i++)
        {
            if (fullSrc[i] == '{') depth++;
            else if (fullSrc[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    closeBraceIdx = i;
                    break;
                }
            }
        }
        Assert.True(closeBraceIdx > openBraceIdx, "Closing brace for TryStartConnectionHealthStream not found");

        var methodSrc = fullSrc.Substring(methodIdx, closeBraceIdx - methodIdx + 1);

        // Strip comments (block and line) while preserving quoted string literals (e.g. "http://...")
        // to ensure dummy comments cannot satisfy the guard and URLs aren't truncated.
        var commentsStripped = Regex.Replace(
            methodSrc,
            @"(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")|(/\*[\s\S]*?\*/|//.*$)",
            m => m.Groups[1].Success ? m.Groups[1].Value : string.Empty,
            RegexOptions.Multiline);

        // Bounded actual constructor instantiation requiring real secret argument (not dummy comment)
        var constructorIdx = commentsStripped.IndexOf("new ClashLogStream(", StringComparison.Ordinal);
        Assert.True(constructorIdx >= 0, "new ClashLogStream constructor call not found in stripped method body");

        var closeParenIdx = commentsStripped.IndexOf(')', constructorIdx);
        Assert.True(closeParenIdx > constructorIdx, "Closing parenthesis for constructor call not found");

        var constructorArgs = commentsStripped.Substring(constructorIdx, closeParenIdx - constructorIdx + 1);
        Assert.Contains("secret: settings.SingBox.ClashApiSecret", constructorArgs);
    }

    // ---- NIGHT-12: Synthetic Serilog sink proof for LogStreamFailure ----

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

        // Nested exception chain simulating transport failure where raw URI and token are embedded
        var innerException = new InvalidOperationException($"Transport connection failed for {rawUri} (token={secret})");
        var outerException = new System.Net.WebSockets.WebSocketException(
            $"WebSocket handshake failed on {rawUri} with secret {secret}",
            innerException);

        ClashLogStream.LogStreamFailure(logger, outerException, TimeSpan.FromSeconds(5));

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Debug, logEvent.Level);

        // Exception object MUST NOT be passed to logger (no stack trace or exception message leakage)
        Assert.Null(logEvent.Exception);

        // Structured properties verify safe type name only and retry seconds
        Assert.True(logEvent.Properties.TryGetValue("ErrorType", out var errorTypeVal));
        var errorType = Assert.IsType<ScalarValue>(errorTypeVal).Value?.ToString();
        Assert.Equal(nameof(System.Net.WebSockets.WebSocketException), errorType);

        Assert.True(logEvent.Properties.TryGetValue("Sec", out var secVal));
        var sec = Convert.ToDouble(Assert.IsType<ScalarValue>(secVal).Value);
        Assert.Equal(5.0, sec);

        // Render proof: rendered message includes safe type name and retry seconds, never secrets or raw uri
        var rendered = logEvent.RenderMessage();
        Assert.Contains(nameof(System.Net.WebSockets.WebSocketException), rendered);
        Assert.Contains("5", rendered);
        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedSecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(rawUri, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outerException.Message, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(innerException.Message, rendered, StringComparison.Ordinal);

        // Properties proof: no property value leaks token or raw uri
        foreach (var kvp in logEvent.Properties)
        {
            var propText = kvp.Value.ToString();
            Assert.DoesNotContain(secret, propText, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedSecret, propText, StringComparison.Ordinal);
            Assert.DoesNotContain(rawUri, propText, StringComparison.Ordinal);
        }
    }

    // ---- NIGHT-12: Source guard for RunAsync catch block and LogStreamFailure ----

    [Fact]
    public void RunAsync_CatchBlock_PinsSafeTypeNameAndNoExceptionLog_CommentsStripped()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
            dir = dir.Parent;
        if (dir == null)
        {
            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "VPNRouter.Core")))
                dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var streamPath = Path.Combine(
            dir!.FullName, "VPNRouter.Core", "Services", "ClashLogStream.cs");
        Assert.True(File.Exists(streamPath), $"ClashLogStream.cs not found at {streamPath}");

        var fullSrc = File.ReadAllText(streamPath);

        // 1. Bound to RunAsync method
        const string runAsyncSig = "async Task RunAsync(CancellationToken ct)";
        var runIdx = fullSrc.IndexOf(runAsyncSig, StringComparison.Ordinal);
        Assert.True(runIdx >= 0, "RunAsync method not found in ClashLogStream.cs");

        var openBraceIdx = fullSrc.IndexOf('{', runIdx);
        var depth = 0;
        var closeBraceIdx = -1;
        for (int i = openBraceIdx; i < fullSrc.Length; i++)
        {
            if (fullSrc[i] == '{') depth++;
            else if (fullSrc[i] == '}')
            {
                depth--;
                if (depth == 0) { closeBraceIdx = i; break; }
            }
        }
        Assert.True(closeBraceIdx > openBraceIdx, "Closing brace for RunAsync not found");
        var runSrc = fullSrc.Substring(runIdx, closeBraceIdx - runIdx + 1);

        var runStripped = Regex.Replace(
            runSrc,
            @"(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")|(/\*[\s\S]*?\*/|//.*$)",
            m => m.Groups[1].Success ? m.Groups[1].Value : string.Empty,
            RegexOptions.Multiline);

        // Disallow exception-bearing logger overloads in RunAsync
        Assert.DoesNotContain("Debug(ex,", runStripped);
        Assert.DoesNotContain("_logger.Debug(ex,", runStripped);
        Assert.DoesNotContain("_logger.Error(ex,", runStripped);
        Assert.DoesNotContain("_logger.Warning(ex,", runStripped);

        // Catch block must delegate to LogStreamFailure
        Assert.Contains("LogStreamFailure(_logger, ex, backoff)", runStripped);

        // 2. Bound to LogStreamFailure method
        const string helperSig = "void LogStreamFailure(ILogger logger, Exception ex, TimeSpan backoff)";
        var helperIdx = fullSrc.IndexOf(helperSig, StringComparison.Ordinal);
        Assert.True(helperIdx >= 0, "LogStreamFailure method not found in ClashLogStream.cs");

        var hOpenBraceIdx = fullSrc.IndexOf('{', helperIdx);
        depth = 0;
        var hCloseBraceIdx = -1;
        for (int i = hOpenBraceIdx; i < fullSrc.Length; i++)
        {
            if (fullSrc[i] == '{') depth++;
            else if (fullSrc[i] == '}')
            {
                depth--;
                if (depth == 0) { hCloseBraceIdx = i; break; }
            }
        }
        Assert.True(hCloseBraceIdx > hOpenBraceIdx, "Closing brace for LogStreamFailure not found");
        var helperSrc = fullSrc.Substring(helperIdx, hCloseBraceIdx - helperIdx + 1);

        var helperStripped = Regex.Replace(
            helperSrc,
            @"(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")|(/\*[\s\S]*?\*/|//.*$)",
            m => m.Groups[1].Success ? m.Groups[1].Value : string.Empty,
            RegexOptions.Multiline);

        // Must pin safe type name only and structured ErrorType / Sec
        Assert.Contains("ex.GetType().Name", helperStripped);
        Assert.Contains("{ErrorType}", helperStripped);
        Assert.Contains("{Sec}", helperStripped);
        Assert.Contains("backoff.TotalSeconds", helperStripped);

        // Must NOT log exception object, message, or ToString
        Assert.DoesNotContain("Debug(ex,", helperStripped);
        Assert.DoesNotContain("ex.Message", helperStripped);
        Assert.DoesNotContain("ex.ToString", helperStripped);
        Assert.DoesNotContain("_logsUri", helperStripped);
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
