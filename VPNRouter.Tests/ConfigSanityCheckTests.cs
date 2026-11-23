using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

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
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var check = new ConfigSanityCheck(logger);
        var result = check.CheckBeforeStart("{\"fixture-parser-detail\":");

        Assert.True(result.IsDead);
        Assert.Contains("parseable", result.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Assert.Single(sink.Events).Properties);
        sink.AssertExcludes("fixture-parser-detail", result.Reason!);
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public MockHandler(IEnumerable<Func<HttpResponseMessage>> responses)
        {
            _responses = new Queue<Func<HttpResponseMessage>>(responses);
        }

        public List<Uri> Requests { get; } = [];
        public List<string?> BearerTokens { get; } = [];
        public int Calls => Requests.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            BearerTokens.Add(request.Headers.Authorization?.Parameter);
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
        const string responseBody = "fixture-probe-response";
        var sink = new CapturingSink();
        using var logger = sink.CreateLogger();
        var handler = new MockHandler(new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent(responseBody),
            },
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent(responseBody),
            },
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var check = new ConfigSanityCheck(logger, http);

        using var cts = new CancellationTokenSource();
        var probeTask = check.ProbeAsync(9090, cts.Token);

        var result = await probeTask;

        Assert.True(result.IsDead);
        Assert.Contains(responseBody, result.Reason!);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e =>
        {
            Assert.Contains("HttpStatus", e.Properties["ReasonCode"].ToString());
            Assert.Equal("504", e.Properties["Status"].ToString());
        });
        sink.AssertExcludes(responseBody);
    }

    [Fact]
    public async Task CandidateProbeWithoutAnAcceptedControllerDoesNotConfirmReadiness()
    {
        var tunnel = new AcceptedTunnelInfo(new AppSettings(), "{}", "", null,
            new Dictionary<string, VlessServerEntry>());
        var result = await tunnel.ProbeProxyAsync(new ConfigSanityCheck(),
            TestContext.Current.CancellationToken, preferHttps: true);
        Assert.True(result.IsDead);
        Assert.Equal(0, result.LastDelayMs);
    }

    [Fact]
    public async Task CandidateProbeUsesTheAcceptedControllerAndHttpsDestination()
    {
        const string config = """
            {"experimental":{"clash_api":{"external_controller":"[::1]:9087","secret":"fixture-accepted"}},"outbounds":[]}
            """;
        var settings = new AppSettings();
        settings.SingBox.ClashApi = "127.0.0.1:9090";
        settings.SingBox.ClashApiSecret = "fixture-configured";
        var tunnel = new AcceptedTunnelInfo(settings, config, "",
            ClashSingBoxApi.ReadLocalController(config), new Dictionary<string, VlessServerEntry>());
        var handler = new MockHandler([
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"delay":123}""") }
        ]);
        using var http = new HttpClient(handler);
        var probe = await tunnel.ProbeProxyAsync(new ConfigSanityCheck(httpClient: http),
            TestContext.Current.CancellationToken, preferHttps: true);

        Assert.False(probe.IsDead);
        Assert.Equal("fixture-accepted", Assert.Single(handler.BearerTokens));
        Assert.Equal("http://[::1]:9087/proxies/proxy/delay?url=" +
            Uri.EscapeDataString(DeepVerifyConstants.ProbeUrl) + "&timeout=5000",
            Assert.Single(handler.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task DnsttProbe_WhenBothDestinationsFail_DoesNotConfirmReadiness()
    {
        var handler = new MockHandler([
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout),
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"delay":0}""") }
        ]);
        using var http = new HttpClient(handler);
        var check = new ConfigSanityCheck(httpClient: http);

        var result = await check.ProbeAsync(new Uri("http://127.0.0.1:9090"), null,
            TestContext.Current.CancellationToken, dnstt: true);

        Assert.True(result.IsDead);
        Assert.Equal(0, result.LastDelayMs);
        Assert.Collection(handler.Requests,
            request => Assert.Equal("http://127.0.0.1:9090/proxies/proxy/delay?url=" +
                Uri.EscapeDataString(DeepVerifyConstants.ProbeUrl) + "&timeout=8000", request.AbsoluteUri),
            request => Assert.Equal("http://127.0.0.1:9090/proxies/proxy/delay?url=" +
                Uri.EscapeDataString("http://www.gstatic.com/generate_204") + "&timeout=6000", request.AbsoluteUri));
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
