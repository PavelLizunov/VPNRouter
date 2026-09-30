using System.Text;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

[Collection("SingBoxFeaturesSerial")]
public sealed class SingBoxJsonSubscriptionTests : IDisposable
{
    private const string Uuid1 = "00000000-0000-4000-8000-000000000001";
    private const string Uuid2 = "00000000-0000-4000-8000-000000000002";
    private const string FakePbk = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string Selector = "{\"type\":\"selector\",\"tag\":\"proxy\",\"outbounds\":[\"a\",\"b\"]}";
    private const string UrlTest = "{\"type\":\"urltest\",\"tag\":\"auto\",\"outbounds\":[\"a\",\"b\"]}";
    private const string Direct = "{\"type\":\"direct\",\"tag\":\"direct\"}";
    private const string Block = "{\"type\":\"block\",\"tag\":\"block\"}";
    private const string DnsOut = "{\"type\":\"dns\",\"tag\":\"dns-out\"}";

    public SingBoxJsonSubscriptionTests() => SingBoxFeatures.OverrideXhttp = false;

    public void Dispose() => SingBoxFeatures.ResetForTests();

    private static string Config(params string[] outbounds) =>
        "{\"log\":{\"level\":\"info\"},\"outbounds\":[" + string.Join(",", outbounds) +
        "],\"route\":{\"final\":\"proxy\"}}";

    private static string Hy2(string tag = "Node HY2 ~probe", string server = "203.0.113.10", string port = "8444",
        string password = "test-password-1", string extra = "") =>
        "{\"type\":\"hysteria2\",\"tag\":\"" + tag + "\",\"server\":\"" + server + "\",\"server_port\":" + port +
        ",\"password\":\"" + password + "\",\"obfs\":{\"type\":\"salamander\",\"password\":\"test-obfs-1\"}," +
        "\"tls\":{\"enabled\":true,\"alpn\":[\"h3\"],\"insecure\":false}" + extra + "}";

    private static string VlessReality(string tag = "Node VLESS ~probe", string server = "203.0.113.11",
        string port = "443", string uuid = Uuid1, string transport = "") =>
        "{\"type\":\"vless\",\"tag\":\"" + tag + "\",\"server\":\"" + server + "\",\"server_port\":" + port +
        ",\"uuid\":\"" + uuid + "\",\"flow\":\"xtls-rprx-vision\",\"packet_encoding\":\"xudp\"," +
        "\"tls\":{\"enabled\":true,\"server_name\":\"reality.example.com\"," +
        "\"utls\":{\"enabled\":true,\"fingerprint\":\"chrome\"}," +
        "\"reality\":{\"enabled\":true,\"public_key\":\"" + FakePbk + "\",\"short_id\":\"0123abcd\"}}" +
        (transport.Length == 0 ? string.Empty : ",\"transport\":" + transport) + "}";

    private static string XhttpTransport =>
        "{\"type\":\"xhttp\",\"mode\":\"packet-up\",\"path\":\"/p\",\"host\":\"cdn.example.com\",\"x_padding_bytes\":\"100-1000\"}";

    [Fact]
    public void ParseBody_RawSingBoxJson_MapsVlessRealityAndHysteria2()
    {
        var body = Config(Selector, Hy2(), VlessReality(), Direct, Block);

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);

        var hy2 = Assert.Single(result, e => e.Protocol == "hysteria2");
        Assert.Equal("Node HY2 ~probe", hy2.Name);
        Assert.Equal("203.0.113.10", hy2.Server);
        Assert.Equal(8444, hy2.Port);
        Assert.Equal("test-password-1", hy2.Password);
        Assert.Equal("salamander", hy2.ObfsType);
        Assert.Equal("test-obfs-1", hy2.ObfsPassword);
        Assert.Equal("203.0.113.10", hy2.Tls.ServerName);
        Assert.False(hy2.Tls.Insecure);

        var vless = Assert.Single(result, e => e.Protocol == "vless");
        Assert.Equal("Node VLESS ~probe", vless.Name);
        Assert.Equal("203.0.113.11", vless.Server);
        Assert.Equal(443, vless.Port);
        Assert.Equal(Uuid1, vless.Uuid);
        Assert.Equal("xtls-rprx-vision", vless.Flow);
        Assert.Equal("reality", vless.Security);
        Assert.True(vless.Reality.Enabled);
        Assert.Equal("reality.example.com", vless.Reality.ServerName);
        Assert.Equal("chrome", vless.Reality.Fingerprint);
        Assert.Equal(FakePbk, vless.Reality.PublicKey);
        Assert.Equal("0123abcd", vless.Reality.ShortId);
        Assert.Equal("tcp", vless.Transport.Type);
    }

    [Fact]
    public void ParseBody_XhttpOutbound_IsDroppedWhenSingBoxHasNoXhttp()
    {
        SingBoxFeatures.OverrideXhttp = false;
        var body = Config(VlessReality("Node XHTTP", "203.0.113.12", "9443", Uuid2, XhttpTransport));

        Assert.Empty(SubscriptionFetcher.ParseBody(body));
    }

    [Fact]
    public void ParseBody_XhttpOutbound_IsMappedWhenSingBoxHasXhttp()
    {
        SingBoxFeatures.OverrideXhttp = true;
        var body = Config(VlessReality("Node XHTTP", "203.0.113.12", "9443", Uuid2, XhttpTransport));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("xhttp", e.Transport.Type);
        Assert.Equal("packet-up", e.Transport.Mode);
        Assert.Equal("/p", e.Transport.Path);
        Assert.Equal("cdn.example.com", e.Transport.Host);
        Assert.Equal("100-1000", e.Transport.XPaddingBytes);
        Assert.Equal("reality", e.Security);
    }

    [Fact]
    public void ParseBody_StructuralAndUnsupportedOutbounds_AreIgnored()
    {
        var body = Config(
            Selector, UrlTest, Direct, Block, DnsOut,
            "{\"type\":\"trojan\",\"tag\":\"t\",\"server\":\"203.0.113.30\",\"server_port\":443,\"password\":\"x\"}",
            "{\"type\":\"vmess\",\"tag\":\"v\",\"server\":\"203.0.113.31\",\"server_port\":443,\"uuid\":\"" + Uuid1 + "\"}",
            "{\"type\":\"wireguard\",\"tag\":\"w\",\"server\":\"203.0.113.32\",\"server_port\":51820}");

        Assert.Empty(SubscriptionFetcher.ParseBody(body));
    }

    [Fact]
    public void ParseBody_WsTransport_MapsPathAndHostHeader()
    {
        var wsString = "{\"type\":\"ws\",\"path\":\"/ws\",\"headers\":{\"Host\":\"ws.example.com\"}}";
        var wsArray = "{\"type\":\"ws\",\"path\":\"/ws2\",\"headers\":{\"host\":[\"ws2.example.com\"]}}";
        var body = Config(
            VlessReality("ws-a", "203.0.113.40", "443", Uuid1, wsString),
            VlessReality("ws-b", "203.0.113.41", "443", Uuid2, wsArray));

        var result = SubscriptionFetcher.ParseBody(body);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("ws", e.Transport.Type));
        Assert.Equal("/ws", result[0].Transport.Path);
        Assert.Equal("ws.example.com", result[0].Transport.Headers["Host"]);
        Assert.Equal("/ws2", result[1].Transport.Path);
        Assert.Equal("ws2.example.com", result[1].Transport.Headers["Host"]);
    }

    [Fact]
    public void ParseBody_GrpcTransport_MapsServiceName()
    {
        var body = Config(VlessReality("grpc", "203.0.113.42", "443", Uuid1,
            "{\"type\":\"grpc\",\"service_name\":\"svc-name\"}"));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("grpc", e.Transport.Type);
        Assert.Equal("svc-name", e.Transport.Path);
    }

    [Fact]
    public void ParseBody_UnsupportedTransport_SkipsOnlyThatOutbound()
    {
        var body = Config(
            VlessReality("quic", "203.0.113.43", "443", Uuid1, "{\"type\":\"quic\"}"),
            VlessReality("httpupgrade", "203.0.113.44", "443", Uuid1, "{\"type\":\"httpupgrade\",\"path\":\"/u\"}"),
            VlessReality("plain", "203.0.113.45"));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("plain", e.Name);
    }

    [Fact]
    public void ParseBody_VlessWithPlainTls_MapsSniAlpnFingerprintAndInsecure()
    {
        var ob = "{\"type\":\"vless\",\"tag\":\"tls-node\",\"server\":\"203.0.113.46\",\"server_port\":443," +
                 "\"uuid\":\"" + Uuid1 + "\",\"tls\":{\"enabled\":true,\"server_name\":\"tls.example.com\"," +
                 "\"alpn\":[\"h2\",\"http/1.1\"],\"insecure\":true,\"utls\":{\"enabled\":true,\"fingerprint\":\"firefox\"}}}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("tls", e.Security);
        Assert.Equal("tls.example.com", e.Tls.ServerName);
        Assert.Equal("h2,http/1.1", e.Tls.Alpn);
        Assert.Equal("firefox", e.Tls.Fingerprint);
        Assert.True(e.Tls.Insecure);
    }

    [Fact]
    public void ParseBody_VlessWithoutTls_UsesSecurityNone()
    {
        var ob = "{\"type\":\"vless\",\"tag\":\"clear\",\"server\":\"203.0.113.47\",\"server_port\":80,\"uuid\":\"" +
                 Uuid1 + "\"}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("none", e.Security);
        Assert.Equal(80, e.Port);
    }

    [Fact]
    public void ParseBody_Hysteria2_MapsSniInsecureAndBandwidth()
    {
        var ob = "{\"type\":\"hysteria2\",\"tag\":\"hy2-full\",\"server\":\"203.0.113.48\",\"server_port\":443," +
                 "\"password\":\"test-password-2\",\"up_mbps\":50,\"down_mbps\":200," +
                 "\"tls\":{\"enabled\":true,\"server_name\":\"hy2.example.com\",\"insecure\":true}}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("hy2.example.com", e.Tls.ServerName);
        Assert.True(e.Tls.Insecure);
        Assert.Equal(50, e.HysteriaUpMbps);
        Assert.Equal(200, e.HysteriaDownMbps);
        Assert.Equal(string.Empty, e.ObfsType);
    }

    [Fact]
    public void ParseBody_Tuic_MapsCredentialsAndTransportOptions()
    {
        var ob = "{\"type\":\"tuic\",\"tag\":\"tuic-node\",\"server\":\"203.0.113.49\",\"server_port\":4443," +
                 "\"uuid\":\"" + Uuid1 + "\",\"password\":\"test-password-3\",\"congestion_control\":\"cubic\"," +
                 "\"udp_relay_mode\":\"quic\",\"tls\":{\"enabled\":true,\"server_name\":\"tuic.example.com\"," +
                 "\"alpn\":[\"h3\"],\"insecure\":true}}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("tuic", e.Protocol);
        Assert.Equal(Uuid1, e.Uuid);
        Assert.Equal("test-password-3", e.Password);
        Assert.Equal("cubic", e.CongestionControl);
        Assert.Equal("quic", e.UdpRelayMode);
        Assert.Equal("tuic.example.com", e.Tls.ServerName);
        Assert.Equal("h3", e.Tls.Alpn);
        Assert.True(e.Tls.Insecure);
    }

    [Fact]
    public void ParseBody_Shadowsocks_MapsMethodPasswordAndPlugin()
    {
        var ob = "{\"type\":\"shadowsocks\",\"tag\":\"ss-node\",\"server\":\"203.0.113.50\",\"server_port\":8388," +
                 "\"method\":\"2022-blake3-aes-128-gcm\",\"password\":\"p@ss:word/1\"," +
                 "\"plugin\":\"obfs-local\",\"plugin_opts\":\"obfs=http;obfs-host=example.com\"}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("shadowsocks", e.Protocol);
        Assert.Equal("2022-blake3-aes-128-gcm", e.Method);
        Assert.Equal("p@ss:word/1", e.Password);
        Assert.Equal("obfs-local", e.Plugin);
        Assert.Equal("obfs=http;obfs-host=example.com", e.PluginOpts);
    }

    [Fact]
    public void ParseBody_MissingOrInvalidFields_SkipOnlyThoseOutbounds()
    {
        var body = Config(
            "{\"type\":\"vless\",\"tag\":\"no-uuid\",\"server\":\"203.0.113.60\",\"server_port\":443}",
            "{\"type\":\"vless\",\"tag\":\"no-server\",\"server_port\":443,\"uuid\":\"" + Uuid1 + "\"}",
            "{\"type\":\"hysteria2\",\"tag\":\"no-password\",\"server\":\"203.0.113.61\",\"server_port\":443}",
            "{\"type\":\"shadowsocks\",\"tag\":\"no-method\",\"server\":\"203.0.113.62\",\"server_port\":443,\"password\":\"x\"}",
            Hy2("port-zero", "203.0.113.63", "0"),
            Hy2("port-huge", "203.0.113.64", "70000"),
            Hy2("port-text", "203.0.113.65", "\"abc\""),
            "{\"type\":\"hysteria2\",\"tag\":\"no-port\",\"server\":\"203.0.113.66\",\"password\":\"x\"}",
            "42",
            "{\"tag\":\"no-type\"}",
            Hy2("kept", "203.0.113.67"));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("kept", e.Name);
    }

    [Fact]
    public void ParseBody_ServerPortGivenAsString_IsAccepted()
    {
        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(Hy2("str-port", "203.0.113.68", "\"8445\""))));

        Assert.Equal(8445, e.Port);
    }

    [Fact]
    public void ParseBody_MissingTag_FallsBackToServerAddress()
    {
        var ob = "{\"type\":\"hysteria2\",\"server\":\"203.0.113.69\",\"server_port\":443,\"password\":\"x\"}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(ob)));

        Assert.Equal("203.0.113.69", e.Name);
    }

    [Fact]
    public void ParseBody_SpecialCharactersInTagAndPassword_RoundTrip()
    {
        const string tag = "A & B #1 %50 ~x é";
        const string password = "p@ss:w/rd#1&x=y+z";
        var body = Config(Hy2(tag, "203.0.113.70", "443", password));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal(tag, e.Name);
        Assert.Equal(password, e.Password);
    }

    [Fact]
    public void ParseBody_Ipv6Server_IsBracketedForTheLinkAndParsedBack()
    {
        var e = Assert.Single(SubscriptionFetcher.ParseBody(Config(Hy2("v6", "2001:db8::1", "443"))));

        Assert.Equal("2001:db8::1", e.Server);
        Assert.Equal(443, e.Port);
    }

    [Fact]
    public void ParseBody_ConfigWrapperWinsOverOutbounds()
    {
        var inner = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            "hysteria2://test-password-9@203.0.113.80:443?sni=wrapper.example.com#wrapper-node"));
        var body = "{\"config\":\"" + inner + "\",\"outbounds\":[" + Hy2("from-outbounds", "203.0.113.81") + "]}";

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("wrapper-node", e.Name);
        Assert.Equal("203.0.113.80", e.Server);
    }

    [Theory]
    [InlineData("{\"outbounds\":[]}")]
    [InlineData("{\"outbounds\":{}}")]
    [InlineData("{\"outbounds\":\"none\"}")]
    [InlineData("{\"outbounds\":[{\"type\":\"vless\"")]
    [InlineData("{\"outbounds\":[null,1,\"x\",[]]}")]
    public void ParseBody_EmptyOrMalformedOutbounds_ReturnEmptyWithoutThrowing(string body)
    {
        Assert.Empty(SubscriptionFetcher.ParseBody(body));
    }

    [Fact]
    public void ParseBody_KnownPlaceholderCredentials_AreCountedAsDropped()
    {
        var ob = VlessReality("placeholder").Replace(FakePbk, "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU");

        var result = SubscriptionFetcher.ParseBody(Config(ob, Hy2("real", "203.0.113.90")), out var dropped);

        Assert.Equal(1, dropped);
        Assert.Equal("real", Assert.Single(result).Name);
    }

    [Fact]
    public void ParseBody_DuplicateOutbounds_AreDeduplicatedByExistingRules()
    {
        var body = Config(Hy2("first", "203.0.113.91"), Hy2("second", "203.0.113.91"));

        var e = Assert.Single(SubscriptionFetcher.ParseBody(body));

        Assert.Equal("first", e.Name);
    }

    [Fact]
    public void ParseOutboundsToUris_ReturnsOnlySupportedShareLinks()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Config(Selector, Hy2(), VlessReality(), Direct));

        var uris = SingBoxJsonSubscription.ParseOutboundsToUris(doc.RootElement);

        Assert.Equal(2, uris.Count);
        Assert.All(uris, u => Assert.True(ServerUriParser.IsSupportedScheme(u)));
        Assert.StartsWith("hysteria2://", uris[0]);
        Assert.StartsWith("vless://", uris[1]);
    }

    [Fact]
    public void ParseBody_ExistingFormats_AreUnchanged()
    {
        const string uri1 = "vless://uuid1@server1.example:443?security=tls&type=tcp#one";
        const string uri2 = "hysteria2://test-password-5@203.0.113.95:443?sni=hy.example.com#two";

        var plain = SubscriptionFetcher.ParseBody(uri1 + "\n" + uri2 + "\n");
        var b64 = SubscriptionFetcher.ParseBody(Convert.ToBase64String(Encoding.UTF8.GetBytes(uri1 + "\n" + uri2)));
        var wrapped = SubscriptionFetcher.ParseBody(
            "{\"config\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri1 + "\n" + uri2)) + "\"}");
        var clash = SubscriptionFetcher.ParseBody(
            "proxies:\n  - name: clash-node\n    type: hysteria2\n    server: 203.0.113.96\n    port: 8444\n    password: test-password-6\n");

        Assert.Equal(2, plain.Count);
        Assert.Equal(2, b64.Count);
        Assert.Equal(2, wrapped.Count);
        Assert.Equal("clash-node", Assert.Single(clash).Name);
        Assert.Empty(SubscriptionFetcher.ParseBody("{\"servers\":[]}"));
    }
}
