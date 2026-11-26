#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ClashConnectionsStreamParseTests
{
    private const string ValidJson =
        "{\"downloadTotal\":12345,\"uploadTotal\":678,\"connections\":[" +
        "{\"id\":\"a\",\"metadata\":{\"host\":\"x\",\"nested\":[1,2,3]}}," +
        "{\"id\":\"b\"},null,42,\"primitive\"]}";

    private static async Task<ClashSingBoxApi.ConnectionsParseResult> ParseAsync(
        string json, int chunkSize, long maxBytes = 1024 * 1024)
    {
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(json), chunkSize);
        return await ClashSingBoxApi.ParseConnectionsSummaryAsync(
            stream, maxBytes, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ParsesAcrossSingleByteChunks()
    {
        var result = await ParseAsync(ValidJson, chunkSize: 1);

        Assert.True(result.Ok);
        Assert.Equal(12345, result.Download);
        Assert.Equal(678, result.Upload);
        Assert.Equal(5, result.ActiveCount);
    }

    [Fact]
    public async Task ParsesAcrossPrimeSizedChunks()
    {
        var result = await ParseAsync(ValidJson, chunkSize: 7);

        Assert.True(result.Ok);
        Assert.Equal(5, result.ActiveCount);
    }

    [Fact]
    public async Task PropertyOrderIndependent_AcrossChunks()
    {
        var result = await ParseAsync(
            "{\"connections\":[{\"id\":\"a\"}],\"uploadTotal\":9,\"downloadTotal\":8}",
            chunkSize: 3);

        Assert.True(result.Ok);
        Assert.Equal(8, result.Download);
        Assert.Equal(9, result.Upload);
        Assert.Equal(1, result.ActiveCount);
    }

    [Fact]
    public async Task NullConnections_CountsZero()
    {
        var result = await ParseAsync(
            "{\"downloadTotal\":5,\"uploadTotal\":6,\"connections\":null}",
            chunkSize: 2);

        Assert.True(result.Ok);
        Assert.Equal(0, result.ActiveCount);
    }

    [Fact]
    public async Task EmptyConnections_CountsZero()
    {
        var result = await ParseAsync(
            "{\"downloadTotal\":5,\"uploadTotal\":6,\"connections\":[]}",
            chunkSize: 1);

        Assert.True(result.Ok);
        Assert.Equal(0, result.ActiveCount);
    }

    [Fact]
    public async Task NestedElements_AreCountedOnce()
    {
        var json = "{\"downloadTotal\":1,\"uploadTotal\":2,\"connections\":[" +
                   "[1,2,3],{\"nested\":{\"deep\":[4]}},[]]}";

        var result = await ParseAsync(json, chunkSize: 1);

        Assert.True(result.Ok);
        Assert.Equal(3, result.ActiveCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"string\"")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task NonObject_IsInvalid(string json)
    {
        var result = await ParseAsync(json, chunkSize: 1);
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50}")]
    [InlineData("{\"downloadTotal\":100,\"connections\":[]}")]
    [InlineData("{\"uploadTotal\":50,\"connections\":[]}")]
    public async Task MissingRequiredFields_IsInvalid(string json)
    {
        var result = await ParseAsync(json, chunkSize: 1);
        Assert.False(result.Ok);
    }

    [Theory]
    [InlineData("{\"downloadTotal\":-1,\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":-1,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":1.5,\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":\"100\",\"uploadTotal\":50,\"connections\":[]}")]
    [InlineData("{\"downloadTotal\":100,\"uploadTotal\":50,\"connections\":{}}")]
    public async Task InvalidValues_AreInvalid(string json)
    {
        var result = await ParseAsync(json, chunkSize: 1);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task DuplicateField_IsInvalid()
    {
        var result = await ParseAsync(
            "{\"downloadTotal\":1,\"downloadTotal\":2,\"uploadTotal\":3,\"connections\":[]}",
            chunkSize: 1);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task TruncatedJson_IsInvalid()
    {
        var truncated = ValidJson[..^10];
        var result = await ParseAsync(truncated, chunkSize: 1);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task TrailingGarbage_IsInvalid()
    {
        var result = await ParseAsync(ValidJson + " extra", chunkSize: 1);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ResponseOverByteLimit_IsInvalid()
    {
        var result = await ParseAsync(ValidJson, chunkSize: 4, maxBytes: 32);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ResponseExactlyAtByteLimit_IsAccepted()
    {
        var bytes = Encoding.UTF8.GetByteCount(ValidJson);
        var result = await ParseAsync(ValidJson, chunkSize: 5, maxBytes: bytes);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task ResponseLargerThanTheReadBuffer_CountsConnectionsAcrossReads()
    {
        const int count = 10_000;
        var connections = string.Join(",", Enumerable.Repeat("{\"id\":\"sample\"}", count));
        var result = await ParseAsync(
            "{\"downloadTotal\":1,\"uploadTotal\":2,\"connections\":[" + connections + "]}", chunkSize: 16 * 1024);

        Assert.True(result.Ok);
        Assert.Equal(count, result.ActiveCount);
    }

    [Fact]
    public async Task OversizedSingleToken_IsInvalidEvenBelowTheResponseLimit()
    {
        var json = "{\"downloadTotal\":1,\"uploadTotal\":2,\"connections\":[\"" + new string('x', 70_000) + "\"]}";
        var result = await ParseAsync(json, chunkSize: 16 * 1024);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task StreamingParse_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClashSingBoxApi.ParseConnectionsSummaryAsync(
                new BlockingStream(), 1024, cts.Token));
    }

    [Fact]
    public async Task GetConnectionsAsync_StreamsChunkedBody()
    {
        var handler = new StaticHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new ChunkedStream(Encoding.UTF8.GetBytes(ValidJson), 3)),
            });

        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(
            httpClient: http, baseUrl: "http://127.0.0.1:9090");

        var snapshot = await api.GetConnectionsAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsValid);
        Assert.Equal(12345L, snapshot.TotalDownloadBytes);
        Assert.Equal(678L, snapshot.TotalUploadBytes);
        Assert.Equal(5, snapshot.ActiveCount);
    }

    [Fact]
    public async Task GetConnectionsAsync_ContentLengthOverLimit_DoesNotReadBody()
    {
        var content = new OversizedLengthContent(
            ClashSingBoxApi.MaxConnectionsResponseBytes + 1);
        var handler = new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(
            httpClient: http, baseUrl: "http://127.0.0.1:9090");

        var snapshot = await api.GetConnectionsAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.IsValid);
        Assert.False(content.ReadAttempted);
    }

    [Fact]
    public async Task GetConnectionsAsync_CancellationDuringStreamRead_ReturnsInvalid()
    {
        var handler = new StaticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream()),
        });

        using var http = new HttpClient(handler);
        using var api = new ClashSingBoxApi(
            httpClient: http, baseUrl: "http://127.0.0.1:9090");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var snapshot = await api.GetConnectionsAsync(cts.Token);

        Assert.False(snapshot.IsValid);
        Assert.Equal(0, snapshot.ActiveCount);
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class ChunkedStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _offset;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = Math.Max(1, chunkSize);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<int>(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> buffer)
        {
            if (_offset >= _data.Length || buffer.Length == 0)
                return 0;

            var count = Math.Min(
                Math.Min(buffer.Length, _chunkSize), _data.Length - _offset);
            _data.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OversizedLengthContent : HttpContent
    {
        private readonly long _length;

        public OversizedLengthContent(long length)
        {
            _length = length;
            Headers.ContentLength = length;
        }

        public bool ReadAttempted { get; private set; }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Body must not be read for an oversized response.");
        }
    }
}
