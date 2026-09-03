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
    public void DefaultConstructor_UsesSharedPolicyClient()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var fetcher = new FreeConfigFetcher(logger);
        var field = typeof(FreeConfigFetcher).GetField(
            "_http",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Same(PolicyHttpClient.Shared, field!.GetValue(fetcher));
    }

    [Fact]
    public async Task FetchAsync_UsesBoundedPolicyEnvelopeAndExtracts()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var http = new FakeHttpClient().Setup(SourceUrl, $"{Vless}\n{Vless}\n");
        var fetcher = new FreeConfigFetcher(logger, http);

        var result = await fetcher.FetchAsync(Source());

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

        var result = await fetcher.FetchAsync(Source());

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

        Assert.Equal(new[] { Vless }, await fetcher.FetchAsync(Source()));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(503)]
    public async Task FetchAsync_HttpFailureReturnsEmpty(int statusCode)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var http = new FakeHttpClient().Setup(SourceUrl, "failure", statusCode);
        var fetcher = new FreeConfigFetcher(logger, http);

        Assert.Empty(await fetcher.FetchAsync(Source()));
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
            Assert.Empty(await fetcher.FetchAsync(Source()));
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
