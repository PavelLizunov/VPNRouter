using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Services;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ClashSingBoxApiReloadTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task ReloadConfigAsync_NeverCallsPutConfigs_AndReturnsFalse()
    {
        var handler = new RecordingHandler();
        using var api = new ClashSingBoxApi(new HttpClient(handler), "http://127.0.0.1:9090");

        var result = await api.ReloadConfigAsync("/tmp/current.json",
            TestContext.Current.CancellationToken);

        Assert.False(result, "The legacy PUT /configs endpoint cannot confirm an applied config.");
        Assert.Empty(handler.Requests);
    }
}
