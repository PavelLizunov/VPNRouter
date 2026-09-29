using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public class ConfigSanityCheckTests
{
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

    [Fact]
    public void DetectsPlaceholderPubkey()
    {
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

        using var cts = new CancellationTokenSource();
        var probeTask = check.ProbeAsync(9090, cts.Token);

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

        var result = await check.ProbeAsync(9090, TestContext.Current.CancellationToken);

        Assert.False(result.IsDead);
        Assert.Equal(123, result.LastDelayMs);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Probe_DelayZero_TreatedAsDead()
    {
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

        var result = await check.ProbeAsync(9090, TestContext.Current.CancellationToken);

        Assert.True(result.IsDead);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Probe_InvalidPort_FailsFast()
    {
        var check = new ConfigSanityCheck();
        var result = await check.ProbeAsync(0, TestContext.Current.CancellationToken);

        Assert.True(result.IsDead);
        Assert.Contains("invalid", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }
}
