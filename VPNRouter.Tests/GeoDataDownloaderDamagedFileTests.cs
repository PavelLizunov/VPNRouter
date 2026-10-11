using System.Text;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public sealed class GeoDataDownloaderDamagedFileTests : IDisposable
{
    private const string Url = "https://rules.example/geosite-tld-ru.srs";
    private const long MinSize = 100;

    private readonly string _dir;
    private readonly string _dest;

    public GeoDataDownloaderDamagedFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vpnr-geo-damaged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dest = Path.Combine(_dir, "geosite-ru.srs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<bool> EnsureAsync(FakeHttpClient http) =>
        new GeoDataDownloader(http, Serilog.Core.Logger.None)
            .EnsureFileAsync(Url, _dest, MinSize, "geosite-ru", TestContext.Current.CancellationToken);

    [Fact]
    public async Task DamagedFileOfTheRightSize_IsReplacedByAFreshDownload()
    {
        var published = RuleSetSamples.UpstreamGeositeTldRu();
        File.WriteAllBytes(_dest, RuleSetSamples.ZeroFilled(published.Length));
        var http = new FakeHttpClient().SetupStream("rules.example", published);

        var ok = await EnsureAsync(http);

        Assert.True(ok);
        Assert.Single(http.SentStreamingRequests);
        Assert.Equal(published, File.ReadAllBytes(_dest));
        Assert.False(File.Exists(_dest + ".tmp"));
    }

    [Fact]
    public async Task DamagedFile_DownloadFails_ReportsUnavailableAndLeavesNoFile()
    {
        File.WriteAllBytes(_dest, RuleSetSamples.ValidHeaderZeroedBody(155));
        var http = new FakeHttpClient().ThrowOnStream("rules.example", new HttpRequestException("offline"));

        var ok = await EnsureAsync(http);

        Assert.False(ok);
        Assert.False(File.Exists(_dest));
        Assert.False(File.Exists(_dest + ".tmp"));
    }

    [Fact]
    public async Task DamagedDownload_IsNotInstalled()
    {
        var http = new FakeHttpClient().SetupStream("rules.example", RuleSetSamples.ZeroFilled(155));

        var ok = await EnsureAsync(http);

        Assert.False(ok);
        Assert.False(File.Exists(_dest));
        Assert.False(File.Exists(_dest + ".tmp"));
    }

    [Fact]
    public async Task FreshValidFile_IsKeptWithoutADownload()
    {
        var published = RuleSetSamples.UpstreamGeositeTldRu();
        File.WriteAllBytes(_dest, published);
        var http = new FakeHttpClient().SetupStream("rules.example", RuleSetSamples.Valid(200));

        var ok = await EnsureAsync(http);

        Assert.True(ok);
        Assert.Empty(http.SentStreamingRequests);
        Assert.Equal(published, File.ReadAllBytes(_dest));
    }

    [Fact]
    public async Task StaleValidFile_DamagedDownload_KeepsTheOldCopy()
    {
        var published = RuleSetSamples.UpstreamGeositeTldRu();
        File.WriteAllBytes(_dest, published);
        File.SetLastWriteTimeUtc(_dest, DateTime.UtcNow.AddDays(-30));
        var html = Encoding.UTF8.GetBytes(new string('<', 200));
        var http = new FakeHttpClient().SetupStream("rules.example", html);

        var ok = await EnsureAsync(http);

        Assert.True(ok);
        Assert.Single(http.SentStreamingRequests);
        Assert.Equal(published, File.ReadAllBytes(_dest));
        Assert.False(File.Exists(_dest + ".tmp"));
    }

    [Fact]
    public void DamagedGeosite_MakesGeoFilesUnavailable()
    {
        var previousDataDir = AppPaths.DataDir;
        try
        {
            AppPaths.OverrideDataDir(_dir);
            Directory.CreateDirectory(AppPaths.GeoDir);
            File.WriteAllBytes(AppPaths.GeoIpRuPath, RuleSetSamples.Valid(11 * 1024));

            File.WriteAllBytes(AppPaths.GeoSiteRuPath, RuleSetSamples.UpstreamGeositeTldRu());
            Assert.True(GeoDataDownloader.AreGeoFilesAvailable());

            File.WriteAllBytes(AppPaths.GeoSiteRuPath, RuleSetSamples.ZeroFilled(155));
            Assert.False(GeoDataDownloader.AreGeoFilesAvailable());
        }
        finally
        {
            AppPaths.OverrideDataDir(previousDataDir);
        }
    }
}
