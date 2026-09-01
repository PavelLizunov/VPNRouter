// Phase 2 — 2D-3 (v3.0 refactor): contract tests for IHttpClient.
//
// Pins the expected behaviour of both implementations:
// 1. PolicyHttpClient (production) — happy path, timeout, retry, non-2xx.
// 2. FakeHttpClient (test double) — Setup canned response, SentRequests
//    capture.
//
// Brief: plans/phase2-2D-ihttpclient-2026-05-17.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Contract tests for <see cref="IHttpClient"/>. Two implementations
/// must pass: <see cref="PolicyHttpClient"/> (production) and
/// <see cref="FakeHttpClient"/> (test double).
/// </summary>
public sealed class IHttpClientContractTests
{
    private const string TestUrl = "https://test.example.invalid/api/resource";

    // ─── PolicyHttpClient contract ─────────────────────────────────────

    [Fact]
    public async Task Send_HappyPath_ReturnsResponse()
    {
        // Arrange — handler returns 200 with the expected body.
        const string expectedBody = "hello world";
        var handler = StubHandler.Sync((req, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(expectedBody, Encoding.UTF8, "text/plain"),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act
        var response = await http.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsSuccess());
        Assert.Equal(expectedBody, response.AsString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_Timeout_ThrowsTimeoutException()
    {
        // Arrange — handler blocks until the per-request timeout fires.
        var handler = StubHandler.Async(async (req, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act + Assert — 50 ms timeout should fire well before the 30 s delay.
        await Assert.ThrowsAsync<TimeoutException>(() => http.SendAsync(
            new HttpRequest(
                HttpMethod.Get,
                new Uri(TestUrl),
                Timeout: TimeSpan.FromMilliseconds(50)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Send_TimeoutWithRetry_RetriesThenSucceeds()
    {
        var attempt = 0;
        var handler = StubHandler.Async(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
        });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        var response = await http.SendAsync(new HttpRequest(
            HttpMethod.Get,
            new Uri(TestUrl),
            Timeout: TimeSpan.FromMilliseconds(50),
            RetryCount: 1,
            RetryBaseDelay: TimeSpan.FromMilliseconds(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Send_RetryCount2_RetriesTwiceOnTransientFailure()
    {
        // Arrange — first 2 attempts return 503 (transient), 3rd returns 200.
        var responses = new Queue<HttpStatusCode>(new[]
        {
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK,
        });
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(responses.Dequeue())
            {
                Content = new StringContent("ok"),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act — RetryCount=2 means up to 2 RETRIES after initial failure = 3 attempts total.
        var response = await http.SendAsync(new HttpRequest(
            HttpMethod.Get,
            new Uri(TestUrl),
            RetryCount: 2,
            RetryBaseDelay: TimeSpan.FromMilliseconds(1)),
            TestContext.Current.CancellationToken);

        // Assert — final success + handler hit exactly 3 times.
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(3, handler.CallCount);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task Send_NonSuccessStatus_DoesNotThrow_ReturnsResponse()
    {
        // Arrange — handler returns 404. By contract IHttpClient does NOT
        // throw on transport-success but app-failure; caller decides.
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing"),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act
        var response = await http.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(404, response.StatusCode);
        Assert.False(response.IsSuccess());
        Assert.Equal("missing", response.AsString());
    }

    [Fact]
    public async Task Send_OversizedBody_ThrowsInvalidDataException()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new ExpandingStream(PolicyHttpClient.MaxResponseBytes + 1024)),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Send_PerRequestBodyLimit_OverridesDefaultLimit()
    {
        const long perRequestLimit = 1024;
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ExpandingStream(perRequestLimit + 1)),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));
        var request = new HttpRequest(
            HttpMethod.Get,
            new Uri(TestUrl),
            RetryCount: 1,
            RetryBaseDelay: TimeSpan.FromMilliseconds(1))
        {
            MaxResponseBytes = perRequestLimit,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => http.SendAsync(
            request,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_PerRequestBodyLimit_CannotRaiseGlobalCap()
    {
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new PolicyHttpClient(new HttpClient(handler));
        var request = new HttpRequest(HttpMethod.Get, new Uri(TestUrl))
        {
            MaxResponseBytes = PolicyHttpClient.MaxResponseBytes + 1,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => http.SendAsync(
            request,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.CallCount);
    }

    // ─── FakeHttpClient contract ───────────────────────────────────────

    [Fact]
    public async Task FakeHttpClient_Setup_ReturnsCannedResponse()
    {
        // Arrange
        var fake = new FakeHttpClient()
            .Setup(TestUrl, "canned payload", statusCode: 201);

        // Act
        var response = await fake.SendAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            TestContext.Current.CancellationToken);

        // Assert — the route's canned response surfaces 1:1.
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("canned payload", response.AsString());
    }

    [Fact]
    public async Task FakeHttpClient_SentRequests_RecordsAllCalls()
    {
        // Arrange — single route, multiple invocations.
        var fake = new FakeHttpClient().Setup(TestUrl, "{}");

        // Act — fire 3 distinct requests.
        var ct = TestContext.Current.CancellationToken;
        await fake.SendAsync(new HttpRequest(HttpMethod.Get, new Uri(TestUrl + "?a=1")), ct);
        await fake.SendAsync(new HttpRequest(HttpMethod.Post, new Uri(TestUrl), Body: new byte[] { 1, 2 }, BodyContentType: "application/octet-stream"), ct);
        await fake.SendAsync(new HttpRequest(HttpMethod.Get, new Uri(TestUrl + "?a=2")), ct);

        // Assert — all 3 captured in call order with the right shape.
        var sent = fake.SentRequests;
        Assert.Equal(3, sent.Count);
        Assert.Equal(HttpMethod.Get, sent[0].Method);
        Assert.Contains("a=1", sent[0].Uri.ToString());
        Assert.Equal(HttpMethod.Post, sent[1].Method);
        Assert.Equal(new byte[] { 1, 2 }, sent[1].Body);
        Assert.Equal(HttpMethod.Get, sent[2].Method);
        Assert.Contains("a=2", sent[2].Uri.ToString());
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="HttpMessageHandler"/> for testing
    /// <see cref="PolicyHttpClient"/> end-to-end without real network.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private int _callCount;

        private StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        public static StubHandler Sync(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
            new((req, ct) => Task.FromResult(respond(req, ct)));

        public static StubHandler Async(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
            new(respond);

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return _respond(request, cancellationToken);
        }
    }

    /// <summary>Yields <paramref name="totalBytes"/> zero-bytes without allocating.</summary>
    private sealed class ExpandingStream(long totalBytes) : Stream
    {
        private long _remaining = totalBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        // Read-only, non-seekable stream: Stream's remaining abstract
        // members must be overridden to compile. Length/Position/Seek/
        // SetLength/Write are unsupported; Flush is a no-op.
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, n);
            _remaining -= n;
            return n;
        }
    }
}
