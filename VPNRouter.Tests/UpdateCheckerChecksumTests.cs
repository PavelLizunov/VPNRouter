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

/// <summary>
/// P01 UPD-1: the desktop update gate MUST hash-verify the downloaded asset
/// against the inline SHA256 threaded from <c>UpdateSourceInfo.AssetSha256</c>
/// (via <c>UpdateInfo.FullChecksumSha256</c>) BEFORE extraction — the
/// <c>IUpdateSource.DownloadAsync</c> MUST-validate contract. The null-digest
/// size-only fallback is covered by <see cref="UpdateCheckerStagingTests"/>.
/// </summary>
public class UpdateCheckerChecksumTests
{
    private const string DownloadUrl = "https://example.test/VPNRouter-update.zip";

    private static byte[] MinimalUpdateZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Marker files so ValidateExtractedContent passes on any platform.
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
        SizeBytes = 0,             // 0 → skip the "too small" guard
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

            // The inline digest is authoritative — the gate must NOT re-fetch
            // the .sha256 sidecar over HTTP.
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

        // Well-formed (64 hex) but wrong digest → reaches the compare step.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.DownloadAndStageAsync(Info("8.8.8", new string('b', 64))));
        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The gate fires BEFORE extraction: the per-attempt dir holds neither
        // the downloaded ZIP (deleted on mismatch) nor an "extracted" dir.
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

        // A non-64-char digest is rejected before any compare/extract.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.DownloadAndStageAsync(Info("5.5.5", "abc")));
    }
}
