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
/// The test host redirects the runtime to an isolated temporary directory.
/// </summary>
public sealed class ZapretUpdaterDownloadTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    private const string ZipUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/test/release.zip";

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
                using var stream = archive.CreateEntry(path).Open();
                if (content == "fixture AMD64 header")
                {
                    byte[] header = new byte[128];
                    header[0] = (byte)'M';
                    header[1] = (byte)'Z';
                    header[0x3c] = 0x40;
                    header[0x40] = (byte)'P';
                    header[0x41] = (byte)'E';
                    header[0x44] = 0x64;
                    header[0x45] = 0x86;
                    header[0x58] = 0x0b;
                    header[0x59] = 0x02;
                    stream.Write(header);
                }
                else
                {
                    using var writer = new StreamWriter(stream);
                    writer.Write(content);
                }
            }
        }
        return ms.ToArray();
    }

    private static byte[] Package(string prefix = "", string? service = null, string? strategy = null) =>
        Zip((prefix + "bin/winws.exe", "fixture AMD64 header"), (prefix + "bin/WinDivert.dll", "fixture AMD64 header"),
            (prefix + "bin/cygwin1.dll", "fixture AMD64 header"), (prefix + "bin/WinDivert64.sys", "fixture AMD64 header"),
            (prefix + "service.bat", service ?? "@echo off"), (prefix + "utils/test zapret.ps1", "# fixture"),
            (prefix + "utils/check_updates.enabled", "enabled"), (prefix + "lists/list-general.txt", "example.com"),
            (prefix + "general.bat", strategy ?? "start \"\" \"%BIN%winws.exe\" --wf-tcp=443 --dpi-desync=fake"));

    private static string ReleaseJson(string tag, string? zipUrl, byte[]? archive = null, string? zipball = null)
    {
        var assets = new List<object> { new { name = "release-notes.txt", browser_download_url = "https://x.test/notes.txt", size = 10 } };
        if (zipUrl != null)
            assets.Add(new { name = "zapret-discord-youtube.zip", browser_download_url = zipUrl, size = archive?.Length,
                digest = archive is null ? null : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive)) });
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

    // ---- installation ----

    [Fact]
    public async Task Download_ReleaseWithSingleTopFolder_UnwrapsItInstallsFilesAndTakesVersionFromServiceBat()
    {
        var zip = Package("zapret-discord-youtube-1.9.7/", service: "@echo off\r\nset \"LOCAL_VERSION=1.9.7\"\r\n");
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
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
    public async Task Download_FlatPackageWithoutEmbeddedVersion_UsesTheReleaseTag()
    {
        var zip = Package();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v2.0.0", ZipUrl, zip))
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
    public async Task Download_UnverifiedZipballIsRejected()
    {
        const string zipball = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/zipball/v1.9.7";
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", zipUrl: null, zipball: zipball))
            .SetupStream("zipball/v1.9.7", Zip(("docs/readme.md", "no winws in here")));
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.Empty(http.SentStreamingRequests);
    }

    [Fact]
    public async Task Download_ArchiveWithoutWinws_IsInvalid_ListsWhatItFoundAndInstallsNothing()
    {
        var zip = Zip(("docs/readme.md", "text"), ("lists/list-general.txt", "example.com"));
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
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
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, zip))
            .SetupStream("release.zip", zip);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ZapretErrorCategory.Invalid, ex.Category);
        Assert.False(ZapretUpdater.IsInstalled());
    }

    [Fact]
    public async Task Download_IncompleteRuntimeCannotReplaceWorkingPackage()
    {
        var working = Package();
        var broken = Package(strategy: "rem no launch command");
        var (first, _, _) = Create(new FakeHttpClient().Setup("releases/latest", ReleaseJson("v1", ZipUrl, working)).SetupStream("release.zip", working));
        await first.DownloadAndExtractAsync(TestContext.Current.CancellationToken);
        var (next, _, _) = Create(new FakeHttpClient().Setup("releases/latest", ReleaseJson("v2", ZipUrl, broken)).SetupStream("release.zip", broken));
        await Assert.ThrowsAsync<ZapretDownloadException>(() => next.DownloadAndExtractAsync(TestContext.Current.CancellationToken));
        Assert.Equal("v1", ZapretUpdater.GetLocalVersion());
    }

    [Fact]
    public async Task Download_UpgradeKeepsDisabledUpdateChecks()
    {
        await InstallFixture("v1");
        var flag = Path.Combine(ZapretUpdater.ZapretDir, "utils", "check_updates.enabled");
        Assert.True(File.Exists(flag));
        File.Delete(flag);
        await InstallFixture("v2");
        Assert.False(File.Exists(flag));
    }

    [Theory]
    [InlineData(ZapretActions.IpSetMode.None)]
    [InlineData(ZapretActions.IpSetMode.Any)]
    public async Task Download_UpgradePreservesTheListForReturningToLoaded(ZapretActions.IpSetMode mode)
    {
        await InstallFixture("v1");
        const string loaded = "192.0.2.0/24\r\n198.51.100.0/24\r\n";
        var list = Path.Combine(ZapretUpdater.ListsDir, "ipset-all.txt");
        File.WriteAllText(list, loaded);
        ZapretActions.SetIpSetMode(mode);

        await InstallFixture("v2");

        Assert.Equal("v2", ZapretUpdater.GetLocalVersion());
        Assert.Equal(mode, ZapretActions.GetIpSetMode());
        Assert.Equal(mode == ZapretActions.IpSetMode.None ? "203.0.113.113/32" : "", File.ReadAllText(list));
        Assert.Equal(loaded, File.ReadAllText(list + ".backup"));
        ZapretActions.SetIpSetMode(ZapretActions.IpSetMode.Loaded);
        Assert.Equal(ZapretActions.IpSetMode.Loaded, ZapretActions.GetIpSetMode());
        Assert.Equal(loaded, File.ReadAllText(list));
        Assert.False(File.Exists(list + ".backup"));
    }

    private static Task InstallFixture(string tag)
    {
        var zip = Package();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson(tag, ZipUrl, zip))
            .SetupStream("release.zip", zip);
        return Create(http).Updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Download_DigestMismatchDoesNotCreateAnInstallation()
    {
        var zip = Zip(("bin/winws.exe", "fixture"));
        var different = (byte[])zip.Clone();
        different[^1] ^= 1;
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1", ZipUrl, zip))
            .SetupStream("release.zip", different);
        var (updater, _, _) = Create(http);

        var ex = await Assert.ThrowsAsync<ZapretDownloadException>(
            () => updater.DownloadAndExtractAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ZapretErrorCategory.Corrupted, ex.Category);
        Assert.False(ZapretUpdater.IsInstalled());
    }

    [Fact]
    public async Task Download_BytesThatAreNotAZip_AreCorrupted()
    {
        var invalidZip = "invalid archive"u8.ToArray();
        var http = new FakeHttpClient()
            .Setup("releases/latest", ReleaseJson("v1.9.7", ZipUrl, invalidZip))
            .SetupStream("release.zip", invalidZip);
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
