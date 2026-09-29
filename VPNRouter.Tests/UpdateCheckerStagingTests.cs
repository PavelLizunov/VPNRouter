using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class UpdateCheckerStagingTests
{
    private const string DownloadUrl = "https://example.test/VPNRouter-update.zip";

    private static byte[] MinimalUpdateZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("app/VPNRouter.GUI.dll");
            zip.CreateEntry("VPNRouter.App.dll");
            zip.CreateEntry("VPNRouter.Mac.dll");
        }
        return ms.ToArray();
    }

    private static UpdateInfo Info(string sha) => new()
    {
        LatestVersion = "9.9.9",
        DownloadUrl = DownloadUrl,
        SizeBytes = 0,
        FullChecksumSha256 = sha,
        HasLiteUpdate = false,
    };

    [Fact]
    public async Task DownloadAndStageAsync_UsesUniquePerAttemptSubdir_NoSharedClobber()
    {
        var zip = MinimalUpdateZip();
        var sha = Convert.ToHexStringLower(SHA256.HashData(zip));
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        var dir1 = await checker.DownloadAndStageAsync(Info(sha), TestContext.Current.CancellationToken);
        var dir2 = await checker.DownloadAndStageAsync(Info(sha), TestContext.Current.CancellationToken);

        try
        {
            Assert.NotEqual(dir1, dir2);

            Assert.True(Directory.Exists(dir1), $"dir1 missing: {dir1}");
            Assert.True(Directory.Exists(dir2), $"dir2 missing: {dir2}");
            Assert.NotEmpty(Directory.GetFileSystemEntries(dir1));
            Assert.NotEmpty(Directory.GetFileSystemEntries(dir2));

            Assert.Equal("extracted", Path.GetFileName(dir1.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.Equal("extracted", Path.GetFileName(dir2.TrimEnd(Path.DirectorySeparatorChar)));
        }
        finally
        {
            foreach (var d in new[] { dir1, dir2 })
            {
                try { Directory.Delete(Path.GetDirectoryName(d)!, recursive: true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/update.zip")]
    [InlineData("javascript:alert(1)")]
    [InlineData("gopher://example.com")]
    [InlineData("not-a-url")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("/updates/update.zip")]
    [InlineData("../updates/update.zip")]
    public async Task DownloadAndStageAsync_RejectsInvalidOrNonHttpDownloadUrls(string invalidUrl)
    {
        var http = new FakeHttpClient();
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);
        var info = new UpdateInfo
        {
            LatestVersion = "9.9.9",
            DownloadUrl = invalidUrl,
            SizeBytes = 0,
            FullChecksumSha256 = new string('a', 64),
            HasLiteUpdate = false,
        };

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => checker.DownloadAndStageAsync(info, TestContext.Current.CancellationToken));

        Assert.Contains("Invalid or non-http(s) update download URL", ex.Message);
        Assert.Empty(http.SentStreamingRequests);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/update.zip.sha256")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    public async Task DownloadAndStageAsync_RejectsInvalidOrNonHttpChecksumUrls(string invalidChecksumUrl)
    {
        var http = new FakeHttpClient();
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);
        var info = new UpdateInfo
        {
            LatestVersion = "9.9.9",
            DownloadUrl = DownloadUrl,
            SizeBytes = 0,
            FullChecksumUrl = invalidChecksumUrl,
            FullChecksumSha256 = null,
            HasLiteUpdate = false,
        };

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => checker.DownloadAndStageAsync(info, TestContext.Current.CancellationToken));

        Assert.Contains("Invalid or non-http(s) update checksum URL", ex.Message);
        Assert.Empty(http.SentRequests);
    }
}
