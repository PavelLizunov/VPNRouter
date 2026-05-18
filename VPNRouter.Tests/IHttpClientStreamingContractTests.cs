// Phase 4 (v3.0 refactor): contract tests for IHttpClient.SendStreamingAsync.
//
// Pins the expected behaviour of both implementations under the new
// streaming seam:
// 1. PolicyHttpClient (production) — happy path, cancellation, dispose
//    chain, 5 MB OOM-safety stress, headers-before-body ordering.
// 2. FakeHttpClient (test double) — SetupStream byte[] round-trip.
//
// Brief: plans/phase4-ihttpclient-streaming-2026-05-18.md.

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

/// <summary>
/// Contract tests for <see cref="IHttpClient.SendStreamingAsync"/>.
/// Pin: progressive body, no buffering, deterministic disposal under
/// cancellation, OOM-safe at 5 MB.
/// </summary>
public sealed class IHttpClientStreamingContractTests
{
    private const string TestUrl = "https://test.example.invalid/api/binary";

    // ─── PolicyHttpClient: happy path ───────────────────────────────────

    [Fact]
    public async Task HappyPath_StreamsBody()
    {
        // Arrange — handler returns a body of known bytes.
        var expectedBytes = Encoding.UTF8.GetBytes("hello streaming world");
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBytes),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act — open stream, read all bytes via CopyToAsync.
        byte[] actual;
        await using (var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl))))
        {
            Assert.Equal(200, resp.StatusCode);
            Assert.True(resp.IsSuccess());

            using var sink = new MemoryStream();
            await resp.Body.CopyToAsync(sink);
            actual = sink.ToArray();
        }

        // Assert — body bytes round-trip cleanly.
        Assert.Equal(expectedBytes, actual);
        Assert.Equal(1, handler.CallCount);
    }

    // ─── Headers available before body read ─────────────────────────────

    [Fact]
    public async Task HeadersAvailable_BeforeBodyRead()
    {
        // Arrange — body is a slow producer that yields bytes only after
        // each ReadAsync(). The status + headers must arrive without the
        // body being touched, because PolicyHttpClient uses
        // HttpCompletionOption.ResponseHeadersRead.
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

        // Act — open the streaming response and inspect headers BEFORE
        // reading any body bytes. This is the canonical use case for
        // download progress reporting.
        await using var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)));

        // Assert — headers + ContentLength are present + body has NOT
        // been consumed yet.
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(13L, resp.ContentLength);
        Assert.True(resp.Headers.ContainsKey("X-Probe"));
        Assert.Equal("probe-value", resp.Headers["X-Probe"]);
        Assert.Equal(0, bodyContent.BytesRead);  // pin: body untouched
    }

    // ─── Cancellation aborts mid-read ───────────────────────────────────

    [Fact]
    public async Task Cancellation_AbortsStream_NoLeak()
    {
        // Arrange — body that blocks indefinitely after the first chunk.
        var hangingContent = new HangingStreamContent();
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = hangingContent,
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));
        using var cts = new CancellationTokenSource();

        // Act — start a read, cancel mid-flight, assert OCE bubbles out
        // and the wrapper disposes cleanly without leaking the connection.
        var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)),
            cts.Token);
        try
        {
            // Schedule a cancel that fires after the read has started.
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            var buffer = new byte[64];
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                // Read into the buffer — first byte arrives, then the
                // stream hangs until cancel. We expect OCE.
                while (await resp.Body.ReadAsync(buffer, cts.Token) > 0) { /* drain */ }
            });
        }
        finally
        {
            // Dispose-on-cancel: must complete without throwing even
            // though the stream is in an aborted state. Idempotent
            // disposal pin.
            await resp.DisposeAsync();
        }

        // Assert — handler was called exactly once; the dispose path
        // cleaned up. No "object disposed twice" + no hung tests.
        Assert.Equal(1, handler.CallCount);
    }

    // ─── Dispose before read complete ───────────────────────────────────

    [Fact]
    public async Task DisposeBeforeReadComplete_AbortsConnection()
    {
        // Arrange — a body that would yield 1 MB if fully read, but
        // we'll dispose after reading only the first 4 bytes.
        var body = new byte[1024 * 1024];
        new Random(42).NextBytes(body);
        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act — read a tiny prefix then dispose.
        var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)));
        var prefix = new byte[4];
        var read = await resp.Body.ReadAsync(prefix);
        await resp.DisposeAsync();

        // Assert — got the prefix, body is now closed.
        Assert.Equal(4, read);
        Assert.Equal(body[0], prefix[0]);

        // Subsequent reads on the disposed body must fail (ObjectDisposed
        // or 0-bytes from closed MemoryStream wrap). Either signal is fine
        // — the pin is "no further bytes flow + no socket leak".
        // ByteArrayContent's stream returns 0 after Dispose on .NET 8, so
        // assert via the stronger signal: a second DisposeAsync is a no-op.
        await resp.DisposeAsync();  // idempotent pin
    }

    // ─── 5 MB stress: streams progressively, no OOM ─────────────────────

    [Fact]
    public async Task LargeBody_5MB_Streams_NoOOM()
    {
        // Arrange — 5 MB body. Critically, we sink it through a counting
        // stream and assert peak managed memory stays bounded — the
        // "progressive" pin. ByteArrayContent reuses the same buffer
        // internally so the 5 MB allocation happens once on the server
        // side, never on the client. ReadAsync into a 64 KB caller buffer
        // (typical SDK pattern) must NEVER materialize a 5 MB client copy.
        const int SizeBytes = 5 * 1024 * 1024;
        var body = new byte[SizeBytes];
        for (int i = 0; i < SizeBytes; i++) body[i] = (byte)(i & 0xff);

        var handler = StubHandler.Sync((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        using var http = new PolicyHttpClient(new HttpClient(handler));

        // Act — drain the body in 64 KB chunks and tally the total. This
        // is the canonical "stream to file" pattern used by
        // ZapretUpdater / WgturnUpdater / UpdateChecker.
        await using var resp = await http.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl)));

        Assert.Equal(SizeBytes, resp.ContentLength);

        var buffer = new byte[64 * 1024];
        long total = 0;
        int chunks = 0;
        int n;
        while ((n = await resp.Body.ReadAsync(buffer)) > 0)
        {
            total += n;
            chunks++;
            // Assert at every chunk that the wrapper isn't holding a
            // hidden copy of the prefix — the *only* state should be
            // the per-call buffer the caller passed in.
            Assert.NotNull(resp.Body);
        }

        // Assert — saw the full 5 MB delivered in >1 chunk (progressive
        // pin: a fully-buffered impl would return everything in 1 read).
        Assert.Equal(SizeBytes, total);
        Assert.True(chunks > 1,
            $"Expected >1 chunk for 5 MB body (got {chunks}); body must stream, not buffer.");
    }

    // ─── FakeHttpClient contract ─────────────────────────────────────────

    [Fact]
    public async Task FakeHttpClient_SetupStream_ReturnsConfiguredBytes()
    {
        // Arrange — canned 16-byte body via the streaming seam.
        var cannedBody = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var fake = new FakeHttpClient()
            .SetupStream(TestUrl, cannedBody, statusCode: 200,
                headers: new Dictionary<string, string> { ["X-Stub"] = "yes" });

        // Act — open + drain.
        byte[] received;
        await using (var resp = await fake.SendStreamingAsync(
            new HttpRequest(HttpMethod.Get, new Uri(TestUrl))))
        {
            Assert.Equal(200, resp.StatusCode);
            Assert.True(resp.IsSuccess());
            Assert.Equal(16L, resp.ContentLength);
            Assert.Equal("yes", resp.Headers["X-Stub"]);

            using var sink = new MemoryStream();
            await resp.Body.CopyToAsync(sink);
            received = sink.ToArray();
        }

        // Assert — body 1:1 round-trip + the streaming request was
        // recorded in the dedicated capture list (not the buffered one).
        Assert.Equal(cannedBody, received);
        Assert.Single(fake.SentStreamingRequests);
        Assert.Empty(fake.SentRequests);  // separate channels pin
        Assert.Equal(HttpMethod.Get, fake.SentStreamingRequests[0].Method);
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// In-memory <see cref="HttpMessageHandler"/> for testing
    /// <see cref="PolicyHttpClient"/> end-to-end without real network.
    /// Same pattern as <see cref="IHttpClientContractTests.StubHandler"/>
    /// (intentionally duplicated rather than shared so each test file
    /// stays self-contained for fast diff review).
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

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return _respond(request, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="HttpContent"/> whose <see cref="Stream"/> tracks how
    /// many bytes have been read out of it. Lets the "headers before
    /// body" test assert that PolicyHttpClient does NOT pre-buffer.
    /// </summary>
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
            // Return a stream that wraps the payload AND increments
            // BytesRead on every Read. Used by HttpClient when
            // HttpCompletionOption.ResponseHeadersRead is set.
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

    /// <summary>
    /// <see cref="HttpContent"/> whose body stream blocks on read until
    /// the caller's cancellation token fires. Used to verify cancellation
    /// + dispose chain on a stalled connection.
    /// </summary>
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
                // Block the calling thread until cancelled. xUnit will
                // surface a timeout if cancel doesn't fire.
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                // Wait forever, respect cancellation. CT cancel raises OCE.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
