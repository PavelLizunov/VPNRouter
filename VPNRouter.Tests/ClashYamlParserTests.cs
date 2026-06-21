#nullable enable
using System.Linq;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// P6 (2026-06-21) — Clash / Clash-Meta YAML subscription parsing. Verifies the
/// <see cref="ClashYamlParser"/> proxy→URI mapping and the end-to-end path through
/// <see cref="SubscriptionFetcher.ParseBody"/> (the real import flow). Tolerant:
/// unsupported proxy types and malformed entries are skipped, not thrown.
/// </summary>
public class ClashYamlParserTests
{
    private const string MixedClashYaml = @"
proxies:
  - name: ""DE-Reality""
    type: vless
    server: de.example.com
    port: 443
    uuid: 11111111-2222-3333-4444-555555555555
    network: tcp
    tls: true
    flow: xtls-rprx-vision
    servername: www.microsoft.com
    client-fingerprint: chrome
    reality-opts:
      public-key: aBcDeFpublickey123
      short-id: 0123abcd
  - name: ""HY2-node""
    type: hysteria2
    server: hy.example.com
    port: 8443
    password: secretpass
    sni: hy.example.com
    skip-cert-verify: true
  - name: ""SS-node""
    type: ss
    server: ss.example.com
    port: 8388
    cipher: aes-256-gcm
    password: sspassword
  - name: ""Trojan-skip""
    type: trojan
    server: tr.example.com
    port: 443
    password: shouldbeskipped
proxy-groups:
  - name: auto
    type: url-test
";

    [Fact]
    public void LooksLikeClashYaml_DetectsProxiesKey()
    {
        Assert.True(ClashYamlParser.LooksLikeClashYaml(MixedClashYaml));
        Assert.True(ClashYamlParser.LooksLikeClashYaml("proxies:\n  - name: x"));
    }

    [Fact]
    public void LooksLikeClashYaml_RejectsNonClashBodies()
    {
        Assert.False(ClashYamlParser.LooksLikeClashYaml("vless://uuid@host:443?security=reality#n"));
        Assert.False(ClashYamlParser.LooksLikeClashYaml("dmxlc3M6Ly91dWlkQGhvc3Q6NDQz")); // base64-ish
        Assert.False(ClashYamlParser.LooksLikeClashYaml(""));
        // "proxies" inside a base64 blob (not at line start) must not trip it.
        Assert.False(ClashYamlParser.LooksLikeClashYaml("Zm9vproxies:bar"));
    }

    [Fact]
    public void ParseProxiesToUris_MapsSupportedSkipsUnsupported()
    {
        var uris = ClashYamlParser.ParseProxiesToUris(MixedClashYaml);

        // vless + hysteria2 + ss mapped; trojan skipped.
        Assert.Equal(3, uris.Count);
        Assert.Contains(uris, u => u.StartsWith("vless://"));
        Assert.Contains(uris, u => u.StartsWith("hysteria2://"));
        Assert.Contains(uris, u => u.StartsWith("ss://"));
        Assert.DoesNotContain(uris, u => u.StartsWith("trojan://"));
    }

    [Fact]
    public void ParseBody_ClashYaml_ProducesEntriesWithCorrectFields()
    {
        var entries = SubscriptionFetcher.ParseBody(MixedClashYaml);

        Assert.Equal(3, entries.Count);

        var vless = Assert.Single(entries, e => e.Protocol == "vless");
        Assert.Equal("de.example.com", vless.Server);
        Assert.Equal(443, vless.Port);
        Assert.Equal("11111111-2222-3333-4444-555555555555", vless.Uuid);
        Assert.Equal("xtls-rprx-vision", vless.Flow);
        Assert.Equal("aBcDeFpublickey123", vless.Reality.PublicKey);
        Assert.Equal("0123abcd", vless.Reality.ShortId);
        Assert.Equal("www.microsoft.com", vless.Reality.ServerName);

        var hy2 = Assert.Single(entries, e => e.Protocol == "hysteria2");
        Assert.Equal("hy.example.com", hy2.Server);
        Assert.Equal(8443, hy2.Port);

        var ss = Assert.Single(entries, e => e.Protocol == "shadowsocks");
        Assert.Equal("ss.example.com", ss.Server);
        Assert.Equal(8388, ss.Port);
    }

    [Fact]
    public void ParseBody_ClashYaml_TlsNoRealityMapsAsTls()
    {
        const string tlsYaml = @"
proxies:
  - name: ""TLS-only""
    type: vless
    server: tls.example.com
    port: 443
    uuid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
    network: ws
    tls: true
    servername: cdn.example.com
    ws-opts:
      path: /vpn
      headers:
        Host: cdn.example.com
";
        var entries = SubscriptionFetcher.ParseBody(tlsYaml);
        var e = Assert.Single(entries);
        Assert.Equal("tls.example.com", e.Server);
        Assert.Equal("vless", e.Protocol);
    }

    [Fact]
    public void ParseProxiesToUris_EmptyOrMalformed_ReturnsEmptyNeverThrows()
    {
        Assert.Empty(ClashYamlParser.ParseProxiesToUris(""));
        Assert.Empty(ClashYamlParser.ParseProxiesToUris("not: valid\n  - broken: ["));
        Assert.Empty(ClashYamlParser.ParseProxiesToUris("proxies: []"));
    }
}
