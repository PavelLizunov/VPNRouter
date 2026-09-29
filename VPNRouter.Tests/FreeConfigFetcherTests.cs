using System;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.FreeConfigs;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class FreeConfigFetcherTests
{
    private const string SourceUrl = "https://configs.example/subscription";
    private const string Vless =
        "vless://11111111-2222-3333-4444-555555555555@server.example:443?security=tls&type=tcp#one";

    [Fact]
    public async Task FetchAsync_UsesBoundedPolicyEnvelopeAndExtracts()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var http = new FakeHttpClient().Setup(SourceUrl, $"{Vless}\n{Vless}\n");
        var fetcher = new FreeConfigFetcher(logger, http);

        var result = await fetcher.FetchAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { Vless }, result);
        var request = Assert.Single(http.SentRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(SourceUrl, request.Uri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.Equal(1, request.RetryCount);
        Assert.Equal((long)FreeConfigFetcher.MaxSourceBytes, request.MaxResponseBytes!.Value);
    }

    [Fact]
    public async Task FetchAsync_RejectsBodyAboveDedicatedCap()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var oversized = Vless + "\n" + new string('x', FreeConfigFetcher.MaxSourceBytes);
        var http = new FakeHttpClient().Setup(SourceUrl, oversized);
        var fetcher = new FreeConfigFetcher(logger, http);

        var result = await fetcher.FetchAsync(Source(), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchAsync_BodyAtExactDedicatedCapIsAccepted()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var prefix = Vless + "\n";
        var atLimit = prefix + new string(' ',
            FreeConfigFetcher.MaxSourceBytes - Encoding.UTF8.GetByteCount(prefix));
        Assert.Equal(FreeConfigFetcher.MaxSourceBytes, Encoding.UTF8.GetByteCount(atLimit));
        var http = new FakeHttpClient().Setup(SourceUrl, atLimit);
        var fetcher = new FreeConfigFetcher(logger, http);

        Assert.Equal(new[] { Vless }, await fetcher.FetchAsync(Source(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/configs.txt")]
    [InlineData("gopher://example.com/123")]
    [InlineData("malformed-secret-sentinel")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task FetchAsync_InvalidUrlDoesNotSendOrLogInput(string? invalidUrl)
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var http = new FakeHttpClient();
        var fetcher = new FreeConfigFetcher(logger, http);
        var source = new FreeConfigSource
        {
            Name = "source-secret-sentinel",
            Url = invalidUrl!,
            Enabled = true,
        };

        Assert.Empty(await fetcher.FetchAsync(source, TestContext.Current.CancellationToken));
        Assert.Empty(http.SentRequests);
        var entry = Assert.Single(sink.Events);
        Assert.Equal("FreeConfigFetcher: refused non-http(s) or malformed source URL", entry.RenderMessage());
        Assert.Empty(entry.Properties);
        Assert.Null(entry.Exception);
    }

    [Theory]
    [InlineData("http://configs.example/subscription")]
    [InlineData("HTTPS://configs.example/subscription")]
    public async Task FetchAsync_AcceptsHttpAndHttps(string url)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var absoluteUrl = new Uri(url).AbsoluteUri;
        var http = new FakeHttpClient().Setup(absoluteUrl, Vless);
        var source = new FreeConfigSource { Name = "test-source", Url = url, Enabled = true };

        Assert.Equal(new[] { Vless }, await new FreeConfigFetcher(logger, http).FetchAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(absoluteUrl, Assert.Single(http.SentRequests).Uri.AbsoluteUri);
    }

    private sealed class CapturingSink : Serilog.Core.ILogEventSink
    {
        public System.Collections.Generic.List<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    public async Task FetchAsync_HttpFailureReturnsEmpty(int statusCode)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var http = new FakeHttpClient().Setup(SourceUrl, "failure", statusCode);
        var fetcher = new FreeConfigFetcher(logger, http);

        Assert.Empty(await fetcher.FetchAsync(Source(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FetchAsync_TransportAndTimeoutFailuresReturnEmpty()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        foreach (var error in new Exception[]
        {
            new HttpRequestException("network failed"),
            new TimeoutException("request timed out"),
        })
        {
            var http = new FakeHttpClient().ThrowOn(SourceUrl, error);
            var fetcher = new FreeConfigFetcher(logger, http);
            Assert.Empty(await fetcher.FetchAsync(Source(), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task FetchAsync_CallerCancellationReachesTransportAndPropagates()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var http = new BlockingHttpClient();
        var fetcher = new FreeConfigFetcher(logger, http);
        using var cts = new CancellationTokenSource();

        var pending = fetcher.FetchAsync(Source(), cts.Token);
        await http.Started.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(http.CancellationObserved);
    }

    [Fact]
    public void ExtractVlessLines_PreservesPlainAndBase64Formats()
    {
        var plain = FreeConfigFetcher.ExtractVlessLines($"ignored\n{Vless}\n{Vless}");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Vless}\n"));
        var fromBase64 = FreeConfigFetcher.ExtractVlessLines(encoded);

        Assert.Equal(new[] { Vless }, plain);
        Assert.Equal(new[] { Vless }, fromBase64);
    }

    [Fact]
    public void ExtractVlessLines_ExtractsMultiProtocolSchemes()
    {
        const string hy2 = "hy2://password@server.example:443?sni=example.com#h2";
        const string ss = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwYXNz@server.example:443#ss";
        var lines = FreeConfigFetcher.ExtractVlessLines($"ignored\n{Vless}\n{hy2}\n{ss}");

        Assert.Equal(new[] { Vless, hy2, ss }, lines);
    }

    [Fact]
    public void ExtractVlessLines_EmptyInputReturnsEmpty()
    {
        Assert.Empty(FreeConfigFetcher.ExtractVlessLines(string.Empty));
        Assert.Empty(FreeConfigFetcher.ExtractVlessLines(" \r\n\t"));
    }

    private sealed class BlockingHttpClient : IHttpClient
    {
        private readonly TaskCompletionSource<bool> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<HttpResponse> SendAsync(
            HttpRequest request,
            CancellationToken ct = default)
        {
            _started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable after an infinite delay.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public Task<IHttpStreamingResponse> SendStreamingAsync(
            HttpRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static FreeConfigSource Source() => new()
    {
        Name = "test-source",
        Url = SourceUrl,
        Enabled = true,
    };
}
