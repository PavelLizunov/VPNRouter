using System.IO.Compression;
using System.Net;
using System.Text;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services.FreeConfigs;
using Xunit;

namespace VPNRouter.Tests;

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
        var bomb = Gzip(new byte[4 * 1024 * 1024]);
        using var src = new MemoryStream(bomb);
        using var dst = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FreeConfigPoolFetcher.DecompressBoundedAsync(src, gzip: true, dst, 64 * 1024, default));
    }

    [Fact]
    public void ParsePool_Stream_ParsesServers()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(SamplePool));
        var entries = FreeConfigPoolFetcher.ParsePool(s);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Host == "1.2.3.4");
        Assert.Contains(entries, e => e.CountryCode == "DE");
    }

    [Fact]
    public void ParsePool_MultiProtocol_ParsesProtocolAndPath()
    {
        const string json = @"{
          ""version"": 2,
          ""servers"": [
            { ""id"": ""a"", ""protocol"": ""hysteria2"", ""host"": ""1.2.3.4"", ""port"": 443, ""raw"": ""hysteria2://pass@1.2.3.4:443?sni=example.com"", ""country"": ""US"", ""path"": ""/chat"" }
          ]
        }";
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var entries = FreeConfigPoolFetcher.ParsePool(s);
        var entry = Assert.Single(entries);
        Assert.Equal("hysteria2", entry.Protocol);
        Assert.Equal("/chat", entry.Path);
        Assert.Equal("US", entry.CountryCode);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
