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
}
