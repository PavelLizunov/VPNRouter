#nullable enable

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class VlessDeepVerifierTests
{
    internal static VlessServerEntry CleanVlessEntry() => new()
    {
        Name = "test-server",
        Protocol = "vless",
        Server = "example.com",
        Port = 443,
        Uuid = "abcd1234-5678-90ab-cdef-1234567890ab",
        Flow = "xtls-rprx-vision",
        Reality = new VlessRealityConfig
        {
            Enabled = true,
            ServerName = "yahoo.com",
            Fingerprint = "chrome",
            PublicKey = "vJgL_realPubkey_definitelyNotPlaceholder_xY9q",
            ShortId = "deadbeef",
        },
    };

    [Theory]
    [InlineData(1, 1500)]
    [InlineData(5, 2700)]
    [InlineData(8, 3600)]
    public void EffectiveSocksBindWait_ScalesWithConcurrency(int concurrency, int expectedMs)
    {
        var v = new VlessDeepVerifier(Serilog.Log.Logger) { MaxConcurrency = concurrency };
        Assert.Equal(expectedMs, (int)v.EffectiveSocksBindWait.TotalMilliseconds);
    }

    [Fact]
    public void BuildSingleOutboundConfig_HappyPathVless_ProducesValidShape()
    {
        var entry = CleanVlessEntry();
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, socksPort: 10808, clashPort: 9090);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var inbounds = root.GetProperty("inbounds");
        Assert.Equal(1, inbounds.GetArrayLength());
        var socksIn = inbounds[0];
        Assert.Equal("socks", socksIn.GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", socksIn.GetProperty("listen").GetString());
        Assert.Equal(10808, socksIn.GetProperty("listen_port").GetInt32());

        var outbounds = root.GetProperty("outbounds");
        Assert.Equal(2, outbounds.GetArrayLength());
        Assert.Equal("vless", outbounds[0].GetProperty("type").GetString());
        Assert.Equal("proxy", outbounds[0].GetProperty("tag").GetString());
        Assert.Equal("direct", outbounds[1].GetProperty("type").GetString());
        Assert.Equal("dns-direct-out", outbounds[1].GetProperty("tag").GetString());

        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());

        var clash = root.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal($"127.0.0.1:9090", clash.GetProperty("external_controller").GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_VlessRealityCredentials_FlowDownToProxyOutbound()
    {
        var entry = CleanVlessEntry();
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);

        using var doc = JsonDocument.Parse(json);
        var proxy = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("example.com", proxy.GetProperty("server").GetString());
        Assert.Equal(443, proxy.GetProperty("server_port").GetInt32());
        Assert.Equal("abcd1234-5678-90ab-cdef-1234567890ab", proxy.GetProperty("uuid").GetString());
        Assert.Equal("xtls-rprx-vision", proxy.GetProperty("flow").GetString());

        var reality = proxy.GetProperty("tls").GetProperty("reality");
        Assert.True(reality.GetProperty("enabled").GetBoolean());
        Assert.Equal("vJgL_realPubkey_definitelyNotPlaceholder_xY9q",
            reality.GetProperty("public_key").GetString());
        Assert.Equal("deadbeef", reality.GetProperty("short_id").GetString());

        var utls = proxy.GetProperty("tls").GetProperty("utls");
        Assert.True(utls.GetProperty("enabled").GetBoolean());
        Assert.Equal("chrome", utls.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_Hysteria2Protocol_DispatchesToHysteria2Builder()
    {
        var entry = new VlessServerEntry
        {
            Name = "hy2-test",
            Protocol = "hysteria2",
            Server = "h2.example.com",
            Port = 443,
            Password = "auth-password",
            Tls = new VlessTlsConfig
            {
                Enabled = true,
                ServerName = "h2.example.com",
                Insecure = false,
            },
        };

        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);
        using var doc = JsonDocument.Parse(json);
        var proxy = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("hysteria2", proxy.GetProperty("type").GetString());
        Assert.Equal("auth-password", proxy.GetProperty("password").GetString());

        var alpn = proxy.GetProperty("tls").GetProperty("alpn");
        Assert.Equal(1, alpn.GetArrayLength());
        Assert.Equal("h3", alpn[0].GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_TuicProtocol_DispatchesToTuicBuilder()
    {
        var entry = new VlessServerEntry
        {
            Name = "tuic-test",
            Protocol = "tuic",
            Server = "tuic.example.com",
            Port = 443,
            Uuid = "tuic-uuid-abcd",
            Password = "tuic-password",
            CongestionControl = "bbr",
            UdpRelayMode = "native",
            Tls = new VlessTlsConfig { Enabled = true, ServerName = "tuic.example.com" },
        };

        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);
        using var doc = JsonDocument.Parse(json);
        var proxy = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("tuic", proxy.GetProperty("type").GetString());
        Assert.Equal("tuic-uuid-abcd", proxy.GetProperty("uuid").GetString());
        Assert.Equal("tuic-password", proxy.GetProperty("password").GetString());
        Assert.Equal("bbr", proxy.GetProperty("congestion_control").GetString());
        Assert.Equal("native", proxy.GetProperty("udp_relay_mode").GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_ShadowsocksProtocol_DispatchesToShadowsocksBuilder()
    {
        var entry = new VlessServerEntry
        {
            Name = "ss-test",
            Protocol = "shadowsocks",
            Server = "ss.example.com",
            Port = 8388,
            Method = "2022-blake3-aes-256-gcm",
            Password = "ss-password",
        };

        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, 10808, 9090);
        using var doc = JsonDocument.Parse(json);
        var proxy = doc.RootElement.GetProperty("outbounds")[0];

        Assert.Equal("shadowsocks", proxy.GetProperty("type").GetString());
        Assert.Equal("2022-blake3-aes-256-gcm", proxy.GetProperty("method").GetString());
        Assert.Equal("ss-password", proxy.GetProperty("password").GetString());
    }

    [Fact]
    public void BuildVlessOutbound_TransportWs_AppliesWebsocketShape()
    {
        var entry = CleanVlessEntry();
        entry.Transport = new VlessTransportConfig { Type = "ws", Path = "/vlessws" };

        var outbound = VlessDeepVerifier.BuildVlessOutbound(entry);

        Assert.Equal("vless", outbound["type"]!.GetValue<string>());
        var transport = outbound["transport"] as JsonObject;
        Assert.NotNull(transport);
        Assert.Equal("ws", transport!["type"]!.GetValue<string>());
        Assert.Equal("/vlessws", transport["path"]!.GetValue<string>());
    }

    [Fact]
    public void FindFreePort_ReturnsHighEphemeralPort()
    {
        var port = NetPortUtil.FindFreePort();
        Assert.InRange(port, 1, 65535);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.5.42", true)]
    [InlineData("172.31.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("1.1.1.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    public void IsPrivateOrLoopback_ClassifiesIpsCorrectly(string ipString, bool expected)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.Equal(expected, DeepVerifyProbe.IsPrivateOrLoopback(ip));
    }

    [Fact]
    public void TrimSnippet_LongInput_TruncatesWithEllipsis()
    {
        var verbose = string.Join('\n', new[] { "line one of stderr", "line two with more", "line three more text" });
        var snip = DeepVerifyProbe.TrimSnippet(verbose, 20);

        Assert.True(snip.Length <= 21);
        Assert.DoesNotContain('\n', snip);
        Assert.DoesNotContain('\r', snip);
        Assert.EndsWith("…", snip);
    }

    [Fact]
    public void TrimSnippet_ShortInput_NoEllipsis()
    {
        var snip = DeepVerifyProbe.TrimSnippet("short", 80);
        Assert.Equal("short", snip);
        Assert.DoesNotContain("…", snip);
    }
}
