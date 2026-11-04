using System.Net;
using System.Net.Http;
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
            Assert.Equal("detour-v1", Assert.Single(request.Headers.GetValues("X-VPNRouter-Capabilities")));
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

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
