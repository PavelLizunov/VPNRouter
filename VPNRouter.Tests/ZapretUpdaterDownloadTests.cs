#nullable enable

using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Serilog;
using VPNRouter.Core;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Characterization tests for <see cref="ZapretUpdater.DownloadAndExtractAsync"/>: how a Flowseal GitHub release
/// becomes an installed zapret folder, and which <see cref="ZapretErrorCategory"/> each failure gets.
/// The install step of the real method stops winws and deletes the WinDivert driver services, so the tests that
/// reach it run only where that step is a no-op (not Windows); every failure path is checked everywhere.
/// </summary>
public sealed class ZapretUpdaterDownloadTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private const string ZipUrl = "https://downloads.example.test/flowseal/release.zip";

    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private readonly string _originalDataDir;
    private readonly string _tempDataDir;

    public ZapretUpdaterDownloadTests()
    {
        _originalDataDir = AppPaths.DataDir;
        _tempDataDir = Path.Combine(Path.GetTempPath(), $"vpnrouter-zapret-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDataDir);
        AppPaths.OverrideDataDir(_tempDataDir);
    }

    public void Dispose()
    {
        AppPaths.OverrideDataDir(_originalDataDir);
        try { Directory.Delete(_tempDataDir, recursive: true); }
        catch { }
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string ReleaseJson(string tag, string? zipUrl, long? size = null, string? zipball = null)
    {
        var assets = new List<object> { new { name = "release-notes.txt", browser_download_url = "https://x.test/notes.txt", size = 10 } };
        if (zipUrl != null)
            assets.Add(size.HasValue
                ? new { name = "zapret-discord-youtube.zip", browser_download_url = zipUrl, size = (object)size.Value }
                : new { name = "zapret-discord-youtube.zip", browser_download_url = zipUrl, size = (object)"n/a" });
        return zipball == null
            ? JsonSerializer.Serialize(new { tag_name = tag, assets })
            : JsonSerializer.Serialize(new { tag_name = tag, assets, zipball_url = zipball });
    }

    private static (ZapretUpdater Updater, FakeHttpClient Http, List<string> Status) Create(FakeHttpClient http)
    {
        var updater = new ZapretUpdater(SilentLogger, http);
        var status = new List<string>();
        updater.StatusChanged += status.Add;
        return (updater, http, status);
    }

    // ---- install (skipped on Windows: the real install step touches winws and the WinDivert services) ----

    [Fact]
    public async Task Download_ReleaseWithSingleTopFolder_UnwrapsItInstallsFilesAndTakesVersionFromServiceBat()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "the install step stops winws and deletes WinDivert services on Windows");
        var zip = Zip(
            ("zapret-discord-youtube-1.9.7/bin/winws.exe", "MZ"),
            ("zapret-discord-youtube-1.9.7/service.bat", "@echo off\r\nset \"LOCAL_VERSION=1.9.7\"\r\n"),
            ("zapret-discord-youtube-1.9.7/general.bat", "start \"\" \"%BIN%winws.exe\" --wf-tcp=443"),
            ("zapret-discord-youtube-1.9.7/lists/list-general.txt", "example.com"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip.Length))
            .SetupStream("release.zip", zip);
        var (updater, _, status) = Create(http);

        await updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);

        Assert.True(ZapretUpdater.IsInstalled());
        Assert.Equal("example.com", File.ReadAllText(Path.Combine(ZapretUpdater.ListsDir, "list-general.txt")));
        Assert.True(File.Exists(Path.Combine(ZapretUpdater.ZapretDir, "general.bat")));
        Assert.Equal("1.9.7", File.ReadAllText(ZapretUpdater.VersionFilePath));
        Assert.Equal("1.9.7", ZapretUpdater.GetLocalVersion());
        Assert.Equal(
            new[] { "Fetching release info...", "Downloading v1.9.7...", "Extracting...", "Installing...", "Installed 1.9.7" },
            status);

        var api = Assert.Single(http.SentRequests);
        Assert.Equal(ApiUrl, api.Uri.ToString());
        Assert.Equal("application/vnd.github.v3+json", api.Headers!["Accept"]);
        var download = Assert.Single(http.SentStreamingRequests);
        Assert.Equal(ZipUrl, download.Uri.ToString());
        Assert.Equal(TimeSpan.FromMinutes(5), download.Timeout);
    }

    [Fact]
    public async Task Download_FlatArchiveWithoutServiceBat_UsesTheReleaseTagAsVersion()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "the install step stops winws and deletes WinDivert services on Windows");
        var zip = Zip(("bin/winws.exe", "MZ"), ("general.bat", "rem"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v2.0.0", ZipUrl, zip.Length))
            .SetupStream("release.zip", zip);
        var (updater, _, status) = Create(http);

        await updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);

        Assert.True(ZapretUpdater.IsInstalled());
        Assert.Equal("v2.0.0", File.ReadAllText(ZapretUpdater.VersionFilePath));
        Assert.Equal("Installed v2.0.0", status[^1]);
    }

    // ---- failures before the install step (run everywhere) ----------------------------------------

    [Fact]
    public async Task Download_ReleaseWithoutZipAssetOrZipball_IsInvalidAndDownloadsNothing()
    {
        var http = new FakeHttpClient().Setup("releases/latest", ReleaseJson("v1.9.7", zipUrl: null));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Contains("No ZIP asset", ex.Message);
        Assert.Empty(http.SentStreamingRequests);
    }

    [Fact]
    public async Task Download_NoZipAssetButAZipball_FallsBackToTheZipballUrl()
    {
        const string zipball = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/zipball/v1.9.7";
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", zipUrl: null, zipball: zipball))
            .SetupStream("zipball/v1.9.7", Zip(("docs/readme.md", "no winws in here")));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Equal(zipball, Assert.Single(http.SentStreamingRequests).Uri.ToString());
    }

    [Fact]
    public async Task Download_ArchiveWithoutWinws_IsInvalid_ListsWhatItFoundAndInstallsNothing()
    {
        var zip = Zip(("docs/readme.md", "text"), ("lists/list-general.txt", "example.com"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip.Length))
            .SetupStream("release.zip", zip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Contains("bin/winws.exe", ex.Message);
        Assert.Contains("docs", ex.Message);
        Assert.Contains("lists", ex.Message);
        Assert.False(ZapretUpdater.IsInstalled());
        Assert.False(Directory.Exists(ZapretUpdater.ZapretDir));
    }

    [Fact]
    public async Task Download_TopFolderPlusALooseRootFile_IsNotUnwrapped_SoWinwsIsNotFound()
    {
        var zip = Zip(("release-1.9.7/bin/winws.exe", "MZ"), ("README.txt", "loose file at the archive root"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip.Length))
            .SetupStream("release.zip", zip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.False(ZapretUpdater.IsInstalled());
    }

    [Fact]
    public async Task Download_BytesThatAreNotAZip_AreCorrupted()
    {
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl))
            .SetupStream("release.zip", System.Text.Encoding.UTF8.GetBytes("this is not a zip archive"));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Corrupted, ex.Category);
    }

    [Fact]
    public async Task Download_GitHubRateLimit_IsRetriedTwiceThenReportedAsRateLimit()
    {
        var http = new FakeHttpClient().Setup("releases/latest", "{}", statusCode: 403);
        var (updater, _, status) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.GitHubRateLimit, ex.Category);
        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(3, http.SentRequests.Count);
        Assert.Equal(2, status.Count(s => s.StartsWith("Retry ", StringComparison.Ordinal)));
    }
}
