using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

// Public behavior regressions for the span scheme prefilter; not a benchmark suite.
public sealed class PerformanceShareLinkTests
{
    public static IEnumerable<object[]> ShareLinks => new[]
    {
        new object[] { "vless", "test-user" },
        new object[] { "hysteria2", "test-password" },
        new object[] { "hy2", "test-password" },
        new object[] { "tuic", "test-user:test-password" },
        new object[] { "ss", "aes-128-gcm:test-password" }
    };

    [Theory]
    [MemberData(nameof(ShareLinks))]
    public void Parse_Ipv6AndValidUriPath_PreservesEndpointAndFragment(string scheme, string userInfo)
    {
        var entry = ServerUriParser.Parse(
            $"{scheme}://{userInfo}@[2001:db8::1]:8443/provider/path?security=tls#ipv6%20node+one");

        Assert.Equal("2001:db8::1", entry.Server);
        Assert.Equal(8443, entry.Port);
        Assert.Equal("ipv6 node+one", entry.Name);
    }

    [Theory]
    [InlineData("", 443)]
    [InlineData(":0", 443)]
    [InlineData(":1", 1)]
    [InlineData(":65535", 65535)]
    public void Parse_PortBoundaries_MatchStdlibDefaults(string port, int expected)
    {
        foreach (var link in ShareLinks)
        {
            var entry = ServerUriParser.Parse($"{link[0]}://{link[1]}@example.com{port}");
            Assert.Equal("example.com", entry.Server);
            Assert.Equal(expected, entry.Port);
        }
    }

    [Theory]
    [MemberData(nameof(ShareLinks))]
    public void Parse_QueryPlusAndEscapes_UseFormDecoding(string scheme, string userInfo)
    {
        var entry = ServerUriParser.Parse(
            $"{scheme}://{userInfo}@example.com?SNI=raw+plus%2Bencoded%20space&plugin=raw+plus%2Bencoded%20space");

        const string expected = "raw plus+encoded space";
        if (scheme == "ss")
            Assert.Equal(expected, entry.Plugin);
        else
            Assert.Equal(expected, entry.Tls!.ServerName);
    }

    [Theory]
    [InlineData("vless", "test-user")]
    [InlineData("tuic", "test-user:test-password")]
    public void Parse_RepeatedAlpnAndAllowInsecure_PreservesCombinedValues(string scheme, string userInfo)
    {
        var entry = ServerUriParser.Parse(
            $"{scheme}://{userInfo}@example.com?security=tls&alpn=h2&ALPN=http%2F1.1&allowInsecure=0&allowInsecure=1");

        Assert.NotNull(entry.Tls);
        Assert.Equal("h2,http/1.1", entry.Tls.Alpn);
        Assert.False(entry.Tls.Insecure);
    }

    [Theory]
    [InlineData("hysteria2")]
    [InlineData("hy2")]
    public void Parse_HysteriaRepeatedAllowInsecure_DoesNotEnableInsecureTls(string scheme)
    {
        var entry = ServerUriParser.Parse(
            $"{scheme}://test-password@example.com?allowInsecure=0&allowInsecure=1");

        Assert.NotNull(entry.Tls);
        Assert.False(entry.Tls.Insecure);
    }

    [Fact]
    public void Parse_VlessCaseInsensitiveQuery_PreservesTransportAndMetadata()
    {
        var entry = VlessUriParser.Parse(
            "vless://test-user@example.com?Security=tls&TYPE=ws&PATH=%2Fraw+plus%2B&OUTBOUND=entry+one&DETOUR=upstream%2Btwo");

        Assert.Equal("tls", entry.Security);
        Assert.Equal("ws", entry.Transport!.Type);
        Assert.Equal("/raw plus+", entry.Transport.Path);
        Assert.Equal("entry one", entry.OutboundId);
        Assert.Equal("upstream+two", entry.DetourVia);
    }

    [Theory]
    [InlineData("example.com:port-secret-marker")]
    [InlineData("example.com:70000")]
    [InlineData("[2001:db8::1")]
    [InlineData("[2001:db8::1]bracket-secret-marker")]
    public void Parse_MalformedAuthority_ThrowsGenericErrorAndLogsNoSecrets(string authority)
    {
        foreach (var link in ShareLinks)
        {
            var scheme = (string)link[0];
            var uri = $"{scheme}://{link[1]}@{authority}?sni=query-secret-marker#fragment-secret-marker";
            var error = Assert.Throws<FormatException>(() => ServerUriParser.Parse(uri));
            var expectedScheme = scheme == "vless" ? "VLESS" : scheme == "hy2" ? "hysteria2" : scheme;
            Assert.Equal($"Invalid {expectedScheme} URI: cannot parse", error.Message);
            Assert.Null(error.InnerException);

            var sink = new CapturingSink();
            using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
            Assert.Empty(SubscriptionFetcher.ParseBody(uri, logger));
            var warning = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Warning, warning.Level);
            Assert.Equal(error.Message, warning.Exception!.Message);
            var logged = warning.RenderMessage() + "\n" + warning.Exception;
            Assert.DoesNotContain((string)link[1], logged);
            Assert.DoesNotContain("port-secret-marker", logged);
            Assert.DoesNotContain("bracket-secret-marker", logged);
            Assert.DoesNotContain("query-secret-marker", logged);
            Assert.DoesNotContain("fragment-secret-marker", logged);
        }
    }

    [Theory]
    [InlineData("vless://test", true)]
    [InlineData("VLESS://test", true)]
    [InlineData("hysteria2://test", true)]
    [InlineData("hy2://test", true)]
    [InlineData("tuic://test", true)]
    [InlineData("ss://test", true)]
    [InlineData("http://test", false)]
    [InlineData("vmess://test", false)]
    [InlineData("unsupported://test", false)]
    [InlineData(" vless://test", false)]
    [InlineData("", false)]
    public void IsSupportedScheme_SpanAndStringOverloadsAgree(string line, bool expected)
    {
        Assert.Equal(expected, ServerUriParser.IsSupportedScheme(line));
        Assert.Equal(expected, ServerUriParser.IsSupportedScheme(line.AsSpan()));
    }

    [Theory]
    [InlineData("naive://test")]
    [InlineData("naive+https://test")]
    [InlineData("naive+quic://test")]
    [InlineData("dns-tunnel://test")]
    [InlineData("awg://test")]
    [InlineData("amneziawg://test")]
    public void IsSupportedScheme_SpanPreservesRuntimeGates(string line)
    {
        var expected = line.StartsWith("naive", StringComparison.Ordinal)
            ? ServerUriParser.NaiveRuntimeAvailable
            : line.StartsWith("dns-tunnel", StringComparison.Ordinal)
                ? ServerUriParser.SlipstreamRuntimeAvailable
                : SingBoxFeatures.AwgAvailable;

        Assert.Equal(expected, ServerUriParser.IsSupportedScheme(line));
        Assert.Equal(expected, ServerUriParser.IsSupportedScheme(line.AsSpan()));
    }

    [Fact]
    public void ParseMultiple_PrefilterSkipsUnsupportedAndMalformedLines()
    {
        const string text = " \r\nhttps://unsupported.example\r\n  VLESS://test-user@example.com:8443#first \r\n"
            + "vless://test-user@example.com:70000\nss://aes-128-gcm:test-password@example.net#second\n";

        var entries = ServerUriParser.ParseMultiple(text);

        Assert.Equal(new[] { "first", "second" }, entries.Select(entry => entry.Name));
        Assert.Equal(8443, entries[0].Port);
        Assert.Equal("shadowsocks", entries[1].Protocol);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
