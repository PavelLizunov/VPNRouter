using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.32.0 (AND-ZAPRET, 2026-05-07) — pin the rules around the Android
/// DPI-bypass JSON injector. The actual config-build pipeline lives in
/// <c>VPNRouter.Android.AndroidConfigBuilder</c> and exercises this
/// helper through <c>InjectDpiBypass</c>; we test the helper directly
/// because the pipeline pulls in <c>AndroidStorage</c> (which needs
/// the Android runtime to read SharedPreferences).
///
/// <para>Why these tests matter: a regression here would silently turn
/// off the DPI-bypass for users in Russian ISP regions. The "bypass
/// works" signal is invisible to a smoke test — sites that DPI blocks
/// still resolve through DNS, just hang on TLS — and the difference
/// between "fragmenting correctly" and "shipping the original Client
/// Hello" is one missing JSON node. The pin set:</para>
///
/// <list type="bullet">
///   <item>Off mode is a no-op (existing snapshots stay byte-identical
///   after install + first run).</item>
///   <item>Standard / aggressive each produce the documented
///   <c>tls_fragment</c> + <c>udp_fragment</c> shape on real proxy
///   outbounds.</item>
///   <item>Direct / dns / control-plane outbounds are NOT mutated.</item>
///   <item>Repeat calls are idempotent (UI may regenerate on every
///   tunnel start; we don't want stacking mutations).</item>
///   <item>Mode flips clean up — aggressive→standard removes
///   <c>udp_fragment</c> so the previous mode's intent doesn't leak.</item>
///   <item>Unknown modes are no-ops (defensive behaviour for typos
///   or hand-edited preferences).</item>
/// </list>
/// </summary>
public class AndroidDpiBypassInjectorTests
{
    /// <summary>
    /// Sample config covering the three outbound categories the injector
    /// must distinguish: a real proxy (vless), a bare direct outbound
    /// that should be left alone, and a dns-direct outbound (whose
    /// pre-existing <c>udp_fragment: true</c> must survive untouched —
    /// it's a sing-box 1.13 workaround unrelated to DPI bypass).
    /// </summary>
    private const string SampleConfig = """
    {
      "log": { "level": "info" },
      "dns": { "servers": [] },
      "inbounds": [{ "type": "tun", "tag": "tun-in" }],
      "outbounds": [
        { "type": "vless", "tag": "proxy", "server": "1.2.3.4", "server_port": 443,
          "uuid": "abc", "flow": "xtls-rprx-vision",
          "tls": { "enabled": true, "server_name": "example.com" } },
        { "type": "direct", "tag": "direct" },
        { "type": "direct", "tag": "dns-direct", "udp_fragment": true }
      ],
      "route": { "rules": [] }
    }
    """;

    [Theory]
    [InlineData("off")]
    [InlineData("OFF")]
    [InlineData("")]
    [InlineData(null)]
    public void Inject_OffOrEmpty_ReturnsInputUnchanged(string? mode)
    {
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, mode!);
        Assert.Equal(SampleConfig, result);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("STRICT")]
    [InlineData("hostfakesplit")] // desktop Zapret strategy name — invalid here
    public void Inject_UnknownMode_ReturnsInputUnchanged(string mode)
    {
        // Defensive: AndroidStorage SR-1 normaliser already rejects these,
        // but tests + direct callers shouldn't be punished for typos.
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, mode);
        Assert.Equal(SampleConfig, result);
    }

    [Fact]
    public void Inject_Standard_AddsTlsFragmentToProxyOutbound()
    {
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, "standard");
        var root = JsonNode.Parse(result) as JsonObject;
        Assert.NotNull(root);

        var proxy = (root["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        var fragment = proxy["tls_fragment"] as JsonObject;
        Assert.NotNull(fragment);
        Assert.True(fragment!["enabled"]!.GetValue<bool>());
        Assert.Equal("10-100", fragment["size"]!.GetValue<string>());
        Assert.Equal("10-50", fragment["sleep"]!.GetValue<string>());
        // Standard does NOT touch udp_fragment.
        Assert.Null(proxy["udp_fragment"]);
    }

    [Fact]
    public void Inject_Aggressive_AddsTlsFragmentAndUdpFragment()
    {
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, "aggressive");
        var root = JsonNode.Parse(result) as JsonObject;
        var proxy = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        var fragment = proxy["tls_fragment"] as JsonObject;
        Assert.NotNull(fragment);
        Assert.True(fragment!["enabled"]!.GetValue<bool>());
        Assert.Equal("5-20", fragment["size"]!.GetValue<string>());
        Assert.Equal("50-150", fragment["sleep"]!.GetValue<string>());
        // Aggressive enables udp_fragment for QUIC carriers.
        Assert.True(proxy["udp_fragment"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("aggressive")]
    public void Inject_DirectOutbound_NotMutated(string mode)
    {
        // The bare direct outbound (no udp_fragment) must stay
        // byte-equal — fragmenting it would break direct-route traffic
        // on platforms that don't tolerate dialer overrides on the
        // empty direct outbound.
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, mode);
        var root = JsonNode.Parse(result) as JsonObject;
        var direct = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "direct");
        Assert.Null(direct["tls_fragment"]);
        Assert.Null(direct["udp_fragment"]);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("aggressive")]
    public void Inject_DnsDirect_KeepsUdpFragmentNoTlsFragment(string mode)
    {
        // dns-direct already has udp_fragment:true (sing-box 1.13
        // workaround for the empty-direct-outbound restriction). The
        // injector must not strip that AND must not add tls_fragment
        // (dns-direct routes only DNS, never user TLS traffic).
        var result = AndroidDpiBypassInjector.Inject(SampleConfig, mode);
        var root = JsonNode.Parse(result) as JsonObject;
        var dns = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "dns-direct");
        Assert.True(dns["udp_fragment"]!.GetValue<bool>(),
            "dns-direct's pre-existing udp_fragment must survive injection");
        Assert.Null(dns["tls_fragment"]);
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("aggressive")]
    public void Inject_IsIdempotent(string mode)
    {
        var once = AndroidDpiBypassInjector.Inject(SampleConfig, mode);
        var twice = AndroidDpiBypassInjector.Inject(once, mode);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Inject_AggressiveThenStandard_ClearsUdpFragment()
    {
        // Mode flip aggressive → standard must explicitly remove the
        // stale udp_fragment so we don't leak the previous mode's
        // intent into the new config.
        var aggressive = AndroidDpiBypassInjector.Inject(SampleConfig, "aggressive");
        var standardAfter = AndroidDpiBypassInjector.Inject(aggressive, "standard");

        var root = JsonNode.Parse(standardAfter) as JsonObject;
        var proxy = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        Assert.Null(proxy["udp_fragment"]);
        // tls_fragment shape is the standard one, not aggressive
        var fragment = proxy["tls_fragment"] as JsonObject;
        Assert.Equal("10-100", fragment!["size"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("hysteria2")]
    [InlineData("tuic")]
    [InlineData("shadowsocks")]
    [InlineData("ss")]
    [InlineData("trojan")]
    [InlineData("http")]
    [InlineData("socks")]
    [InlineData("shadowtls")]
    public void Inject_AllSupportedProxyTypes_GetTlsFragment(string proxyType)
    {
        // Build a minimal config with one proxy outbound of the given
        // type. The injector should treat all listed types alike.
        var json = $$"""
        {
          "outbounds": [
            { "type": "{{proxyType}}", "tag": "proxy" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;
        var result = AndroidDpiBypassInjector.Inject(json, "standard");
        var root = JsonNode.Parse(result) as JsonObject;
        var proxy = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        Assert.NotNull(proxy["tls_fragment"]);
    }

    [Theory]
    [InlineData("selector")]
    [InlineData("urltest")]
    [InlineData("block")]
    [InlineData("dns")]
    public void Inject_ControlPlaneOutbounds_NotMutated(string outboundType)
    {
        // Selector / urltest / block / dns are control-plane: they
        // don't terminate at a remote host so fragmentation is
        // meaningless (and would break sing-box's outbound type
        // validation in some cases).
        var json = $$"""
        {
          "outbounds": [
            { "type": "{{outboundType}}", "tag": "ctrl" }
          ]
        }
        """;
        var result = AndroidDpiBypassInjector.Inject(json, "standard");
        var root = JsonNode.Parse(result) as JsonObject;
        var ctrl = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "ctrl");
        Assert.Null(ctrl["tls_fragment"]);
    }

    [Fact]
    public void Inject_MultipleProxyOutbounds_AllGetTlsFragment()
    {
        // Realistic multi-server case: ConfigGenerator builds child
        // outbounds + a urltest wrapper. Each child gets fragmentation;
        // the urltest wrapper does not (it's control-plane).
        var json = """
        {
          "outbounds": [
            { "type": "vless", "tag": "vless-srv1" },
            { "type": "vless", "tag": "vless-srv2" },
            { "type": "vless", "tag": "vless-srv3" },
            { "type": "urltest", "tag": "proxy",
              "outbounds": ["vless-srv1","vless-srv2","vless-srv3"] },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;
        var result = AndroidDpiBypassInjector.Inject(json, "standard");
        var root = JsonNode.Parse(result) as JsonObject;
        var outbounds = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>().ToList();

        // All three vless children get tls_fragment.
        Assert.All(outbounds.Where(o => o["type"]!.GetValue<string>() == "vless"),
            o => Assert.NotNull(o["tls_fragment"]));
        // urltest wrapper does not.
        var urltest = outbounds.First(o => o["type"]!.GetValue<string>() == "urltest");
        Assert.Null(urltest["tls_fragment"]);
        // direct does not.
        var direct = outbounds.First(o => o["type"]!.GetValue<string>() == "direct");
        Assert.Null(direct["tls_fragment"]);
    }

    [Fact]
    public void Inject_MalformedJson_ReturnsInputUnchanged()
    {
        // libbox will surface its own clearer error on the unchanged
        // input than we could construct here from a half-walked tree.
        var malformed = "{ this is not json";
        var result = AndroidDpiBypassInjector.Inject(malformed, "standard");
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Inject_OverwritesExistingTlsFragment()
    {
        // User pasted custom JSON with their own tls_fragment values,
        // then picked "aggressive". Most-recent-action-wins: aggressive
        // values must replace the user's. Escape hatch is keeping the
        // picker on "off".
        var json = """
        {
          "outbounds": [
            { "type": "vless", "tag": "proxy",
              "tls_fragment": { "enabled": true, "size": "999-9999", "sleep": "0-0" } },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;
        var result = AndroidDpiBypassInjector.Inject(json, "aggressive");
        var root = JsonNode.Parse(result) as JsonObject;
        var proxy = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        var fragment = proxy["tls_fragment"] as JsonObject;
        Assert.Equal("5-20", fragment!["size"]!.GetValue<string>());
        Assert.Equal("50-150", fragment["sleep"]!.GetValue<string>());
    }
}
