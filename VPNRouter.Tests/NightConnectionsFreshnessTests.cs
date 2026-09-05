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

/// <summary>
/// Correctness and freshness tests for <see cref="ClashSingBoxApi.GetConnectionsAsync"/>
/// and <see cref="ConnectionsSnapshot"/>:
/// - Valid zero/nonzero success snapshots return IsValid = true.
/// - Failures (HTTP 500, bad JSON, missing required fields, timeout, cancellation)
///   return a failure snapshot with IsValid = false without throwing.
/// - Cancellation tokens and timeouts preserve the contract of returning failureSnapshot rather than throwing.
/// </summary>
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
    public void ConnectionsSnapshot_DefaultIsValid_IsTrue()
    {
        var snapshot = new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow);
        Assert.True(snapshot.IsValid);
    }

    [Fact]
    public void ConnectionsSnapshot_ExplicitIsValidFalse_IsFalse()
    {
        var snapshot = new ConnectionsSnapshot(0, 0L, 0L, DateTimeOffset.UtcNow) { IsValid = false };
        Assert.False(snapshot.IsValid);
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
    public async Task GetConnectionsAsync_NonObjectJson_ReturnsInvalidSnapshot()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[1, 2, 3]", Encoding.UTF8, "application/json")
            }
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":200}")]
    [InlineData("{\"downloadTotal\":100,\"connections\":[]}")]
    [InlineData("{\"uploadTotal\":200,\"connections\":[]}")]
    public async Task GetConnectionsAsync_MissingFields_ReturnsInvalidSnapshot(string json)
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
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

        // Must NOT throw OperationCanceledException; must return failureSnapshot with IsValid = false
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

    [Fact]
    public async Task GetConnectionsAsync_TaskCanceledException_ReturnsInvalidSnapshotWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => throw new TaskCanceledException("simulated timeout")
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }

    [Fact]
    public async Task GetConnectionsAsync_NetworkException_ReturnsInvalidSnapshotWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler
        {
            Responder = _ => throw new HttpRequestException("connection refused")
        };

        var api = CreateApi(handler);
        var snapshot = await api.GetConnectionsAsync();

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }
}
