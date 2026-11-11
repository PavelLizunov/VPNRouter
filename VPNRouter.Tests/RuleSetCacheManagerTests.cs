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

public sealed class RuleSetCacheManagerTests : IDisposable
{
    private readonly string _tempCacheDir;

    public RuleSetCacheManagerTests()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(),
            "vpnr-rsc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempCacheDir, recursive: true); } catch { }
    }

    private string ExpectedCachedFile(string filename)
        => Path.Combine(_tempCacheDir, RuleSetCacheManager.CacheSubdir, filename);

    [Fact]
    public async Task EnsureLocal_FreshCacheBelowMaxAge_UsesCachedNoFetch()
    {
        var filename = "test-fresh.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = RuleSetSamples.UpstreamGeositeTldRu();
        File.WriteAllBytes(path, bytes);

        var counting = new CountingHandler();
        var client = new HttpClient(counting);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.Equal(0, counting.RequestCount);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task EnsureLocal_StaleCache_FetchSucceeds_OverwritesAndReturns()
    {
        var filename = "test-stale.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RuleSetSamples.UpstreamGeositeTldRu());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

        var freshBody = RuleSetSamples.IntactEnvelope(64);
        var handler = new StaticResponseHandler(HttpStatusCode.OK, freshBody);
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(freshBody, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task EnsureLocal_StaleCache_FetchFails_ReturnsStale()
    {
        var filename = "test-fallback.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stale = RuleSetSamples.UpstreamGeositeTldRu();
        File.WriteAllBytes(path, stale);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));

        var handler = new ThrowingHandler(new HttpRequestException("simulated DNS fail"));
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(stale, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task EnsureLocal_NoCache_FetchFails_ReturnsNull()
    {
        var filename = "test-nofallback.srs";

        var handler = new ThrowingHandler(new HttpRequestException("offline"));
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(File.Exists(ExpectedCachedFile(filename)));
    }

    [Fact]
    public async Task EnsureLocal_NoCache_FetchSucceeds_CachesAndReturns()
    {
        var filename = "test-firstfetch.srs";
        var path = ExpectedCachedFile(filename);
        Assert.False(File.Exists(path));

        var body = RuleSetSamples.UpstreamGeositeTldRu();
        var handler = new StaticResponseHandler(HttpStatusCode.OK, body);
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.True(File.Exists(path));
        Assert.Equal(body, await File.ReadAllBytesAsync(path, ct));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureLocal_FetchReturnsHttpError_NoCache_ReturnsNull()
    {
        var filename = "test-404.srs";
        var handler = new StaticResponseHandler(HttpStatusCode.NotFound, Array.Empty<byte>());
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/missing.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(ExpectedCachedFile(filename)));
    }

    [Fact]
    public async Task EnsureLocal_FetchSuccess_AtomicWrite_NoTmpLeftover()
    {
        var filename = "test-atomic.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmpPath = path + ".tmp";
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(tmpPath, Encoding.UTF8.GetBytes("PREV-TMP"), ct);

        var body = RuleSetSamples.UpstreamGeositeTldRu();
        var handler = new StaticResponseHandler(HttpStatusCode.OK, body);
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/atomic.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.Equal(body, await File.ReadAllBytesAsync(path, ct));
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public void EnsureLocal_PathSeparatorInFilename_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RuleSetCacheManager.EnsureLocal(
                "https://example.invalid/x.srs",
                "evil/../config.yaml",
                cacheDir: _tempCacheDir));
    }

    [Fact]
    public async Task EnsureLocal_EmptyUrl_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RuleSetCacheManager.EnsureLocalAsync(
                "",
                "test.srs",
                cacheDir: _tempCacheDir,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureLocal_HtmlResponse_RejectedBeforeCaching()
    {
        var filename = "test-captive-portal.srs";
        var path = ExpectedCachedFile(filename);

        var htmlPayload = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Blocked</body></html>");
        var handler = new StaticResponseHandler(HttpStatusCode.OK, htmlPayload);
        using var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/blocked.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(path), "Corrupted HTML payload must never be written to local cache.");
    }

    [Fact]
    public async Task EnsureLocal_AdBlockUrl_AfterFetch_FileExistsAndIsBinary()
    {
        var filename = "adblock_reject.srs";
        var validSrs = RuleSetSamples.UpstreamGeositeTldRu();
        var handler = new StaticResponseHandler(HttpStatusCode.OK, validSrs);
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var path = await RuleSetCacheManager.EnsureLocalAsync(
            "https://raw.githubusercontent.com/REIJI007/AdBlock_Rule_For_Sing-box/main/adblock_reject.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.NotNull(path);
        Assert.EndsWith(filename, path);
        Assert.True(File.Exists(path));
        Assert.Equal(validSrs, await File.ReadAllBytesAsync(path, ct));
    }

    [Theory]
    [InlineData("ftp://example.invalid/test.srs")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain;base64,AAAA")]
    [InlineData("not-a-url")]
    public async Task EnsureLocal_NonHttpUrl_ThrowsArgumentException(string invalidUrl)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RuleSetCacheManager.EnsureLocalAsync(
                invalidUrl,
                "test.srs",
                cacheDir: _tempCacheDir,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Throws<ArgumentException>(() =>
            RuleSetCacheManager.EnsureLocal(
                invalidUrl,
                "test.srs",
                cacheDir: _tempCacheDir));
    }

    [Fact]
    public async Task EnsureLocal_DamagedCachedFile_TriggersFetchAndOverwritesWithValid()
    {
        var filename = "test-corrupt-cache.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Corrupt cache: not valid SRS binary
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("DAMAGED_CORRUPT_NOT_SRS"));

        var validSrs = RuleSetSamples.UpstreamGeositeTldRu();
        var handler = new StaticResponseHandler(HttpStatusCode.OK, validSrs);
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Equal(path, result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(validSrs, await File.ReadAllBytesAsync(path, ct));
    }

    [Fact]
    public async Task EnsureLocal_DamagedCachedFile_FetchFails_ReturnsNullNotDamaged()
    {
        var filename = "test-corrupt-cache-fail.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Corrupt cache: not valid SRS binary
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("DAMAGED_CORRUPT_NOT_SRS"));

        var handler = new ThrowingHandler(new HttpRequestException("fetch failed"));
        var client = new HttpClient(handler);

        var ct = TestContext.Current.CancellationToken;
        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: ct);

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureLocal_DownloadedInvalidBinary_RejectedAndNotCached()
    {
        var filename = "test-invalid-download.srs";
        var path = ExpectedCachedFile(filename);

        var invalidBinary = Encoding.UTF8.GetBytes("SOME_BINARY_GARBAGE_NOT_SRS");
        var handler = new StaticResponseHandler(HttpStatusCode.OK, invalidBinary);
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/invalid.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EnsureLocal_CancellationDuringFetch_ReturnsNull_NoCache()
    {
        var filename = "test-cancel.srs";
        var handler = new HangingHandler();
        var client = new HttpClient(handler);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/x.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir,
            cancellationToken: cts.Token);

        Assert.Null(result);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("counter"))
            });
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        public int RequestCount;
        public StaticResponseHandler(HttpStatusCode status, byte[] body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body)
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public int RequestCount;
        public ThrowingHandler(Exception ex) { _ex = ex; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            throw _ex;
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
