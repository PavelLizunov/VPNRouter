using System.Net;
using System.Net.Http;
using System.Threading.Channels;
using VPNRouter.App.Services;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class BusinessClientHttpTests
{
    [Fact]
    public async Task Fetch_AdvertisesCapabilitiesAndValidatesTheEntireResponse()
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"VPNRouterBusiness/{AppVersion.Version}", request.Headers.UserAgent.ToString());
            Assert.Equal(OperatingSystem.IsWindows(), request.Headers.Contains("X-VPNRouter-Platform"));
            await Task.Yield();
            return new(HttpStatusCode.OK) { Content = new StringContent(BusinessClientConfigurationTests.Four) };
        }));
        using var http = new BusinessClientHttp(client);
        Assert.Equal(BusinessClientConfigurationTests.Four, await http.FetchAsync(new("https://example.invalid/sub"), CancellationToken.None));
    }

    private const string Core2 = "sing-box version 1.14.4-vpnctl.2";
    private const string Core3 = "sing-box version 1.14.4-vpnctl.3";

    public static TheoryData<string?, string?, bool, string?, bool> EqualSizeCoreUpdate
    {
        get
        {
            var windowsBusiness = OperatingSystem.IsWindows() && ProductEdition.IsBusiness;
            return new() { { Core3, Core2, windowsBusiness, windowsBusiness ? "1.14.4-vpnctl.3" : "1.14.4-vpnctl.2", true } };
        }
    }

    [Theory]
    [InlineData(Core3, null, true, "1.14.4-vpnctl.3", true)]
    [InlineData(Core3 + "\nTags: with_awg", Core2, true, "1.14.4-vpnctl.3", true)]
    [MemberData(nameof(EqualSizeCoreUpdate))]
    [InlineData(Core3, Core3, false, "1.14.4-vpnctl.3", true)]
    [InlineData(null, "sing-box version 1.14.4", false, "1.14.4", false)]
    [InlineData(null, "sing-box version invalid value", false, null, false)]
    [InlineData(null, null, false, null, false)]
    public async Task WindowsSubscriptionAndPreconnectProbeUseTheNextStartupExecutable(
        string? bundle, string? installed, bool selectBundle, string? version, bool dnstt)
    {
        var root = Directory.CreateTempSubdirectory("business-runtime-");
        try
        {
            var deployment = new SingBoxDeployment(Path.Combine(root.FullName, "bundle", "sing-box.exe"),
                Path.Combine(root.FullName, "bin", "sing-box.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(deployment.Bundled)!);
            Directory.CreateDirectory(Path.GetDirectoryName(deployment.Deployed)!);
            var nativeLibrary = Path.Combine(Path.GetDirectoryName(deployment.Bundled)!, "libcronet.dll");
            File.WriteAllText(nativeLibrary, "synthetic library");
            if (bundle != null) File.WriteAllText(deployment.Bundled, bundle);
            if (installed != null) File.WriteAllText(deployment.Deployed, installed);
            var expected = selectBundle ? deployment.Bundled : deployment.Deployed;
            Assert.Equal(expected, AppPaths.SingBoxDeploymentSource(deployment.Bundled, deployment.Deployed));

            string? inspected = null;
            string ReadVersion(string path)
            {
                inspected = path;
                return File.ReadAllText(path);
            }
            using var client = new HttpClient(new Handler((request, _) =>
            {
                Assert.Equal($"VPNRouterBusiness/{AppVersion.Version}", request.Headers.UserAgent.ToString());
                Assert.Equal("windows", Assert.Single(request.Headers.GetValues("X-VPNRouter-Platform")));
                Assert.Equal(version, request.Headers.TryGetValues("X-VPNRouter-Core-Version", out var values)
                    ? Assert.Single(values) : null);
                Assert.Equal(dnstt ? "detour-v1, dnstt-v1" : "detour-v1",
                    Assert.Single(request.Headers.GetValues("X-VPNRouter-Capabilities")));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(BusinessClientConfigurationTests.Three) });
            }));
            using var http = new BusinessClientHttp(client, subscriptionRuntime: () =>
                (true, SingBoxFeatures.ReadDesktopFeatures(deployment, ReadVersion).Version));
            var ct = TestContext.Current.CancellationToken;
            await http.FetchAsync(new("https://example.invalid/api/v1/app/config/pilot"), ct);

            var runner = new FakeProcessRunner().OnStart(_ => true, _ => throw new IOException("Synthetic stop before networking"));
            var verifier = new VlessDeepVerifier(Serilog.Log.Logger, deployment, runner);
            var entry = VlessDeepVerifierTests.CleanVlessEntry();
            var result = await verifier.VerifyAsync(entry, [entry], measureBandwidth: false, ct);
            Assert.Equal(DeepVerifyFailurePhase.LocalSpawn, result.FailurePhase);
            if (bundle == null && installed == null)
            {
                Assert.Null(inspected);
                Assert.Empty(runner.StartCalls);
            }
            else
            {
                Assert.Equal(expected, inspected);
                Assert.Equal(expected, Assert.Single(runner.StartCalls).ExecutablePath);
                if (OperatingSystem.IsWindows())
                    Assert.Equal("synthetic library", File.ReadAllText(Path.Combine(Path.GetDirectoryName(expected)!, "libcronet.dll")));
            }
        }
        finally { root.Delete(recursive: true); }
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
