#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class NightConnectionsFreshnessTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Responder is not null)
            {
                return Task.FromResult(Responder(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static ClashSingBoxApi CreateApi(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new ClashSingBoxApi(httpClient: httpClient, baseUrl: "http://127.0.0.1:9090");
    }

    [Fact]
    public async Task GetConnectionsAsync_ValidZero_ReturnsValidSnapshot()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":[]}",
                    Encoding.UTF8,
                    "application/json")
            }
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.True(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
        Assert.Equal(0L, snapshot.TotalDownloadBytes);
        Assert.Equal(0L, snapshot.TotalUploadBytes);
        Assert.True(snapshot.CapturedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetConnectionsAsync_ValidNonzero_ReturnsValidSnapshot()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\":123456,\"uploadTotal\":654321,\"connections\":[{\"id\":\"c1\"},{\"id\":\"c2\"}]}",
                    Encoding.UTF8,
                    "application/json")
            }
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.True(snapshot.IsValid);
        Assert.Equal(2, snapshot.ActiveCount);
        Assert.Equal(123456L, snapshot.TotalDownloadBytes);
        Assert.Equal(654321L, snapshot.TotalUploadBytes);
    }

    [Fact]
    public async Task GetConnectionsAsync_Http500_ReturnsInvalidSnapshot()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
        Assert.Equal(0L, snapshot.TotalDownloadBytes);
        Assert.Equal(0L, snapshot.TotalUploadBytes);
    }

    [Fact]
    public async Task GetConnectionsAsync_BadJson_ReturnsInvalidSnapshot()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{malformed json", Encoding.UTF8, "application/json")
            }
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }

    [Fact]
    public async Task GetConnectionsAsync_PreCancellationToken_ReturnsInvalidSnapshotWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"downloadTotal\":10,\"uploadTotal\":20,\"connections\":[]}",
                    Encoding.UTF8,
                    "application/json")
            }
        };

        var api = CreateApi(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snapshot = await api.GetConnectionsAsync(cts.Token);

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
        Assert.Equal(0L, snapshot.TotalDownloadBytes);
        Assert.Equal(0L, snapshot.TotalUploadBytes);
    }

    [Fact]
    public async Task GetConnectionsAsync_TimeoutOrCancellationInHandler_ReturnsInvalidSnapshotWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => throw new OperationCanceledException("simulated timeout or cancellation")
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }
}
