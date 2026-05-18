// Phase 3 — 3F (v3.0 refactor): contract tests for IUpdateSource.
//
// Pins the expected behaviour of all three concrete implementations:
//   • GitHubReleaseSource (desktop)
//   • SideloadSource (Android sideload)
//   • PlayStoreSource (Android Play Store stub)
//
// Tests run on any OS — they use FakeHttpClient + in-process fake
// installers. The Android-specific Intent.ActionView dispatch is covered
// by the installer adapter (not in scope here); the IUpdateSource layer
// only owns the GitHub probe + SHA256 verification gate.
//
// SECURITY-CRITICAL: SideloadSource.DownloadAsync MUST verify SHA256
// against AssetSha256 BEFORE returning the path. Once Intent.ActionView
// dispatches to PackageInstaller, the OS trusts the bytes on disk. The
// SHA check is the LAST gate against a tampered or truncated transfer.
// SideloadSource_DownloadAsync_ShaMismatch_ThrowsAndDeletesFile pins
// this ordering.
//
// Brief: plans/phase3-3F-android-updatesource-2026-05-18.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Contract tests for <see cref="IUpdateSource"/>. Covers all three
/// concrete impls: <see cref="GitHubReleaseSource"/> (desktop),
/// <see cref="SideloadSource"/> (Android sideload),
/// <see cref="PlayStoreSource"/> (Android Play Store stub).
///
/// <para>
/// SECURITY CONTRACT (pinned by
/// <see cref="SideloadSource_DownloadAsync_ShaMismatch_ThrowsAndDeletesFile"/>):
/// every concrete impl MUST verify SHA256 against
/// <see cref="UpdateSourceInfo.AssetSha256"/> BEFORE returning the
/// staged path from <see cref="IUpdateSource.DownloadAsync"/>. Once
/// <see cref="IUpdateSource.ApplyAsync"/> dispatches to the platform
/// installer (Intent.ActionView on Android, helper.cmd on Windows,
/// pkexec on Linux, ditto on macOS) the platform trusts the bytes on
/// disk wholesale — there is NO abort path. SHA verification is the
/// last gate against tampered or truncated transfers.
/// </para>
/// </summary>
public sealed class IUpdateSourceContractTests
{
    private const string ReleasesApi =
        "https://api.github.com/repos/PavelLizunov/VPNRouter/releases";
    private const string CurrentVersion = "2.32.0";
    private const string TestRepo = "PavelLizunov/VPNRouter";

    // ─── GitHubReleaseSource ────────────────────────────────────────────

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_HappyPath_ReturnsInfo()
    {
        // Arrange — canned releases JSON with one stable release strictly
        // newer than the running version + a SHA256 companion file.
        const string newerVersion = "2.32.1";
        var assetName = AssetNameForCurrentPlatform("2.32.1");
        var assetUrl = $"https://github.com/foo/bar/releases/download/v{newerVersion}/{assetName}";
        var shaName = $"{assetName}.sha256";
        var shaUrl = $"{assetUrl}.sha256";
        var canonicalSha = new string('a', 64); // 64 lowercase hex chars

        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                Tag: $"v{newerVersion}",
                Prerelease: false,
                Body: "**v2.32.1** — bug fixes",
                Assets: new[] {
                    new AssetStub(assetName, assetUrl, 12_345_678),
                    new AssetStub(shaName, shaUrl, 64),
                }),
        });

        var fake = new FakeHttpClient()
            .Setup(ReleasesApi, releasesJson)
            .Setup(shaUrl, canonicalSha);

        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "stable" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        // Act
        var info = await source.CheckAsync();

        // Assert
        Assert.NotNull(info);
        Assert.Equal(newerVersion, info!.Version);
        Assert.Equal(assetUrl, info.DownloadUrl);
        Assert.Equal(12_345_678, info.AssetSize);
        Assert.Equal(canonicalSha, info.AssetSha256);
        Assert.False(info.IsPrerelease);
        Assert.Contains("v2.32.1", info.ReleaseNotes);
        Assert.Equal("github", source.SourceId);
    }

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_NoNewerVersion_ReturnsNull()
    {
        // Arrange — only a SAME-version release on the feed. Source must
        // return null because the version-ladder predicate is STRICTLY
        // greater than (not >=) — otherwise we'd loop on the same release.
        var assetName = AssetNameForCurrentPlatform(CurrentVersion);
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                Tag: $"v{CurrentVersion}",
                Prerelease: false,
                Body: "Current release",
                Assets: new[] {
                    new AssetStub(assetName, $"https://example.com/{assetName}", 1_000_000),
                }),
        });

        var fake = new FakeHttpClient().Setup(ReleasesApi, releasesJson);
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "stable" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        // Act
        var info = await source.CheckAsync();

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_StableChannel_SkipsPrerelease()
    {
        // Arrange — newer release exists but is flagged prerelease;
        // stable channel must skip it. Demonstrates the IsExperimental
        // gate.
        var assetName = AssetNameForCurrentPlatform("2.33.0-r1");
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                Tag: "v2.33.0-r1",
                Prerelease: true,
                Body: "Candidate",
                Assets: new[] {
                    new AssetStub(assetName, $"https://example.com/{assetName}", 1_000),
                }),
        });

        var fake = new FakeHttpClient().Setup(ReleasesApi, releasesJson);
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "stable" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        // Act
        var info = await source.CheckAsync();

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task GitHubReleaseSource_DownloadAsync_StreamsProgressFromInstaller()
    {
        // Arrange — fake installer reports byte-percent progress via
        // its supplied IProgress sink. Source must surface that 1:1 to
        // the caller.
        var info = SampleSourceInfo(sha: null);
        var fakeInstaller = new FakeDesktopInstaller
        {
            ProgressEmits = new[]
            {
                new DownloadProgress(0, 1000),
                new DownloadProgress(500, 1000),
                new DownloadProgress(1000, 1000),
            },
            ReturnPath = @"C:\fake\staging\extracted",
        };
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            new FakeHttpClient(),
            fakeInstaller);

        var captured = new List<DownloadProgress>();
        // Use a synchronous IProgress impl rather than Progress<T> —
        // the latter dispatches via SynchronizationContext when one is
        // available, which xUnit may or may not provide, making the
        // test flaky. CapturingProgress reports synchronously into the
        // list so order + final value are deterministic.
        var sink = new CapturingProgress(captured);

        // Act
        var path = await source.DownloadAsync(info, sink);

        // Assert — installer was invoked, path bubbled up, progress
        // events surfaced 1:1 from installer to caller.
        Assert.Equal(@"C:\fake\staging\extracted", path);
        Assert.Equal(3, captured.Count);
        Assert.Equal(1000, captured[^1].BytesReceived);
        Assert.Equal(1000, captured[^1].TotalBytes);
        Assert.Equal(100, captured[^1].Percent);
    }

    // ─── SideloadSource ─────────────────────────────────────────────────

    [Fact]
    public async Task SideloadSource_CheckAsync_PicksApkAsset_NotZip()
    {
        // Arrange — release with BOTH an APK and a -win.zip. Sideload
        // source must pick the APK; never the desktop zip.
        var apkName = "VPNRouter-v2.32.1-android.apk";
        var apkUrl = $"https://example.com/{apkName}";
        var zipName = "VPNRouter-v2.32.1-win.zip";
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                Tag: "v2.32.1",
                Prerelease: false,
                Body: string.Empty,
                Assets: new[] {
                    new AssetStub(zipName, $"https://example.com/{zipName}", 12_000),
                    new AssetStub(apkName, apkUrl, 41_000_000),
                }),
        });

        var fake = new FakeHttpClient().Setup(ReleasesApi, releasesJson);
        var source = new SideloadSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "stable" },
            CurrentVersion,
            fake,
            new FakeAndroidInstaller());

        // Act
        var info = await source.CheckAsync();

        // Assert — APK chosen, not the desktop zip.
        Assert.NotNull(info);
        Assert.Equal(apkName, info!.AssetName);
        Assert.Equal(apkUrl, info.DownloadUrl);
        Assert.Equal(41_000_000, info.AssetSize);
        Assert.Equal("sideload", source.SourceId);
    }

    [Fact]
    public async Task SideloadSource_DownloadAsync_ShaMatch_ReturnsPath()
    {
        // Arrange — installer hands back an APK whose bytes hash to a
        // known SHA. Source must verify and return the path.
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-{Guid.NewGuid():N}.apk");
        var bytes = Encoding.UTF8.GetBytes("fake APK contents for SHA test");
        await File.WriteAllBytesAsync(tempPath, bytes);
        var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        try
        {
            var info = SampleSourceInfo(sha: expectedSha);
            var fakeInstaller = new FakeAndroidInstaller { DownloadReturnPath = tempPath };
            var source = new SideloadSource(
                new UpdateSettings { GitHubRepo = TestRepo },
                CurrentVersion,
                new FakeHttpClient(),
                fakeInstaller);

            // Act
            var path = await source.DownloadAsync(info);

            // Assert — path bubbles up; file still on disk for the next step.
            Assert.Equal(tempPath, path);
            Assert.True(File.Exists(tempPath));
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public async Task SideloadSource_DownloadAsync_ShaMismatch_ThrowsAndDeletesFile()
    {
        // ── SECURITY GATE — load-bearing invariant ──
        // This test pins the contract: SHA mismatch MUST throw + delete
        // the corrupt file BEFORE the caller can pass the path to
        // ApplyAsync (which fires Intent.ActionView). Once the system
        // PackageInstaller takes the file, we can't claw it back.
        //
        // If a future refactor moves the SHA check after BeginInstallAsync
        // (or skips it entirely when the .sha256 is published), this
        // test will fail. Don't loosen it without a security re-review.

        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-{Guid.NewGuid():N}.apk");
        var bytes = Encoding.UTF8.GetBytes("fake APK contents for mismatch test");
        await File.WriteAllBytesAsync(tempPath, bytes);
        // Use a wrong but well-formed SHA so the validation reaches the
        // compare step rather than failing on shape.
        var wrongSha = new string('b', 64);

        try
        {
            var info = SampleSourceInfo(sha: wrongSha);
            var fakeInstaller = new FakeAndroidInstaller { DownloadReturnPath = tempPath };
            var source = new SideloadSource(
                new UpdateSettings { GitHubRepo = TestRepo },
                CurrentVersion,
                new FakeHttpClient(),
                fakeInstaller);

            // Act + Assert — must throw + file removed so retry pulls fresh.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => source.DownloadAsync(info));
            Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tempPath),
                "Corrupted APK must be deleted on SHA mismatch so the next attempt downloads fresh bytes.");

            // BeginInstallAsync must NOT have been called — proves the
            // SHA check sits BEFORE Intent dispatch.
            Assert.Equal(0, fakeInstaller.BeginInstallCallCount);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public async Task SideloadSource_ApplyAsync_InvokesAndroidInstaller()
    {
        // Arrange — source forwards to IAndroidInstaller.BeginInstallAsync.
        var info = SampleSourceInfo(sha: null);
        var fakeInstaller = new FakeAndroidInstaller { BeginInstallReturnValue = true };
        var source = new SideloadSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            new FakeHttpClient(),
            fakeInstaller);

        // Act
        var result = await source.ApplyAsync(info, @"/data/data/com.app/cache/update.apk");

        // Assert
        Assert.True(result);
        Assert.Equal(1, fakeInstaller.BeginInstallCallCount);
        Assert.Equal(@"/data/data/com.app/cache/update.apk", fakeInstaller.LastApkPath);
    }

    // ─── PlayStoreSource ────────────────────────────────────────────────

    [Fact]
    public async Task PlayStoreSource_CheckAsync_ReturnsNull()
    {
        // Phase 3F stub — Play Store handles its own updates, so our
        // in-app check returns null (no banner). Phase 4 will replace
        // with Play In-App Update API.
        var source = new PlayStoreSource();
        var info = await source.CheckAsync();
        Assert.Null(info);
        Assert.Equal("play-store", source.SourceId);
    }

    [Fact]
    public async Task PlayStoreSource_DownloadAndApply_Throw()
    {
        // Stub raises NotSupportedException loudly so a caller bug
        // (forgetting the SourceId == "play-store" branch) shows up in
        // QA rather than as a silent no-op.
        var source = new PlayStoreSource();
        var info = SampleSourceInfo(sha: null);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => source.DownloadAsync(info));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => source.ApplyAsync(info, "/data/data/x/files/x.apk"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    /// <summary>Resolve the asset name for the OS the test is running
    /// on, matching <see cref="GitHubReleaseSource"/>'s PlatformSuffix
    /// table.</summary>
    private static string AssetNameForCurrentPlatform(string version)
    {
        if (OperatingSystem.IsMacOS())
            return $"VPNRouter-v{version}-mac.zip";
        if (OperatingSystem.IsLinux())
            return $"VPNRouter-v{version}-linux.tar.gz";
        return $"VPNRouter-v{version}-win.zip";
    }

    private static UpdateSourceInfo SampleSourceInfo(string? sha) => new(
        Version: "2.32.1",
        ReleaseUrl: "https://github.com/foo/bar/releases/tag/v2.32.1",
        AssetName: "VPNRouter-v2.32.1-android.apk",
        DownloadUrl: "https://example.com/VPNRouter-v2.32.1-android.apk",
        AssetSize: 41_000_000,
        AssetSha256: sha,
        IsPrerelease: false,
        ReleaseNotes: "Bug fixes");

    private static string BuildReleasesJson(IEnumerable<ReleaseStub> releases)
    {
        // Build the anonymous-shape JSON that
        // GitHubReleaseSource.CheckAsync expects via
        // JsonConvert.DeserializeAnonymousType. Keep field order
        // stable; the parser is field-name-keyed so order doesn't
        // matter, but consistent layout helps debug failures.
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var r in releases)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{")
              .Append($"\"tag_name\":{JsonStr(r.Tag)},")
              .Append($"\"body\":{JsonStr(r.Body)},")
              .Append($"\"html_url\":{JsonStr($"https://github.com/foo/bar/releases/tag/{r.Tag}")},")
              .Append($"\"draft\":false,")
              .Append($"\"prerelease\":{(r.Prerelease ? "true" : "false")},")
              .Append("\"assets\":[");
            var firstA = true;
            foreach (var a in r.Assets)
            {
                if (!firstA) sb.Append(',');
                firstA = false;
                sb.Append("{")
                  .Append($"\"browser_download_url\":{JsonStr(a.Url)},")
                  .Append($"\"size\":{a.Size},")
                  .Append($"\"name\":{JsonStr(a.Name)}")
                  .Append("}");
            }
            sb.Append("]}");
        }
        sb.Append(']');
        return sb.ToString();

        static string JsonStr(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private sealed record ReleaseStub(string Tag, bool Prerelease, string Body, AssetStub[] Assets);
    private sealed record AssetStub(string Name, string Url, long Size);

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> impl — reports into the
    /// list inline rather than queuing onto a SynchronizationContext
    /// (the latter is what <see cref="Progress{T}"/> does, which makes
    /// xUnit assertions on captured callbacks flaky).
    /// </summary>
    private sealed class CapturingProgress : IProgress<DownloadProgress>
    {
        private readonly List<DownloadProgress> _list;
        public CapturingProgress(List<DownloadProgress> list) => _list = list;
        public void Report(DownloadProgress value) => _list.Add(value);
    }

    /// <summary>Test double for <see cref="IDesktopInstaller"/>.</summary>
    private sealed class FakeDesktopInstaller : IDesktopInstaller
    {
        public IReadOnlyList<DownloadProgress> ProgressEmits { get; init; } = Array.Empty<DownloadProgress>();
        public string ReturnPath { get; init; } = string.Empty;
        public int DownloadCallCount { get; private set; }
        public int ApplyCallCount { get; private set; }

        public Task<string> DownloadAndStageAsync(
            UpdateSourceInfo info,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            DownloadCallCount++;
            if (progress != null)
                foreach (var p in ProgressEmits)
                    progress.Report(p);
            return Task.FromResult(ReturnPath);
        }

        public Task<bool> ApplyStagedAsync(
            UpdateSourceInfo info,
            string stagedPath,
            CancellationToken ct)
        {
            ApplyCallCount++;
            return Task.FromResult(true);
        }
    }

    /// <summary>Test double for <see cref="IAndroidInstaller"/>.</summary>
    private sealed class FakeAndroidInstaller : IAndroidInstaller
    {
        public string DownloadReturnPath { get; set; } = string.Empty;
        public bool BeginInstallReturnValue { get; set; }
        public int BeginInstallCallCount { get; private set; }
        public string? LastApkPath { get; private set; }

        public Task<string> DownloadApkAsync(
            UpdateSourceInfo info,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct)
        {
            return Task.FromResult(DownloadReturnPath);
        }

        public Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct)
        {
            BeginInstallCallCount++;
            LastApkPath = apkPath;
            return Task.FromResult(BeginInstallReturnValue);
        }
    }
}
