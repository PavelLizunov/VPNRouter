#nullable enable
using System;
using System.IO;
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
}
