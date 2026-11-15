using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VPNRouter.App.Services;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

public sealed class BusinessPolicyHttpTests
{
    [Fact]
    public async Task Fetch_UsesOnlyProvisionedEndpointAndHeaderIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = BusinessPolicyConfigurationTests.Configuration(key);
        var expected = Encoding.UTF8.GetBytes("{\"synthetic\":true}");
        using var client = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(configuration.Endpoint, request.RequestUri);
            Assert.Equal(string.Empty, request.RequestUri!.Query);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(configuration.ReadToken, request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains("application/json", request.Headers.Accept.ToString());
            return Task.FromResult(Response(expected));
        }));
        using var http = new BusinessPolicyHttp(client);
        Assert.Equal(expected, await http.FetchAsync(configuration, CancellationToken.None));
    }

    [Theory]
    [InlineData(204, "application/json")]
    [InlineData(301, "application/json")]
    [InlineData(302, "application/json")]
    [InlineData(307, "application/json")]
    [InlineData(401, "application/json")]
    [InlineData(503, "application/json")]
    [InlineData(200, "text/html")]
    public async Task Fetch_RefusesUnexpectedStatusOrContentTypeWithoutExposingResponse(int status, string type)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = BusinessPolicyConfigurationTests.Configuration(key);
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = Response(Encoding.UTF8.GetBytes("synthetic-private-server-error"), (HttpStatusCode)status);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            response.Headers.Location = new Uri("https://other.invalid/untrusted");
            return Task.FromResult(response);
        }));
        using var http = new BusinessPolicyHttp(client);
        var error = await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(configuration, CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-private", error.Message);
        Assert.DoesNotContain(configuration.ReadToken, error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Fetch_RefusesAnInjectedClientThatFollowedARedirect()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new HttpClient(new Handler((_, _) =>
        {
            var response = Response("{}"u8.ToArray());
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.invalid/policy");
            return Task.FromResult(response);
        }));
        using var http = new BusinessPolicyHttp(client);
        await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BusinessPolicy.MaximumResponseBytes + 1)]
    public async Task Fetch_RejectsEmptyOrOversizedDeclaredBody(int length)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new byte[length]))));
        using var http = new BusinessPolicyHttp(client);
        await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_EnforcesDecodedLimitOnStreamingBodyWithoutLength()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stream = new CountingStream(new byte[BusinessPolicy.MaximumResponseBytes * 2]);
        using var content = new StreamingContent(stream);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var http = new BusinessPolicyHttp(client);
        Assert.Null(content.Headers.ContentLength);
        await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), CancellationToken.None));
        Assert.Equal(BusinessPolicy.MaximumResponseBytes + 1, stream.BytesRead);
        Assert.False(content.Serialized);
    }

    [Fact]
    public async Task Fetch_WholeBodyDeadline_IsBoundedAndSanitized()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stream = new BlockingStream();
        using var content = new StreamingContent(stream);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var clock = new ManualDeadline();
        using var http = new BusinessPolicyHttp(client, timeProvider: clock);
        var fetch = http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), CancellationToken.None);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        var error = await Assert.ThrowsAsync<IOException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Fetch_CallerCancellationInterruptsBodyReadAndPreservesCancellation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var stream = new BlockingStream();
        using var content = new StreamingContent(stream);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        using var http = new BusinessPolicyHttp(client);
        using var cancellation = new CancellationTokenSource();
        var fetch = http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Fetch_NetworkExceptionDoesNotExposeEndpointOrCredentials()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("synthetic-private-endpoint-error")));
        using var http = new BusinessPolicyHttp(client);
        var error = await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(BusinessPolicyConfigurationTests.Configuration(key), CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-private", error.Message);
    }

    private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly Stream _stream;
        internal bool Serialized { get; private set; }
        internal StreamingContent(Stream stream)
        {
            _stream = stream;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context)
        {
            Serialized = true;
            throw new InvalidOperationException("The response must be streamed.");
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            return read;
        }
    }

    private sealed class BlockingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ManualDeadline : TimeProvider
    {
        private CallbackTimer? _timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(10), dueTime);
            return _timer = new CallbackTimer(callback, state);
        }
        internal void Fire() => (_timer ?? throw new InvalidOperationException("Deadline not created.")).Fire();

        private sealed class CallbackTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
