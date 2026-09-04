using System;
using System.Collections.Generic;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class PerformanceShareLinkTests
{
    [Fact]
    public void ShareLinkHelper_ParseComponents_ParsesStandardVless()
    {
        var uri = "vless://00000000-0000-0000-0000-000000000001@198.51.100.1:443?type=tcp&security=reality&sni=example.com#node1";
        var span = uri.AsSpan("vless://".Length);

        ShareLinkHelper.ParseComponents(span, out var userinfo, out var host, out var port, out var query, out var name);

        Assert.Equal("00000000-0000-0000-0000-000000000001", userinfo.ToString());
        Assert.Equal("198.51.100.1", host);
        Assert.Equal(443, port);
        Assert.Equal("type=tcp&security=reality&sni=example.com", query.ToString());
        Assert.Equal("node1", name);
    }

    [Fact]
    public void ShareLinkHelper_ParseComponents_ParsesIpv6BracketsAndPort()
    {
        var uri = "vless://uuid@[2001:db8::1]:8443?security=tls#ipv6-node";
        var span = uri.AsSpan("vless://".Length);

        ShareLinkHelper.ParseComponents(span, out var userinfo, out var host, out var port, out var query, out var name);

        Assert.Equal("uuid", userinfo.ToString());
        Assert.Equal("2001:db8::1", host);
        Assert.Equal(8443, port);
        Assert.Equal("security=tls", query.ToString());
        Assert.Equal("ipv6-node", name);
    }

    [Fact]
    public void ShareLinkHelper_ParseComponents_InvalidPort_ThrowsFormatException()
    {
        var uri = "vless://uuid@example.com:70000#bad-port";
        Assert.Throws<FormatException>(() =>
            ServerUriParser.Parse(uri));
    }

    [Fact]
    public void ShareLinkHelper_ParseQuery_ExtractsCaseInsensitiveValues()
    {
        var queryStr = "Security=reality&SNI=example.com&flow=xtls-rprx-vision&EMPTY=&FLAG";
        var q = ShareLinkHelper.ParseQuery(queryStr.AsSpan());

        Assert.Equal("reality", q["security"]);
        Assert.Equal("example.com", q["sni"]);
        Assert.Equal("xtls-rprx-vision", q["FLOW"]);
        Assert.Equal(string.Empty, q["empty"]);
        Assert.Equal(string.Empty, q["flag"]);
        Assert.Null(q["nonexistent"]);
    }

    [Fact]
    public void ServerUriParser_IsSupportedScheme_SpanOverload_MatchesStringOverload()
    {
        Assert.True(ServerUriParser.IsSupportedScheme("vless://test".AsSpan()));
        Assert.True(ServerUriParser.IsSupportedScheme("hysteria2://test".AsSpan()));
        Assert.True(ServerUriParser.IsSupportedScheme("hy2://test".AsSpan()));
        Assert.True(ServerUriParser.IsSupportedScheme("tuic://test".AsSpan()));
        Assert.True(ServerUriParser.IsSupportedScheme("ss://test".AsSpan()));

        Assert.False(ServerUriParser.IsSupportedScheme("http://test".AsSpan()));
        Assert.False(ServerUriParser.IsSupportedScheme("vmess://test".AsSpan()));
        Assert.False(ServerUriParser.IsSupportedScheme("unsupported://test".AsSpan()));
    }
}
