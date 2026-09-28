#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class IHttpClientStreamingContractTests
{
    private const string TestUrl = "https://test.example.invalid/api/binary";

    [Fact]
    public async Task HappyPath_StreamsBody()
    {
        var expectedBytes = Encoding.UTF8.GetBytes("hello streaming world");
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBytes),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        byte[] actual;
        var ct = TestContext.Current.CancellationToken;
        await using (var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)), ct))
        {
            Assert.Equal(200, resp.StatusCode);
            Assert.True(resp.IsSuccess());

            using var sink = new MemoryStream();
            await resp.Body.CopyToAsync(sink, ct);
            actual = sink.ToArray();
        }

        Assert.Equal(expectedBytes, actual);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task HeadersAvailable_BeforeBodyRead()
    {
        var bodyContent = new SlowStreamContent("payload-bytes"u8.ToArray());
        bodyContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        bodyContent.Headers.ContentLength = 13;

        var handler = StubHandler.Sync((_, _) =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = bodyContent,
            };
            msg.Headers.TryAddWithoutValidation("X-Probe", "probe-value");
            return msg;
        });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        await using var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            TestContext.Current.CancellationToken);

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(13L, resp.ContentLength);
        Assert.True(resp.Headers.ContainsKey("X-Probe"));
        Assert.Equal("probe-value", resp.Headers["X-Probe"]);
        Assert.Equal(0, bodyContent.BytesRead);
    }

    [Fact]
    public async Task Cancellation_AbortsStream_NoLeak()
    {
        var hangingContent = new HangingStreamContent();
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = hangingContent,
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();

        var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            cts.Token);
        try
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            var buffer = new byte[64];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (await resp.Body.ReadAsync(buffer, cts.Token) > 0) {  }
            });
        }
        finally
        {
            await resp.DisposeAsync();
        }

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DisposeBeforeReadComplete_AbortsConnection()
    {
        var body = new byte[1024 * 1024];
        new Random(42).NextBytes(body);
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        var ct = TestContext.Current.CancellationToken;
        var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)), ct);
        var prefix = new byte[4];
        var read = await resp.Body.ReadAsync(prefix, ct);
        await resp.DisposeAsync();

        Assert.Equal(4, read);
        Assert.Equal(body[0], prefix[0]);

        await resp.DisposeAsync();
    }

    [Fact]
    public async Task LargeBody_5MB_Streams_NoOOM()
    {
        const int SizeBytes = 5 * 1024 * 1024;
        var body = new byte[SizeBytes];
        for (int i = 0; i < SizeBytes; i++) body[i] = (byte)(i & 0xff);

        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        var ct = TestContext.Current.CancellationToken;
        await using var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)), ct);

        Assert.Equal(SizeBytes, resp.ContentLength);

        var buffer = new byte[64 * 1024];
        long total = 0;
        int chunks = 0;
        int n;
        while ((n = await resp.Body.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            chunks++;
            Assert.NotNull(resp.Body);
        }

        Assert.Equal(SizeBytes, total);
        Assert.True(chunks > 1,
            $"Expected >1 chunk for 5 MB body (got {chunks}); body must stream, not buffer.");
    }

    [Fact]
    public async Task FakeHttpClient_SetupStream_ReturnsConfiguredBytes()
    {
        var cannedBody = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var fake = new FakeHttpClient()
            .SetupStream(TestUrl, cannedBody, statusCode: 200,
                headers: new Dictionary<string, string> { ["X-Stub"] = "yes" });

        byte[] received;
        var ct = TestContext.Current.CancellationToken;
        await using (var resp = await fake.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)), ct))
        {
            Assert.Equal(200, resp.StatusCode);
            Assert.True(resp.IsSuccess());
            Assert.Equal(16L, resp.ContentLength);
            Assert.Equal("yes", resp.Headers["X-Stub"]);

            using var sink = new MemoryStream();
            await resp.Body.CopyToAsync(sink, ct);
            received = sink.ToArray();
        }

        Assert.Equal(cannedBody, received);
        Assert.Single(fake.SentStreamingRequests);
        Assert.Empty(fake.SentRequests);
        Assert.Equal(HttpMethod.Get, fake.SentStreamingRequests[0].Method);
    }

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

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return _respond(request, cancellationToken);
        }
    }

    private sealed class SlowStreamContent : HttpContent
    {
        private readonly byte[] _payload;
        public int BytesRead { get; private set; }

        public SlowStreamContent(byte[] payload) => _payload = payload;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            BytesRead += _payload.Length;
            return stream.WriteAsync(_payload, 0, _payload.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(new TrackedReadStream(_payload, n => BytesRead += n));
        }

        private sealed class TrackedReadStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly Action<int> _onRead;

            public TrackedReadStream(byte[] payload, Action<int> onRead)
            {
                _inner = new MemoryStream(payload, writable: false);
                _onRead = onRead;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = _inner.Read(buffer, offset, count);
                if (n > 0) _onRead(n);
                return n;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var n = await _inner.ReadAsync(buffer, cancellationToken);
                if (n > 0) _onRead(n);
                return n;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    private sealed class HangingStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new HangingStream());

        private sealed class HangingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
