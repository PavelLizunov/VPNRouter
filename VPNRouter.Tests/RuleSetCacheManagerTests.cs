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
        var bytes = Encoding.UTF8.GetBytes("cached-bytes-fresh");
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
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("OLD"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

        var freshBody = Encoding.UTF8.GetBytes("FRESH-BYTES");
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
        var stale = Encoding.UTF8.GetBytes("STALE-FALLBACK");
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

        var body = Encoding.UTF8.GetBytes("FIRST-FETCH");
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
    public async Task EnsureLocal_FetchReturnsEmptyBody_NoCache_ReturnsNull()
    {
        var filename = "test-empty.srs";
        var handler = new StaticResponseHandler(HttpStatusCode.OK, Array.Empty<byte>());
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/empty.srs",
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

        var body = Encoding.UTF8.GetBytes("CLEAN-FRESH");
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
        var fakeSrs = new byte[4096];
        new Random(42).NextBytes(fakeSrs);
        var handler = new StaticResponseHandler(HttpStatusCode.OK, fakeSrs);
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
        Assert.Equal(fakeSrs, await File.ReadAllBytesAsync(path, ct));
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
