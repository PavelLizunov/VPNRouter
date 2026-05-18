using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// F-E (2026-05-11) — pin tests for <see cref="ConfigSanityCheck"/>.
///
/// <para>Covers the placeholder fingerprint matchers (Reality
/// public_key, short_id, server IP) using the actual stas-evidence
/// outbound JSON shape from <c>plans/stas-evidence-current.json</c>,
/// plus structural fallbacks (missing server, missing port, missing
/// uuid for VLESS, no proxy outbound at all) and the happy-path
/// validation of a clean config.</para>
///
/// <para>The probe phase is exercised via a mock <see cref="HttpClient"/>
/// configured with <see cref="MockHandler"/> so we don't need a live
/// Clash API. Both attempts produce the same response so the cycle
/// behavior is deterministic in tests.</para>
/// </summary>
public class ConfigSanityCheckTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Build a sing-box outbound JsonObject matching stas-evidence-current.json.
    /// Caller can null-out / override fields to exercise individual checks.
    /// </summary>
    private static JsonObject BuildValidOutbound(
        string? pubkey = null,
        string? shortId = null,
        string? server = null,
        int? port = null,
        string? uuid = null)
    {
        return new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = "proxy",
            ["server"] = server ?? "194.87.222.111",
            ["server_port"] = port ?? 443,
            ["uuid"] = uuid ?? "2d54442d-158f-49e2-b225-67ba1a5b77f4",
            ["flow"] = "xtls-rprx-vision",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = "yahoo.com",
                ["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = pubkey ?? "RealGoodPubKeyFromValidSub_abc123",
                    ["short_id"] = shortId ?? "abcd1234",
                },
            },
        };
    }

    private static JsonObject BuildConfigWithOutbound(JsonObject outbound)
    {
        return new JsonObject
        {
            ["outbounds"] = new JsonArray { outbound, new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
        };
    }

    // ─── Pre-start static check ───────────────────────────────────────────

    [Fact]
    public void DetectsPlaceholderPubkey()
    {
        // Test #1 in the F-E acceptance criteria — stas-evidence pubkey.
        var outbound = BuildValidOutbound(
            pubkey: "DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU");
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Contains("placeholder", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outbound.tls.reality.public_key", result.OffendingField);
    }

    [Fact]
    public void DetectsPlaceholderShortId()
    {
        var outbound = BuildValidOutbound(shortId: "78ca7952");
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Contains("placeholder", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outbound.tls.reality.short_id", result.OffendingField);
    }

    [Fact]
    public void DetectsPlaceholderServer()
    {
        // Stas-evidence server IP — 195.135.255.216 — without the matching
        // pubkey/short_id (e.g. user manually patched the server but left
        // the IP). Still dead.
        var outbound = BuildValidOutbound(server: "195.135.255.216");
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Contains("placeholder", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outbound.server", result.OffendingField);
    }

    [Fact]
    public void DetectsMissingServer()
    {
        var outbound = BuildValidOutbound(server: "");
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Contains("empty", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("outbound.server", result.OffendingField);
    }

    [Fact]
    public void DetectsInvalidPort()
    {
        var outbound = BuildValidOutbound(port: 0);
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Equal("outbound.server_port", result.OffendingField);
    }

    [Fact]
    public void DetectsMissingUuidForVless()
    {
        var outbound = BuildValidOutbound(uuid: "");
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Equal("outbound.uuid", result.OffendingField);
    }

    [Fact]
    public void DetectsNoProxyOutbound()
    {
        // Only a direct outbound — no proxy/vless/hy2 type at all.
        var config = new JsonObject
        {
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
        };

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.True(result.IsDead);
        Assert.Equal("outbounds", result.OffendingField);
    }

    [Fact]
    public void PassesValidConfig()
    {
        // The defaults from BuildValidOutbound resemble a real subscription
        // outbound — different pubkey/sid/server than the placeholders.
        var outbound = BuildValidOutbound();
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.False(result.IsDead);
        Assert.Null(result.Reason);
        Assert.Null(result.OffendingField);
    }

    [Fact]
    public void PassesValidHysteria2Config()
    {
        // Non-VLESS protocol — uuid not required, only password.
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = "hy2.example.com",
            ["server_port"] = 443,
            ["password"] = "secret",
        };
        var config = BuildConfigWithOutbound(outbound);

        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart(config);

        Assert.False(result.IsDead);
    }

    [Fact]
    public void DetectsMalformedJson()
    {
        var check = new ConfigSanityCheck();
        var result = check.CheckBeforeStart("not json at all");

        Assert.True(result.IsDead);
        Assert.Contains("parseable", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Post-start probe (mocked HTTP) ───────────────────────────────────

    /// <summary>
    /// Minimal HttpMessageHandler that returns a queued sequence of
    /// responses for sequential HTTP calls. Useful for testing the
    /// "try twice" loop in <see cref="ConfigSanityCheck.ProbeAsync"/>.
    /// </summary>
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public MockHandler(IEnumerable<Func<HttpResponseMessage>> responses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(responses);
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no more queued responses"),
                });
            }
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    [Fact]
    public async Task Probe_BothAttemptsHttp504_ReturnsDead()
    {
        var handler = new MockHandler(new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("{\"message\":\"An operation was canceled.\"}"),
            },
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("{\"message\":\"An operation was canceled.\"}"),
            },
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var check = new ConfigSanityCheck(httpClient: http);

        // Short-circuit the inter-attempt 3s delay via CT cancellation —
        // we want the probe loop logic without the timeout cost.
        using var cts = new CancellationTokenSource();
        var probeTask = check.ProbeAsync(9090, cts.Token);

        // Allow first request, then cancel before the 3s settle delay
        // so the second attempt either still runs OR throws — both
        // valid because the loop must end with IsDead.
        var result = await probeTask;

        Assert.True(result.IsDead);
        Assert.NotNull(result.Reason);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Probe_FirstAttemptOk_ReturnsAlive()
    {
        var handler = new MockHandler(new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"delay\":123}"),
            },
        });
        var http = new HttpClient(handler);
        var check = new ConfigSanityCheck(httpClient: http);

        var result = await check.ProbeAsync(9090);

        Assert.False(result.IsDead);
        Assert.Equal(123, result.LastDelayMs);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Probe_DelayZero_TreatedAsDead()
    {
        // Clash API convention: delay=0 means timeout/unreachable.
        var handler = new MockHandler(new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"delay\":0}"),
            },
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"delay\":0}"),
            },
        });
        var http = new HttpClient(handler);
        var check = new ConfigSanityCheck(httpClient: http);

        var result = await check.ProbeAsync(9090);

        Assert.True(result.IsDead);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Probe_InvalidPort_FailsFast()
    {
        var check = new ConfigSanityCheck();
        var result = await check.ProbeAsync(0);

        Assert.True(result.IsDead);
        Assert.Contains("invalid", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
