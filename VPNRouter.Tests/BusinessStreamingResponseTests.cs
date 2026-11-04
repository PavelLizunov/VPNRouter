using System.Net;
using System.Net.Http;
using System.Text;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class BusinessStreamingResponseTests
{
    [Fact]
    public async Task Fetch_StopsOversizedStreamWithoutContentLength_BeforeConsumingTheWholeResponse()
    {
        const int limit = 1024 * 1024;
        using var content = new NoLengthContent(new byte[limit * 2]);
        Assert.Null(content.Headers.ContentLength);
        using var client = new HttpClient(new ResponseHandler(content));
        using var http = new BusinessClientHttp(client);

        await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));

        Assert.InRange(content.BytesRead, limit + 1, limit + 64 * 1024);
        Assert.True(content.BytesRead < limit * 2);
        Assert.False(content.SerializedInsteadOfStreamed);
    }

    [Fact]
    public async Task Fetch_AcceptsCompleteValidStreamWithoutContentLength()
    {
        var payload = Encoding.UTF8.GetBytes(BusinessClientConfigurationTests.Four);
        using var content = new NoLengthContent(payload);
        Assert.Null(content.Headers.ContentLength);
        using var client = new HttpClient(new ResponseHandler(content));
        using var http = new BusinessClientHttp(client);

        var body = await http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None);

        Assert.Equal(BusinessClientConfigurationTests.Four, body);
        Assert.Equal(payload.Length, content.BytesRead);
        Assert.False(content.SerializedInsteadOfStreamed);
    }

    private sealed class ResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class NoLengthContent(byte[] payload) : HttpContent
    {
        internal int BytesRead { get; private set; }
        internal bool SerializedInsteadOfStreamed { get; private set; }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            SerializedInsteadOfStreamed = true;
            throw new InvalidOperationException("Fixture must exercise the response stream directly.");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CountingStream(payload, read => BytesRead += read));

        private sealed class CountingStream(byte[] bytes, Action<int> onRead) : Stream
        {
            private readonly MemoryStream _inner = new(bytes, writable: false);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                onRead(read);
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                var read = await _inner.ReadAsync(buffer, ct);
                onRead(read);
                return read;
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
}
