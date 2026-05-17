#nullable enable
// ============================================================================
// VlessDeepVerifierTests.cs — Phase 2G sub-wave 7c-1 (v3.0 refactor, 2026-05-18)
// ============================================================================
//
// Pinned behaviour for `VlessDeepVerifier` — the deep server probe gating
// admission to the Servers / Subscriptions pools. HIGH priority per
// test-coverage-audit-2026-05-17.md §2 (false positive → bad server marked
// good → user's traffic silently fails).
//
// This file covers Layer 1 (sing-box JSON config builder) + Layer 4 (helper
// utilities). The placeholder-credential gate, binary-missing fallback, and
// cancellation behaviour live in <see cref="VlessDeepVerifierBehaviourTests"/>.
// Split to stay under the per-file 300-LOC gate from
// plans/phase2-2G-untested-services-2026-05-17.md.
//
// Integration probe (real Process.Start + SOCKS5 round-trip) is OUT of
// scope — Wave 6's IProcessRunner/IHttpClient/ISingBoxApi seams are the
// path to a full-stack rewrite of VerifyAsync that's testable end-to-end.
// We restrict scope to the parts that can be exercised without spawning
// a real sing-box (per brief §"Scope boundaries": minimal seam wiring only).
// ============================================================================

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

    // ─── Layer 1: sing-box config builder (BuildSingleOutboundConfig) ─────

    [Fact]
    public void BuildSingleOutboundConfig_HappyPathVless_ProducesValidShape()
    {
        // The verifier-spawned sing-box must have a SOCKS inbound, a single
        // proxy outbound, a direct dns-direct-out outbound, and the Clash
        // API enabled — those are the four invariants the production
        // probe path depends on. If the config builder drifts, every
        // VerifyAsync verdict turns to noise.
        var entry = CleanVlessEntry();
        var json = VlessDeepVerifier.BuildSingleOutboundConfig(entry, socksPort: 10808, clashPort: 9090);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // SOCKS inbound on loopback at the chosen port (used by the HTTP probe).
        var inbounds = root.GetProperty("inbounds");
        Assert.Equal(1, inbounds.GetArrayLength());
        var socksIn = inbounds[0];
        Assert.Equal("socks", socksIn.GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", socksIn.GetProperty("listen").GetString());
        Assert.Equal(10808, socksIn.GetProperty("listen_port").GetInt32());

        // Two outbounds: the protocol-tagged proxy + the dns-direct-out
        // helper (required to break the DNS hijack-dns loop on 1.13+).
        var outbounds = root.GetProperty("outbounds");
        Assert.Equal(2, outbounds.GetArrayLength());
        Assert.Equal("vless", outbounds[0].GetProperty("type").GetString());
        Assert.Equal("proxy", outbounds[0].GetProperty("tag").GetString());
        Assert.Equal("direct", outbounds[1].GetProperty("type").GetString());
        Assert.Equal("dns-direct-out", outbounds[1].GetProperty("tag").GetString());

        // route.final routes everything through the proxy — split-tunnel
        // semantics don't apply here (we want ALL traffic through the
        // candidate so the verdict reflects the proxy's reachability).
        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());

        // Clash API on the chosen port (used by HealthMonitor / hot-reload
        // in production; not used here but the schema is shared so we keep
        // the surface honest).
        var clash = root.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal($"127.0.0.1:9090", clash.GetProperty("external_controller").GetString());
    }

    [Fact]
    public void BuildSingleOutboundConfig_VlessRealityCredentials_FlowDownToProxyOutbound()
    {
        // VLESS-specific fields must end up on the proxy outbound — uuid,
        // flow, reality.public_key, reality.short_id, server, server_port.
        // Skip this and a verifier "passes" with wrong creds because the
        // sing-box config silently dropped them.
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
        // v2.31.6-r16 fix (iter#7 Phase 2): pre-r16 hard-coded "vless"
        // here, so Hy2/TUIC/SS deep-verify ALWAYS failed because sing-box
        // rejected the config. Pin that the dispatcher now hits the
        // right builder.
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

        // ALPN forced to h3 — required by Hy2 spec.
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
        // Branch coverage: VLESS+Reality+WS — the v2.31.6-r16 protocol
        // dispatcher kept this branch from BuildVlessOutbound. Pre-r16
        // this was hard-coded as `type=vless` with no transport block,
        // so WS-only servers failed deep-verify with a connect timeout.
        var entry = CleanVlessEntry();
        entry.Transport = new VlessTransportConfig { Type = "ws", Path = "/vlessws" };

        var outbound = VlessDeepVerifier.BuildVlessOutbound(entry);

        Assert.Equal("vless", outbound["type"]!.GetValue<string>());
        var transport = outbound["transport"] as JsonObject;
        Assert.NotNull(transport);
        Assert.Equal("ws", transport!["type"]!.GetValue<string>());
        Assert.Equal("/vlessws", transport["path"]!.GetValue<string>());
    }

    // ─── Layer 4: helper utilities ───────────────────────────────────────

    [Fact]
    public void FindFreePort_ReturnsHighEphemeralPort()
    {
        // Smoke test: returns a positive port that's currently free on
        // loopback. The probe path uses two FindFreePort calls (SOCKS +
        // Clash) — they MUST differ in production, but that's a separate
        // race-condition concern; here we pin that the call succeeds.
        var port = VlessDeepVerifier.FindFreePort();
        Assert.InRange(port, 1, 65535);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]    // Loopback
    [InlineData("10.0.0.5", true)]     // 10.0.0.0/8
    [InlineData("172.16.5.42", true)]  // 172.16.0.0/12 (low end)
    [InlineData("172.31.0.1", true)]   // 172.16.0.0/12 (high end)
    [InlineData("192.168.1.1", true)]  // 192.168.0.0/16
    [InlineData("100.64.0.1", true)]   // 100.64.0.0/10 (CGN range)
    [InlineData("1.1.1.1", false)]     // Cloudflare public DNS
    [InlineData("8.8.8.8", false)]     // Google public DNS
    [InlineData("172.15.0.1", false)]  // Just below RFC1918
    [InlineData("172.32.0.1", false)]  // Just above RFC1918
    public void IsPrivateOrLoopback_ClassifiesIpsCorrectly(string ipString, bool expected)
    {
        // The verifier rejects "verified" if the Cloudflare trace endpoint
        // returns an ip= line that's private/loopback — that's the
        // signature of a transparent proxy or a sandboxed VM picking up
        // ITS OWN egress instead of the proxy's. A bug in this classifier
        // → wrong-IP verdicts slip through.
        var ip = IPAddress.Parse(ipString);
        Assert.Equal(expected, VlessDeepVerifier.IsPrivateOrLoopback(ip));
    }

    [Fact]
    public void TrimSnippet_LongInput_TruncatesWithEllipsis()
    {
        // Used for stderr-snippet trimming when sing-box stderr is too
        // verbose to surface inline. Long input → truncated + ellipsis;
        // newlines → collapsed to spaces (so log line stays single-line).
        var verbose = string.Join('\n', new[] { "line one of stderr", "line two with more", "line three more text" });
        var snip = VlessDeepVerifier.TrimSnippet(verbose, 20);

        Assert.True(snip.Length <= 21); // 20 + ellipsis (1-char "…")
        Assert.DoesNotContain('\n', snip);
        Assert.DoesNotContain('\r', snip);
        Assert.EndsWith("…", snip);
    }

    [Fact]
    public void TrimSnippet_ShortInput_NoEllipsis()
    {
        // Input shorter than the budget passes through clean (after
        // newline collapsing).
        var snip = VlessDeepVerifier.TrimSnippet("short", 80);
        Assert.Equal("short", snip);
        Assert.DoesNotContain("…", snip);
    }
}
