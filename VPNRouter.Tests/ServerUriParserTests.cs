using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.EmergencyChannel;

namespace VPNRouter.Tests;
// ═══════════════════════════════════════════════════════════════════════════════
// ServerUriParser — v2.30.1-r3 multi-protocol URI parsing
//
// Verifies that share-link URIs for non-VLESS protocols (Hysteria2 with
// Salamander obfuscation, TUIC v5 with congestion-control hint, Shadowsocks
// 2022 in both plain and base64 userinfo forms, Shadowsocks + ShadowTLS v3
// plugin) parse into VlessServerEntry rows with the right Protocol
// discriminator and protocol-specific fields. The pre-existing VLESS path
// keeps working unchanged.
// ═══════════════════════════════════════════════════════════════════════════════

public class ServerUriParserTests
{
    [Fact]
    public void Vless_BackwardCompat_ParsesAndKeepsProtocolDefault()
    {
        var uri = "vless://abc-123@1.2.3.4:443?type=tcp&security=reality&sni=example.com&pbk=PUB&sid=ID&flow=xtls-rprx-vision#main";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("vless", e.Protocol);
        Assert.Equal("1.2.3.4", e.Server);
        Assert.Equal(443, e.Port);
        Assert.Equal("abc-123", e.Uuid);
        Assert.Equal("xtls-rprx-vision", e.Flow);
        Assert.Equal("PUB", e.Reality.PublicKey);
    }

    [Fact]
    public void Hysteria2_Plain_ParsesCorrectly()
    {
        var uri = "hysteria2://mypass@example.com:9443/?sni=example.com&insecure=0#main";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal("example.com", e.Server);
        Assert.Equal(9443, e.Port);
        Assert.Equal("mypass", e.Password);
        Assert.Equal("example.com", e.Tls.ServerName);
        Assert.False(e.Tls.Insecure);
        Assert.Equal(string.Empty, e.ObfsType);
    }

    [Fact]
    public void Hysteria2_Salamander_PopulatesObfsFields()
    {
        var uri = "hysteria2://pass@host:443/?sni=foo.com&obfs=salamander&obfs-password=obfspw#hy2";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.Equal("salamander", e.ObfsType);
        Assert.Equal("obfspw", e.ObfsPassword);
    }

    [Fact]
    public void Hysteria2_Hy2Alias_ParsesAsHysteria2()
    {
        var uri = "hy2://pw@1.2.3.4:443/?sni=x.com&insecure=1#alias";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("hysteria2", e.Protocol);
        Assert.True(e.Tls.Insecure);
    }

    [Fact]
    public void Tuic_UuidPasswordUserinfo_ParsesBoth()
    {
        var uri = "tuic://u-uid:pass-word@host:443?sni=foo.com&congestion_control=cubic&udp_relay_mode=quic&alpn=h3#tuic";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("tuic", e.Protocol);
        Assert.Equal("u-uid", e.Uuid);
        Assert.Equal("pass-word", e.Password);
        Assert.Equal("cubic", e.CongestionControl);
        Assert.Equal("quic", e.UdpRelayMode);
        Assert.Equal("h3", e.Tls.Alpn);
    }

    [Fact]
    public void Tuic_UuidOnly_AcceptsEmptyPassword()
    {
        var uri = "tuic://just-uuid@host:443#tuic";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("just-uuid", e.Uuid);
        Assert.Equal(string.Empty, e.Password);
    }

    [Fact]
    public void Shadowsocks_PlainUserinfo_ParsesMethodAndPassword()
    {
        var uri = "ss://2022-blake3-aes-256-gcm:secret-key@host:8388#ss22";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("shadowsocks", e.Protocol);
        Assert.Equal("2022-blake3-aes-256-gcm", e.Method);
        Assert.Equal("secret-key", e.Password);
    }

    [Fact]
    public void Shadowsocks_Base64Userinfo_DecodesAndParses()
    {
        // base64 of "aes-256-gcm:secretpw"
        var ui = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:secretpw"));
        var uri = "ss://" + ui + "@host:8388#ss-legacy";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("aes-256-gcm", e.Method);
        Assert.Equal("secretpw", e.Password);
    }

    [Fact]
    public void Shadowsocks_Base64UrlUserinfo_DecodesAndParses()
    {
        // Regression (v2.44.1): SIP002 and our Clash-YAML emitter
        // (ClashYamlParser.MapShadowsocks) produce base64URL userinfo using
        // '-'/'_', not standard base64. Before the fix the parser decoded with a
        // plain Convert.FromBase64String which threw on '-'/'_', so those ss
        // servers were silently dropped. This fixture's standard base64 contains
        // a '+' (-> '-' in url-safe form), so it genuinely exercises the path.
        // url-safe of UTF8("aes-256-gcm:s>>?>p?w") = "YWVzLTI1Ni1nY206cz4-Pz5wP3c".
        var uri = "ss://YWVzLTI1Ni1nY206cz4-Pz5wP3c@host:8388#ss-b64url";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("shadowsocks", e.Protocol);
        Assert.Equal("aes-256-gcm", e.Method);
        Assert.Equal("s>>?>p?w", e.Password);
    }

    [Fact]
    public void Shadowsocks_ShadowTlsV3Plugin_ParsesPluginAndOpts()
    {
        var uri = "ss://2022-blake3-aes-256-gcm:k@host:443/?plugin=shadow-tls%3Bversion%3D3%3Bpassword%3Dstpw%3Bhost%3Dcdn.example.com#ss-stls";
        var e = VPNRouter.Core.Services.ServerUriParser.Parse(uri);
        Assert.Equal("shadow-tls", e.Plugin);
        Assert.Equal("version=3;password=stpw;host=cdn.example.com", e.PluginOpts);
    }

    [Fact]
    public void IsSupportedScheme_AcceptsAllSupportedSchemes()
    {
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("vless://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("hysteria2://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("hy2://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("tuic://x"));
        Assert.True(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("ss://x"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("trojan://x"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme("https://example.com"));
        Assert.False(VPNRouter.Core.Services.ServerUriParser.IsSupportedScheme(""));
    }

    [Fact]
    public void Parse_UnsupportedScheme_Throws()
    {
        Assert.Throws<System.FormatException>(() =>
            VPNRouter.Core.Services.ServerUriParser.Parse("trojan://x@host:443#bad"));
    }

    [Fact]
    public void Parse_UnsupportedSchemeWithCredentials_RedactsCredentialsInExceptionMessage()
    {
        var ex = Assert.Throws<System.FormatException>(() =>
            VPNRouter.Core.Services.ServerUriParser.Parse("trojan://secretuser:secretpass@host.example.com:443#bad"));

        Assert.DoesNotContain("secretuser", ex.Message);
        Assert.DoesNotContain("secretpass", ex.Message);
        Assert.Contains("trojan://host.example.com", ex.Message);
    }

    [Fact]
    public void ParseMultiple_SkipsBadLines_KeepsGoodOnes()
    {
        var blob = string.Join("\n",
            "vless://abc@1.2.3.4:443?security=reality&sni=x.com&pbk=P&sid=I#vl",
            "",
            "hysteria2://pw@host:443/?sni=x.com#hy2",
            "garbage",
            "tuic://u:p@host:443#tuic");
        var list = VPNRouter.Core.Services.ServerUriParser.ParseMultiple(blob);
        Assert.Equal(3, list.Count);
        Assert.Equal("vless",     list[0].Protocol);
        Assert.Equal("hysteria2", list[1].Protocol);
        Assert.Equal("tuic",      list[2].Protocol);
    }
}
