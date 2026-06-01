using System.IO.Compression;
using System.Net;
using System.Text;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Tests for the audit-#4 fix: the pool fetcher now prefers the compressed
/// pool.json.gz (~3.9 MB) over raw pool.json (~27 MB), with bounded
/// decompression (bomb defense), validate-before-replace, and a raw fallback.
/// Suite is sequential (xunit.runner.json) so OverrideDataDir is safe.
/// </summary>
public sealed class FreeConfigPoolFetcherTests
{
    private const string SamplePool = @"{
      ""version"": 1,
      ""servers"": [
        { ""id"": ""a"", ""host"": ""1.2.3.4"", ""port"": 443, ""raw"": ""vless://x@1.2.3.4:443"", ""country"": ""US"" },
        { ""id"": ""b"", ""host"": ""5.6.7.8"", ""port"": 443, ""raw"": ""vless://y@5.6.7.8:443"", ""country"": ""DE"" }
      ]
    }";

    private static readonly Serilog.ILogger SilentLog = new LoggerConfiguration().CreateLogger();

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] GzipText(string s) => Gzip(Encoding.UTF8.GetBytes(s));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    // --- decompression bounds (the new, risky code) ---

    [Fact]
    public async Task DecompressBounded_RoundTripsGzip()
    {
        var original = Encoding.UTF8.GetBytes(SamplePool);
        using var src = new MemoryStream(Gzip(original));
        using var dst = new MemoryStream();
        await FreeConfigPoolFetcher.DecompressBoundedAsync(src, gzip: true, dst, 10_000_000, default);
        Assert.Equal(original, dst.ToArray());
    }

    [Fact]
    public async Task DecompressBounded_NonGzip_IsPassthrough()
    {
        var original = Encoding.UTF8.GetBytes(SamplePool);
        using var src = new MemoryStream(original);
        using var dst = new MemoryStream();
        await FreeConfigPoolFetcher.DecompressBoundedAsync(src, gzip: false, dst, 10_000_000, default);
        Assert.Equal(original, dst.ToArray());
    }

    [Fact]
    public async Task DecompressBounded_RejectsBomb()
    {
        // 4 MB of zeros compresses to a few KB; cap the expansion at 64 KB.
        var bomb = Gzip(new byte[4 * 1024 * 1024]);
        using var src = new MemoryStream(bomb);
        using var dst = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FreeConfigPoolFetcher.DecompressBoundedAsync(src, gzip: true, dst, 64 * 1024, default));
    }

    // --- parse ---

    [Fact]
    public void ParsePool_Stream_ParsesServers()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(SamplePool));
        var entries = FreeConfigPoolFetcher.ParsePool(s);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Host == "1.2.3.4");
        Assert.Contains(entries, e => e.CountryCode == "DE");
    }

    // --- end-to-end fetch via fake handler ---

    [Fact]
    public async Task FetchPool_PrefersGzip()
    {
        var previous = AppPaths.DataDir;
        var dir = Path.Combine(Path.GetTempPath(), "pool-fetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.OverrideDataDir(dir);
            var handler = new FakeHandler
            {
                Responder = req =>
                {
                    if (req.RequestUri!.AbsoluteUri.EndsWith("pool.json.gz"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(GzipText(SamplePool)) };
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            };
            var fetcher = new FreeConfigPoolFetcher(SilentLog, handler);
            var entries = await fetcher.FetchPoolAsync();
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
            Assert.True(File.Exists(Path.Combine(AppPaths.CacheDir, "pool.json")), "decompressed cache should be written");
        }
        finally { AppPaths.OverrideDataDir(previous); TryDelete(dir); }
    }

    [Fact]
    public async Task FetchPool_FallsBackToRaw_WhenGzipMissing()
    {
        var previous = AppPaths.DataDir;
        var dir = Path.Combine(Path.GetTempPath(), "pool-fetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.OverrideDataDir(dir);
            var handler = new FakeHandler
            {
                Responder = req =>
                {
                    if (req.RequestUri!.AbsoluteUri.EndsWith("pool.json.gz"))
                        return new HttpResponseMessage(HttpStatusCode.NotFound);          // no gz on this release
                    if (req.RequestUri.AbsoluteUri.EndsWith("pool.json"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SamplePool) };
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            };
            var fetcher = new FreeConfigPoolFetcher(SilentLog, handler);
            var entries = await fetcher.FetchPoolAsync();
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
        }
        finally { AppPaths.OverrideDataDir(previous); TryDelete(dir); }
    }

    [Fact]
    public async Task FetchPool_TruncatedGzip_KeepsPreviousCache()
    {
        var previous = AppPaths.DataDir;
        var dir = Path.Combine(Path.GetTempPath(), "pool-fetch-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.OverrideDataDir(dir);
            AppPaths.EnsureDirectories();
            // seed a good last-known-good cache
            File.WriteAllText(Path.Combine(AppPaths.CacheDir, "pool.json"), SamplePool);

            var truncated = GzipText(SamplePool);
            Array.Resize(ref truncated, truncated.Length / 2);      // corrupt the gz
            var handler = new FakeHandler
            {
                Responder = req => req.RequestUri!.AbsoluteUri.EndsWith("pool.json.gz")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(truncated) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            var fetcher = new FreeConfigPoolFetcher(SilentLog, handler);
            var entries = await fetcher.FetchPoolAsync();   // gz corrupt, raw 404 -> local cache
            Assert.NotNull(entries);
            Assert.Equal(2, entries!.Count);
            // the good cache must survive the corrupt download
            Assert.Contains("1.2.3.4", File.ReadAllText(Path.Combine(AppPaths.CacheDir, "pool.json")));
        }
        finally { AppPaths.OverrideDataDir(previous); TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
