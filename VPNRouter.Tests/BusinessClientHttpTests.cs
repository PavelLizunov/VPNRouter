using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using VPNRouter.App.Services;

namespace VPNRouter.Tests;

public sealed class BusinessClientHttpTests
{
    [Fact]
    public async Task Fetch_AdvertisesCapabilitiesAndValidatesTheEntireResponse()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("VPNRouter", request.Headers.UserAgent.ToString());
            Assert.Equal(VPNRouter.Core.Services.SubscriptionFetcher.GetSubscriptionHeaders(secure: true)["X-VPNRouter-Capabilities"],
                Assert.Single(request.Headers.GetValues("X-VPNRouter-Capabilities")));
            await Task.Yield();
            return new(HttpStatusCode.OK) { Content = new StringContent(BusinessClientConfigurationTests.Four) };
        }));
        using var http = new BusinessClientHttp(client);
        Assert.Equal(BusinessClientConfigurationTests.Four, await http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));
    }

    [Theory]
    [InlineData(200, "invalid")]
    [InlineData(200, BusinessClientConfigurationTests.Three + "\npartial-row")]
    [InlineData(302, BusinessClientConfigurationTests.Four)]
    [InlineData(503, "private-error-body")]
    public async Task Fetch_RefusesInvalidOrUnsuccessfulResponses(int status, string response)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent(response) })));
        using var http = new BusinessClientHttp(client);
        await Assert.ThrowsAnyAsync<Exception>(() => http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_RejectsOversizedBody()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', 1024 * 1024 + 1)) })));
        using var http = new BusinessClientHttp(client);
        await Assert.ThrowsAsync<IOException>(() => http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_RejectsInvalidUtf8InsteadOfReplacingCredentialBytes()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[] { 0xff, 0xfe, 0x80 }) })));
        using var http = new BusinessClientHttp(client);
        await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() => http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));
    }

    [Theory]
    [InlineData(201, true)]
    [InlineData(302, false)]
    [InlineData(401, false)]
    [InlineData(503, false)]
    public async Task Send_UsesBoundedZipContract_RetainsReportOnEveryOutcome(int status, bool accepted)
    {
        var folder = Path.Combine(Path.GetTempPath(), "business-upload-" + Guid.NewGuid().ToString("N"));
        BusinessPrivateFiles.CreateDirectory(folder);
        var path = Path.Combine(folder, "report.zip");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        try
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(async (request, ct) =>
            {
                calls++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("synthetic-upload-identity", request.Headers.Authorization?.Parameter);
                Assert.Equal("vpnrouter-business-support/1", Assert.Single(request.Headers.GetValues("X-VPNRouter-Report-Format")));
                Assert.Equal("application/zip", request.Content!.Headers.ContentType?.MediaType);
                Assert.Equal(3L, request.Content.Headers.ContentLength.GetValueOrDefault());
                Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content.ReadAsByteArrayAsync(ct));
                return new((HttpStatusCode)status);
            }));
            using var http = new BusinessClientHttp(client);
            var send = http.SendAsync(new("https://example.invalid/api/reports"), "synthetic-upload-identity", path, CancellationToken.None);
            if (accepted) await send; else await Assert.ThrowsAsync<IOException>(() => send);
            Assert.True(File.Exists(path));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_TracksTheSameReceiptPastTheOriginalWait(bool deadlineExpires)
    {
        var directory = Path.Combine(Path.GetTempPath(), "business-analysis-" + Guid.NewGuid().ToString("N"));
        BusinessPrivateFiles.CreateDirectory(directory);
        var path = Path.Combine(directory, "report.zip");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        try
        {
            var id = new string('a', 64);
            var posts = 0;
            var polls = 0;
            var acknowledged = false;
            var clock = new ManualClock();
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Assert.Equal("synthetic-upload-identity", request.Headers.Authorization?.Parameter);
                if (request.Method == HttpMethod.Post) posts++;
                else
                {
                    Assert.True(acknowledged);
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal($"https://example.invalid/api/reports/{id}/analysis", request.RequestUri!.AbsoluteUri);
                    polls++;
                    if (polls == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    if (polls == 2) throw new OperationCanceledException("Simulated per-request HTTP timeout");
                }

                var ready = !deadlineExpires && polls == 16;
                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    report_id = id,
                    analysis = new { version = 1, state = ready ? "ready" : "pending", message_en = ready ? "Verified summary" : null }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }));
            using var http = new BusinessClientHttp(client, clock);
            var send = http.SendAsync(new("https://example.invalid/api/reports"), "synthetic-upload-identity",
                path, CancellationToken.None, () => acknowledged = true);
            Assert.Equal(TimeSpan.FromSeconds(8), (await clock.NextTimerAsync()).Due);
            var deadline = await clock.NextTimerAsync();
            Assert.Equal(TimeSpan.FromMinutes(2), deadline.Due);
            for (var poll = 0; poll < (deadlineExpires ? 3 : 16); poll++)
            {
                var delay = await clock.NextTimerAsync();
                Assert.Equal(TimeSpan.FromSeconds(3), delay.Due);
                delay.Fire();
            }
            if (deadlineExpires)
            {
                await clock.NextTimerAsync();
                deadline.Fire();
            }
            var reply = await send.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(acknowledged);
            Assert.Equal(1, posts);
            Assert.Equal(deadlineExpires ? 3 : 16, polls);
            Assert.Equal(id, reply.ReportId);
            Assert.Equal(deadlineExpires ? "queued" : "ready", reply.State);
            Assert.Equal(deadlineExpires ? null : "Verified summary", reply.MessageEn);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        internal Task<ManualTimer> NextTimerAsync() =>
            _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private int _disposed;
            internal TimeSpan Due => due;
            internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
