using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VPNRouter.Core;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

public class UpdateCheckerChecksumTests
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

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static UpdateInfo Info(string version, string? inlineSha) => new()
    {
        LatestVersion = version,
        DownloadUrl = DownloadUrl,
        SizeBytes = 0,
        FullChecksumSha256 = inlineSha,
        HasLiteUpdate = false,
    };

    [Fact]
    public async Task DownloadAndStageAsync_InlineShaMatch_StagesSuccessfully()
    {
        var zip = MinimalUpdateZip();
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        var dir = await checker.DownloadAndStageAsync(Info("7.7.7", Sha256Hex(zip)));
        try
        {
            Assert.True(Directory.Exists(dir), $"staged dir missing: {dir}");
            Assert.NotEmpty(Directory.GetFileSystemEntries(dir));

            Assert.DoesNotContain(
                http.SentRequests,
                r => r.Uri.ToString().Contains(".sha256", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DownloadAndStageAsync_InlineShaMismatch_RefusesAndDeletesAsset()
    {
        var zip = MinimalUpdateZip();
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        var stagingBase = Path.Combine(AppPaths.DataDir, "update-staging");
        var dirsBefore = Directory.Exists(stagingBase)
            ? Directory.GetDirectories(stagingBase).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.DownloadAndStageAsync(Info("8.8.8", new string('b', 64))));
        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);

        var newDirs = Directory.Exists(stagingBase)
            ? Directory.GetDirectories(stagingBase).Where(d => !dirsBefore.Contains(d)).ToArray()
            : Array.Empty<string>();
        Assert.NotEmpty(newDirs);
        foreach (var d in newDirs)
        {
            Assert.False(File.Exists(Path.Combine(d, "VPNRouter-v8.8.8.zip")),
                $"corrupt ZIP must be deleted on mismatch, found under {d}");
            Assert.False(Directory.Exists(Path.Combine(d, "extracted")),
                $"extraction must not be reached on mismatch, found under {d}");
        }
    }

    [Fact]
    public async Task DownloadAndStageAsync_InlineShaMalformedLength_Throws()
    {
        var zip = MinimalUpdateZip();
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.DownloadAndStageAsync(Info("5.5.5", "abc")));
    }

    [Fact]
    public async Task DownloadAndStageAsync_MissingDigest_RefusesExtract()
    {
        var zip = MinimalUpdateZip();
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.DownloadAndStageAsync(Info("9.9.9", null)));
        Assert.Contains("checksum is missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
