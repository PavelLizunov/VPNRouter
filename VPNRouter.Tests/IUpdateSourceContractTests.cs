#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

public sealed class IUpdateSourceContractTests
{
    private const string ReleasesApi =
        "https://api.github.com/repos/PavelLizunov/VPNRouter/releases";
    private const string CurrentVersion = "2.32.0";
    private const string TestRepo = "PavelLizunov/VPNRouter";

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_HappyPath_ReturnsInfo()
    {
        const string newerVersion = "2.32.1";
        var assetName = AssetNameForCurrentPlatform("2.32.1");
        var assetUrl = $"https://github.com/foo/bar/releases/download/v{newerVersion}/{assetName}";
        var shaName = $"{assetName}.sha256";
        var shaUrl = $"{assetUrl}.sha256";
        var canonicalSha = new string('a', 64);

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

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

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

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_MissingSidecar_ReturnsNull()
    {
        const string newerVersion = "2.32.1";
        var assetName = AssetNameForCurrentPlatform(newerVersion);
        var assetUrl = $"https://github.com/foo/bar/releases/download/v{newerVersion}/{assetName}";
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                Tag: $"v{newerVersion}",
                Prerelease: false,
                Body: "No sidecar",
                Assets: new[] {
                    new AssetStub(assetName, assetUrl, 12_345_678),
                }),
        });

        var fake = new FakeHttpClient().Setup(ReleasesApi, releasesJson);
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "stable" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Null(info);
    }

    [Fact]
    public async Task GitHubReleaseSource_CheckAsync_StableChannel_SkipsPrerelease()
    {
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

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task GitHubReleaseSource_DownloadAsync_StreamsProgressFromInstaller()
    {
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
        var sink = new CapturingProgress(captured);

        var path = await source.DownloadAsync(info, sink, TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\fake\staging\extracted", path);
        Assert.Equal(3, captured.Count);
        Assert.Equal(1000, captured[^1].BytesReceived);
        Assert.Equal(1000, captured[^1].TotalBytes);
        Assert.Equal(100, captured[^1].Percent);
    }

    [Fact]
    public async Task GitHubReleaseSource_DownloadAsync_ShaMismatch_ThrowsBeforeApply()
    {
        var zip = MinimalUpdateZip();
        const string assetUrl = "https://example.com/VPNRouter-v2.32.1-win.zip";
        var http = new FakeHttpClient().SetupStream(assetUrl, zip);
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            http,
            new UpdateChecker(new UpdateSettings(), CurrentVersion, http));

        var info = new UpdateSourceInfo(
            Version: "2.32.1",
            ReleaseUrl: "https://github.com/foo/bar/releases/tag/v2.32.1",
            AssetName: "VPNRouter-v2.32.1-win.zip",
            DownloadUrl: assetUrl,
            AssetSize: zip.LongLength,
            AssetSha256: new string('b', 64),
            IsPrerelease: false,
            ReleaseNotes: string.Empty);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.DownloadAsync(info, ct: TestContext.Current.CancellationToken));
        Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitHubReleaseSource_ListStableAsync_ReturnsRecentOlderVerifiedOnly()
    {
        var eligibleVersions = new[] { "2.31.9", "2.31.8", "2.31.7" };
        var releases = new List<ReleaseStub>();
        var fake = new FakeHttpClient();
        foreach (var version in eligibleVersions)
        {
            var assetName = AssetNameForCurrentPlatform(version);
            var assetUrl = $"https://example.com/{assetName}";
            releases.Add(new ReleaseStub(
                $"v{version}", false, $"Release {version}",
                new[]
                {
                    new AssetStub(assetName, assetUrl, 10_000),
                    new AssetStub($"{assetName}.sha256", $"{assetUrl}.sha256", 64),
                }));
            fake.Setup($"{assetUrl}.sha256", new string(version[^1], 64));
        }

        var missingShaName = AssetNameForCurrentPlatform("2.31.95");
        releases.Add(new ReleaseStub(
            "v2.31.95", false, "Missing checksum",
            new[] { new AssetStub(missingShaName, $"https://example.com/{missingShaName}", 10_000) }));
        var mislabeledCandidateName = AssetNameForCurrentPlatform("2.31.99-r1");
        var mislabeledCandidateUrl = $"https://example.com/{mislabeledCandidateName}";
        releases.Add(new ReleaseStub(
            "v2.31.99-r1", false, "Candidate mislabeled as stable",
            new[]
            {
                new AssetStub(mislabeledCandidateName, mislabeledCandidateUrl, 10_000),
                new AssetStub($"{mislabeledCandidateName}.sha256", $"{mislabeledCandidateUrl}.sha256", 64),
            }));
        fake.Setup($"{mislabeledCandidateUrl}.sha256", new string('f', 64));
        releases.Add(new ReleaseStub(
            "v2.31.98", false, "Draft",
            Array.Empty<AssetStub>(), Draft: true));

        fake.Setup(ReleasesApi, BuildReleasesJson(releases));
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "experimental" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var result = await source.ListStableAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2.31.9", "2.31.8" }, result.Select(x => x.Version));
        Assert.All(result, x => Assert.Matches("^[0-9a-f]{64}$", x.AssetSha256));
        Assert.All(result, x => Assert.False(x.IsPrerelease));
    }

    [Fact]
    public async Task GitHubReleaseSource_ListOlderAsync_WithCandidates_ListsOlderCandidatesAndStableNewestFirst()
    {
        // running version is 2.32.0: older = 2.31.x stable and 2.31.x-rN candidates, plus the candidate of the running core version below the final
        var spec = new (string Tag, bool PreFlag, bool Draft, bool WithSha)[]
        {
            ("v2.31.9", false, false, true),
            ("v2.31.9-r4", true, false, true),
            ("v2.31.9-r2", true, false, true),
            ("v2.31.8", false, false, true),
            ("v2.31.7-r9", false, false, true),   // candidate whose release is not flagged as a prerelease: still a candidate
            ("v2.31.6", false, true, true),       // draft
            ("v2.31.5", false, false, false),     // no checksum
            ("v2.32.1", false, false, true),      // newer than the running build
            ("v2.32.0", false, false, true),      // the running build itself
        };
        var releases = new List<ReleaseStub>();
        var fake = new FakeHttpClient();
        foreach (var (tag, preFlag, draft, withSha) in spec)
        {
            var version = tag.TrimStart('v');
            var assetName = AssetNameForCurrentPlatform(version);
            var assetUrl = $"https://example.com/{assetName}";
            var assets = withSha
                ? new[] { new AssetStub(assetName, assetUrl, 10_000), new AssetStub($"{assetName}.sha256", $"{assetUrl}.sha256", 64) }
                : new[] { new AssetStub(assetName, assetUrl, 10_000) };
            releases.Add(new ReleaseStub(tag, preFlag, $"Release {tag}", assets, Draft: draft));
            if (withSha) fake.Setup($"{assetUrl}.sha256", new string('c', 64));
        }
        fake.Setup(ReleasesApi, BuildReleasesJson(releases));
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "experimental" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var all = await source.ListOlderAsync(20, includePrereleases: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2.31.9", "2.31.9-r4", "2.31.9-r2", "2.31.8", "2.31.7-r9" }, all.Select(x => x.Version));
        Assert.Equal(new[] { false, true, true, false, true }, all.Select(x => x.IsPrerelease));

        var capped = await source.ListOlderAsync(2, includePrereleases: true, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "2.31.9", "2.31.9-r4" }, capped.Select(x => x.Version));

        var stableOnly = await source.ListOlderAsync(20, includePrereleases: false, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "2.31.9", "2.31.8" }, stableOnly.Select(x => x.Version));
        Assert.All(stableOnly, x => Assert.False(x.IsPrerelease));
    }

    [Fact]
    public async Task GitHubReleaseSource_ListOlderAsync_ManyCandidates_StillOffersTheNewestStableReleases()
    {
        // ten candidates newer than every stable release: a plain "newest eight" would never reach a stable version
        var tags = Enumerable.Range(1, 10).Select(i => $"2.31.9-r{i}")
            .Concat(new[] { "2.31.0", "2.30.0", "2.29.0", "2.28.0" }).ToList();
        var releases = new List<ReleaseStub>();
        var fake = new FakeHttpClient();
        foreach (var version in tags)
        {
            var assetName = AssetNameForCurrentPlatform(version);
            var assetUrl = $"https://example.com/{assetName}";
            releases.Add(new ReleaseStub($"v{version}", version.Contains("-r"), $"Release {version}", new[]
            {
                new AssetStub(assetName, assetUrl, 10_000),
                new AssetStub($"{assetName}.sha256", $"{assetUrl}.sha256", 64),
            }));
            fake.Setup($"{assetUrl}.sha256", new string('e', 64));
        }
        fake.Setup(ReleasesApi, BuildReleasesJson(releases));
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "experimental" },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var list = await source.ListOlderAsync(8, includePrereleases: true, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "2.31.9-r10", "2.31.9-r9", "2.31.9-r8", "2.31.9-r7", "2.31.9-r6", "2.31.0", "2.30.0", "2.29.0" },
            list.Select(x => x.Version));
        Assert.Equal(new[] { true, true, true, true, true, false, false, false }, list.Select(x => x.IsPrerelease));

        // a small cap keeps the old meaning: the newest items of any kind
        var small = await source.ListOlderAsync(3, includePrereleases: true, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "2.31.9-r10", "2.31.9-r9", "2.31.9-r8" }, small.Select(x => x.Version));
    }

    [Fact]
    public async Task GitHubReleaseSource_ListOlderAsync_RunningCandidate_ListsEarlierCandidates()
    {
        var versions = new[] { "2.32.0-r1", "2.32.0-r2", "2.32.0-r3", "2.32.0-r4" };
        var releases = new List<ReleaseStub>();
        var fake = new FakeHttpClient();
        foreach (var version in versions)
        {
            var assetName = AssetNameForCurrentPlatform(version);
            var assetUrl = $"https://example.com/{assetName}";
            releases.Add(new ReleaseStub($"v{version}", true, $"Candidate {version}", new[]
            {
                new AssetStub(assetName, assetUrl, 10_000),
                new AssetStub($"{assetName}.sha256", $"{assetUrl}.sha256", 64),
            }));
            fake.Setup($"{assetUrl}.sha256", new string('d', 64));
        }
        fake.Setup(ReleasesApi, BuildReleasesJson(releases));
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo, Channel = "experimental" },
            "2.32.0-r3",
            fake,
            new FakeDesktopInstaller());

        var older = await source.ListOlderAsync(8, includePrereleases: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "2.32.0-r2", "2.32.0-r1" }, older.Select(x => x.Version));
    }

    [Fact]
    public async Task GitHubReleaseSource_ListStableAsync_InvalidChecksum_HidesRelease()
    {
        const string version = "2.31.9";
        var assetName = AssetNameForCurrentPlatform(version);
        var assetUrl = $"https://example.com/{assetName}";
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                $"v{version}", false, "Bad checksum",
                new[]
                {
                    new AssetStub(assetName, assetUrl, 10_000),
                    new AssetStub($"{assetName}.sha256", $"{assetUrl}.sha256", 64),
                }),
        });
        var fake = new FakeHttpClient()
            .Setup(ReleasesApi, releasesJson)
            .Setup($"{assetUrl}.sha256", "not-a-sha");
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var result = await source.ListStableAsync(3, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GitHubReleaseSource_ListStableAsync_WrongVersionAsset_HidesRelease()
    {
        const string releaseVersion = "2.31.9";
        var wrongAssetName = AssetNameForCurrentPlatform("2.31.8");
        var wrongAssetUrl = $"https://example.com/{wrongAssetName}";
        var releasesJson = BuildReleasesJson(new[]
        {
            new ReleaseStub(
                $"v{releaseVersion}", false, "Mismatched asset",
                new[]
                {
                    new AssetStub(wrongAssetName, wrongAssetUrl, 10_000),
                    new AssetStub($"{wrongAssetName}.sha256", $"{wrongAssetUrl}.sha256", 64),
                }),
        });
        var fake = new FakeHttpClient()
            .Setup(ReleasesApi, releasesJson)
            .Setup($"{wrongAssetUrl}.sha256", new string('a', 64));
        var source = new GitHubReleaseSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            fake,
            new FakeDesktopInstaller());

        var result = await source.ListStableAsync(3, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SideloadSource_CheckAsync_PicksApkAsset_NotZip()
    {
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

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal(apkName, info!.AssetName);
        Assert.Equal(apkUrl, info.DownloadUrl);
        Assert.Equal(41_000_000, info.AssetSize);
        Assert.Equal("sideload", source.SourceId);
    }

    [Fact]
    public async Task SideloadSource_DownloadAsync_ShaMatch_ReturnsPath()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-{Guid.NewGuid():N}.apk");
        var bytes = Encoding.UTF8.GetBytes("fake APK contents for SHA test");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        try
        {
            var info = SampleSourceInfo(sha: expectedSha);
            var fakeInstaller = new FakeAndroidInstaller { DownloadReturnPath = tempPath };
            var source = new SideloadSource(
                new UpdateSettings { GitHubRepo = TestRepo },
                CurrentVersion,
                new FakeHttpClient(),
                fakeInstaller);

            var path = await source.DownloadAsync(info, ct: ct);

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
        var tempPath = Path.Combine(Path.GetTempPath(), $"vpnrouter-test-{Guid.NewGuid():N}.apk");
        var bytes = Encoding.UTF8.GetBytes("fake APK contents for mismatch test");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(tempPath, bytes, ct);
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

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => source.DownloadAsync(info, ct: ct));
            Assert.Contains("checksum mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tempPath),
                "Corrupted APK must be deleted on SHA mismatch so the next attempt downloads fresh bytes.");

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
        var info = SampleSourceInfo(sha: null);
        var fakeInstaller = new FakeAndroidInstaller { BeginInstallReturnValue = true };
        var source = new SideloadSource(
            new UpdateSettings { GitHubRepo = TestRepo },
            CurrentVersion,
            new FakeHttpClient(),
            fakeInstaller);

        var result = await source.ApplyAsync(info, @"/data/data/com.app/cache/update.apk", TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(1, fakeInstaller.BeginInstallCallCount);
        Assert.Equal(@"/data/data/com.app/cache/update.apk", fakeInstaller.LastApkPath);
    }

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
              .Append($"\"draft\":{(r.Draft ? "true" : "false")},")
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

    private sealed record ReleaseStub(
        string Tag,
        bool Prerelease,
        string Body,
        AssetStub[] Assets,
        bool Draft = false);
    private sealed record AssetStub(string Name, string Url, long Size);

    private sealed class CapturingProgress : IProgress<DownloadProgress>
    {
        private readonly List<DownloadProgress> _list;
        public CapturingProgress(List<DownloadProgress> list) => _list = list;
        public void Report(DownloadProgress value) => _list.Add(value);
    }

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
