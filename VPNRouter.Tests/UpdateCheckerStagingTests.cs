using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// v2.44.1-r5 race fix: <see cref="UpdateChecker.DownloadAndStageAsync"/> must
/// stage each attempt into a UNIQUE per-attempt subdir so a concurrent / looping
/// update can't clobber a previous attempt's staging mid-copy. The shared-dir
/// delete+recreate was the user-reported "kill-loop" root cause (2026-06-22):
/// a second attempt deleted the dir while a previous attempt's detached
/// helper.cmd was still xcopying from it -> empty SRC -> failed copy that had
/// ALREADY killed sing-box -> .update-failed -> relaunch -> retry.
///
/// The standard gates can't catch this: the CI Auto-Update Integration Test
/// uses <c>--staged-dir</c> (bypasses DownloadAndStageAsync) and the cut-stable
/// live-update gate exercises the PREVIOUS version's stager, never the
/// candidate's. So this unit test is the validation for the staging change.
/// (The helper.cmd itself is unchanged — verified by HelperCmdParserGuardTests.)
/// </summary>
public class UpdateCheckerStagingTests
{
    private const string DownloadUrl = "https://example.test/VPNRouter-update.zip";

    private static byte[] MinimalUpdateZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Marker files so ValidateExtractedContent passes on any platform
            // (Windows checks app/VPNRouter.GUI.*, Linux root VPNRouter.App.*,
            // macOS root VPNRouter.Mac.dll).
            zip.CreateEntry("app/VPNRouter.GUI.dll");
            zip.CreateEntry("VPNRouter.App.dll");
            zip.CreateEntry("VPNRouter.Mac.dll");
        }
        return ms.ToArray();
    }

    private static UpdateInfo Info() => new()
    {
        LatestVersion = "9.9.9",
        DownloadUrl = DownloadUrl,
        SizeBytes = 0,           // skip the "too small" guard
        FullChecksumUrl = null,  // skip checksum verification
        HasLiteUpdate = false,
    };

    [Fact]
    public async Task DownloadAndStageAsync_UsesUniquePerAttemptSubdir_NoSharedClobber()
    {
        var zip = MinimalUpdateZip();
        var http = new FakeHttpClient().SetupStream(DownloadUrl, zip);
        var checker = new UpdateChecker(new UpdateSettings(), "2.44.1-r4", http);

        var dir1 = await checker.DownloadAndStageAsync(Info());
        var dir2 = await checker.DownloadAndStageAsync(Info());

        try
        {
            // Two attempts land in DIFFERENT directories — the race fix. A
            // shared-dir scheme would return the same path both times and the
            // 2nd call's delete+recreate would have wiped the 1st's files.
            Assert.NotEqual(dir1, dir2);

            // Both still exist and are populated (extracted payload present).
            Assert.True(Directory.Exists(dir1), $"dir1 missing: {dir1}");
            Assert.True(Directory.Exists(dir2), $"dir2 missing: {dir2}");
            Assert.NotEmpty(Directory.GetFileSystemEntries(dir1));
            Assert.NotEmpty(Directory.GetFileSystemEntries(dir2));

            // The returned path is the conventional "extracted" leaf — the same
            // shape ApplyUpdate(extractedDir) consumes as %SRC%, so the
            // per-attempt path flows through to the (unchanged) helper intact.
            Assert.Equal("extracted", Path.GetFileName(dir1.TrimEnd(Path.DirectorySeparatorChar)));
            Assert.Equal("extracted", Path.GetFileName(dir2.TrimEnd(Path.DirectorySeparatorChar)));
        }
        finally
        {
            // Remove only the per-attempt subdirs this test created (the parent
            // of each "extracted" leaf); never touch sibling staging dirs.
            foreach (var d in new[] { dir1, dir2 })
            {
                try { Directory.Delete(Path.GetDirectoryName(d)!, recursive: true); } catch { }
            }
        }
    }
}
