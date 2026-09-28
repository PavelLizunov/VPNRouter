using System.Linq;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

[Trait("Category", "Unit")]
[Trait("Phase", "Phase0")]
[Trait("Layer", "Core")]
public class AndroidDpiBypassInjectorTests
{
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
    [InlineData("hostfakesplit")]
    public void Inject_UnknownMode_ReturnsInputUnchanged(string mode)
    {
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
        Assert.True(proxy["udp_fragment"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("aggressive")]
    public void Inject_DirectOutbound_NotMutated(string mode)
    {
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
        var aggressive = AndroidDpiBypassInjector.Inject(SampleConfig, "aggressive");
        var standardAfter = AndroidDpiBypassInjector.Inject(aggressive, "standard");

        var root = JsonNode.Parse(standardAfter) as JsonObject;
        var proxy = (root!["outbounds"] as JsonArray)!.OfType<JsonObject>()
            .First(o => o["tag"]?.GetValue<string>() == "proxy");
        Assert.Null(proxy["udp_fragment"]);
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

        Assert.All(outbounds.Where(o => o["type"]!.GetValue<string>() == "vless"),
            o => Assert.NotNull(o["tls_fragment"]));
        var urltest = outbounds.First(o => o["type"]!.GetValue<string>() == "urltest");
        Assert.Null(urltest["tls_fragment"]);
        var direct = outbounds.First(o => o["type"]!.GetValue<string>() == "direct");
        Assert.Null(direct["tls_fragment"]);
    }

    [Fact]
    public void Inject_MalformedJson_ReturnsInputUnchanged()
    {
        var malformed = "{ this is not json";
        var result = AndroidDpiBypassInjector.Inject(malformed, "standard");
        Assert.Equal(malformed, result);
    }

    [Fact]
    public void Inject_OverwritesExistingTlsFragment()
    {
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
