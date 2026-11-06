using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VPNRouter.App.Services;
using VPNRouter.Core.Services.UpdateSources;

namespace VPNRouter.Tests;

public sealed class BusinessUpdateSourceTests
{
    private static readonly Uri Feed = new("https://updates.example.invalid/business/latest.json?private=fixture");
    private static string Manifest(string version = "2.50.6") => JsonSerializer.Serialize(new
    {
        schema = 1, edition = "Business", architecture = "win-x64", version,
        size = 12345, sha256 = new string('a', 64),
        asset = $"VPNRouterBusiness-update-v{version}-win-x64.zip",
        url = $"https://updates.example.invalid/business/VPNRouterBusiness-update-v{version}-win-x64.zip"
    });

    [Fact]
    public async Task TrustedManifest_ReusesInstallerAndPreparesBeforeApply()
    {
        var installer = new Installer();
        using var http = Client(Manifest());
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer,
            () => { installer.Events.Add("prepare"); return Task.CompletedTask; }, http, true);
        var info = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal("business", source.SourceId);
        Assert.Equal("2.50.6", info.Version);
        Assert.Equal(new string('a', 64), info.AssetSha256);
        var staged = await source.DownloadAsync(info, ct: TestContext.Current.CancellationToken);
        Assert.True(await source.ApplyAsync(info, staged, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "download", "prepare", "apply" }, installer.Events);
        Assert.Same(info, installer.Applied);
    }

    [Theory]
    [InlineData("2.50.4")]
    [InlineData("2.50.5")]
    public async Task CurrentOrOlderVersion_IsNotOffered(string version)
    {
        using var http = Client(Manifest(version));
        var installer = new Installer();
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer, http: http, windowsX64: true);
        Assert.Null(await source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);
    }

    [Theory]
    [InlineData("edition", "Standard")]
    [InlineData("architecture", "win-arm64")]
    [InlineData("sha256", "invalid")]
    [InlineData("url", "http://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.6-win-x64.zip")]
    [InlineData("url", "https://other.example.invalid/business/VPNRouterBusiness-update-v2.50.6-win-x64.zip")]
    [InlineData("url", "https://updates.example.invalid/business/VPNRouterBusiness-update-v2.50.6-win-x64.zip?secret=fixture")]
    [InlineData("asset", "VPNRouter-v2.50.6-win.zip")]
    [InlineData("version", "v2.50.6")]
    public async Task IncompatibleManifest_RefusesBeforeInstaller(string property, string value)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Manifest())!;
        data[property] = JsonSerializer.SerializeToElement(value);
        using var http = Client(JsonSerializer.Serialize(data));
        var installer = new Installer();
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer, http: http, windowsX64: true);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private=fixture", error.ToString());
        Assert.Empty(installer.Events);
    }

    [Theory]
    [InlineData("{\"schema\":1,\"schema\":1}")]
    [InlineData("[]")]
    [InlineData("{\"schema\":1}")]
    public async Task MalformedManifest_IsNotAccepted(string json)
    {
        using var http = Client(json);
        var source = new BusinessUpdateSource(Feed, "2.50.5", new Installer(), http: http, windowsX64: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingFeedOrUnsupportedPlatform_DoesNotFetchOrFallBack()
    {
        var handler = new Handler(() => throw new InvalidOperationException("unexpected HTTP"));
        using var http = new HttpClient(handler);
        foreach (var source in new[]
        {
            new BusinessUpdateSource(null, "2.50.5", new Installer(), http: http, windowsX64: true),
            new BusinessUpdateSource(Feed, "2.50.5", new Installer(), http: http, windowsX64: false)
        })
        {
            Assert.False(source.IsConfigured);
            Assert.Null(await source.CheckAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task UnknownLengthOverLimitResponse_IsBoundedAndRedacted()
    {
        var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string(' ', BusinessUpdateSource.ManifestLimit + 1))));
        content.Headers.ContentType = new("application/json");
        Assert.Null(content.Headers.ContentLength);
        using var http = new HttpClient(new Handler(() => new(HttpStatusCode.OK)
        {
            Content = content
        }));
        var source = new BusinessUpdateSource(Feed, "2.50.5", new Installer(), http: http, windowsX64: true);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Feed.AbsoluteUri, error.ToString());
    }

    [Fact]
    public async Task ForgedOfferAndFailedPreparation_NeverApply()
    {
        using var http = Client(Manifest());
        var installer = new Installer();
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer,
            () => throw new InvalidOperationException("still connecting"), http, true);
        var info = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadAsync(info with { DownloadUrl = "https://other.example.invalid/a.zip" }));
        await source.DownloadAsync(info, ct: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ApplyAsync(info, "staged"));
        Assert.Equal(new[] { "download" }, installer.Events);
    }

    [Fact]
    public async Task InstallerRefusal_DoesNotReportSuccessfulApply()
    {
        using var http = Client(Manifest());
        var source = new BusinessUpdateSource(Feed, "2.50.5", new Installer { Accept = false },
            () => Task.CompletedTask, http, true);
        var info = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        await source.DownloadAsync(info, ct: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => source.ApplyAsync(info, "staged"));
    }

    [Fact]
    public async Task LaterCheck_DoesNotInvalidateTheVerifiedDownloadedOffer()
    {
        using var http = Client(Manifest());
        var installer = new Installer();
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer,
            () => Task.CompletedTask, http, true);
        var first = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        var path = await source.DownloadAsync(first, ct: TestContext.Current.CancellationToken);
        var later = Assert.IsType<UpdateSourceInfo>(await source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.NotSame(first, later);
        Assert.True(await source.ApplyAsync(first, path, TestContext.Current.CancellationToken));
        Assert.Same(first, installer.Applied);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ApplyAsync(later, path));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(536870913L)]
    public async Task InvalidDeclaredAssetSize_NeverReachesDownloader(long size)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Manifest())!;
        data["size"] = JsonSerializer.SerializeToElement(size);
        using var http = Client(JsonSerializer.Serialize(data));
        var installer = new Installer();
        var source = new BusinessUpdateSource(Feed, "2.50.5", installer, http: http, windowsX64: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Empty(installer.Events);
    }

    private static HttpClient Client(string json) => new(new Handler(() => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response()); }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Installer : IDesktopInstaller
    {
        internal readonly List<string> Events = new();
        internal UpdateSourceInfo? Applied;
        internal bool Accept = true;
        public Task<string> DownloadAndStageAsync(UpdateSourceInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct)
        { Events.Add("download"); return Task.FromResult("staged"); }
        public Task<bool> ApplyStagedAsync(UpdateSourceInfo info, string stagedPath, CancellationToken ct)
        { Events.Add("apply"); Applied = info; return Task.FromResult(Accept); }
    }
}
