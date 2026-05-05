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

/// <summary>
/// v2.31.9-r3 regression suite for <see cref="RuleSetCacheManager"/>.
///
/// <para>Origin: brat-2026-05-05 user logged 4+ FATAL sing-box crashes
/// in 90 seconds because <c>raw.githubusercontent.com</c> TLS handshake
/// timeouts during AdBlock rule-set fetch crashed sing-box at startup.
/// These tests pin the cache manager's behaviour: fetch-success caches,
/// fetch-fail with stale cache returns stale, fetch-fail without cache
/// returns null (signalling caller to skip the rule-set entirely).</para>
/// </summary>
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
        // Mtime defaults to now → "fresh".

        var counting = new CountingHandler();
        var client = new HttpClient(counting);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

        Assert.Equal(path, result);
        Assert.Equal(0, counting.RequestCount); // fresh → no fetch
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task EnsureLocal_StaleCache_FetchSucceeds_OverwritesAndReturns()
    {
        var filename = "test-stale.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("OLD"));
        // Backdate mtime to 8 days ago — past 7-day MaxAgeForUseAsIs.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

        var freshBody = Encoding.UTF8.GetBytes("FRESH-BYTES");
        var handler = new StaticResponseHandler(HttpStatusCode.OK, freshBody);
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

        Assert.Equal(path, result);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(freshBody, await File.ReadAllBytesAsync(path));
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

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

        Assert.Equal(path, result);
        Assert.Equal(1, handler.RequestCount);
        // Stale file still exists, content not corrupted.
        Assert.Equal(stale, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task EnsureLocal_NoCache_FetchFails_ReturnsNull()
    {
        var filename = "test-nofallback.srs";
        // No file pre-written.

        var handler = new ThrowingHandler(new HttpRequestException("offline"));
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

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

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/test.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

        Assert.Equal(path, result);
        Assert.True(File.Exists(path));
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
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
            cacheDir: _tempCacheDir);

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
            cacheDir: _tempCacheDir);

        Assert.Null(result);
        Assert.False(File.Exists(ExpectedCachedFile(filename)));
    }

    [Fact]
    public async Task EnsureLocal_FetchSuccess_AtomicWrite_NoTmpLeftover()
    {
        var filename = "test-atomic.srs";
        var path = ExpectedCachedFile(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Pre-existing tmp from a previous failed run.
        var tmpPath = path + ".tmp";
        await File.WriteAllBytesAsync(tmpPath, Encoding.UTF8.GetBytes("PREV-TMP"));

        var body = Encoding.UTF8.GetBytes("CLEAN-FRESH");
        var handler = new StaticResponseHandler(HttpStatusCode.OK, body);
        var client = new HttpClient(handler);

        var result = await RuleSetCacheManager.EnsureLocalAsync(
            "https://example.invalid/atomic.srs",
            filename,
            httpClient: client,
            cacheDir: _tempCacheDir);

        Assert.Equal(path, result);
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        // Tmp leftover is overwritten + renamed away after success.
        Assert.False(File.Exists(tmpPath));
    }

    [Fact]
    public void EnsureLocal_PathSeparatorInFilename_Throws()
    {
        // Defensive: rule-set name comes from C# code, but ensure we
        // don't accidentally allow path traversal if anyone wires a
        // user-controlled value through.
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
                cacheDir: _tempCacheDir));
    }

    [Fact]
    public async Task EnsureLocal_CancellationDuringFetch_ReturnsNull_NoCache()
    {
        var filename = "test-cancel.srs";
        // Handler that hangs forever — only cancellation can release.
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

    // ── HttpMessageHandler test doubles ────────────────────────────────

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
            // Hangs until cancelled.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        }
    }
}
