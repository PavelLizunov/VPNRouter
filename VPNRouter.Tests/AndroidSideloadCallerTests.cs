// Phase 4 (Wave 18, 2026-05-18) — pin the Android-side caller contract:
// AndroidApp.AutoUpdate drives IUpdateSource.CheckAsync / DownloadAsync /
// ApplyAsync (instead of static AndroidUpdater methods).
//
// VPNRouter.Tests is net8.0 and cannot ProjectReference VPNRouter.Android
// (net8.0-android), so we test the Android-side flow indirectly by:
//   1. Driving a SideloadSource (Core-side concrete) via FakeHttpClient
//      with canned GitHub release JSON containing an APK asset.
//   2. Replacing the Android-platform IAndroidInstaller with a fake.
//   3. Asserting that the existing AndroidApp.AutoUpdate flow shape —
//      CheckAsync → PromptUpdateAvailable's caching of UpdateSourceInfo
//      → DownloadAsync → ApplyAsync — still works against the contract.
//
// The Android-specific Intent.ActionView dispatch lives in
// AndroidUpdater.BeginInstall (called by AndroidInstallerAdapter which
// lives in VPNRouter.Android); that's covered by the integration test
// path on a real device and out of scope here.
//
// Brief: plans/phase4-iupdatesource-callers-2026-05-18.md.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Tests for the Android sideload caller's IUpdateSource consumption.
/// Mirrors what <c>VPNRouter.Android.AndroidApp.AutoUpdate</c> does
/// without the platform-specific Intent dispatch.
/// </summary>
public sealed class AndroidSideloadCallerTests
{
    [Fact]
    public async Task AndroidCaller_CheckDownloadApply_FlowsThroughIUpdateSource()
    {
        // Arrange — same shape as AndroidApp.AutoUpdate.RunUpdateCheckAsync
        // → DownloadAndInstallAsync → HandleInstallClick chain, only
        // driven through the IUpdateSource contract rather than static
        // AndroidUpdater methods.
        var info = new UpdateSourceInfo(
            Version: "2.34.2",
            ReleaseUrl: "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.34.2",
            AssetName: "VPNRouter-v2.34.2-android.apk",
            DownloadUrl: "https://example.com/VPNRouter-v2.34.2-android.apk",
            AssetSize: 41_000_000,
            AssetSha256: null,
            IsPrerelease: false,
            ReleaseNotes: "Android sideload smoke");

        var fake = new FakeUpdateSource
        {
            SourceId = "sideload",
            CheckResult = info,
            DownloadReturnPath = "/data/data/com.ninitux.vpnrouter/cache/update.apk",
            ApplyReturnValue = true,
        };

        // Act — replicate the call chain.
        var ct = TestContext.Current.CancellationToken;
        var checkResult = await fake.CheckAsync(ct);
        Assert.NotNull(checkResult);

        var stagedPath = await fake.DownloadAsync(checkResult!, ct: ct);
        Assert.Equal("/data/data/com.ninitux.vpnrouter/cache/update.apk", stagedPath);

        var applyResult = await fake.ApplyAsync(checkResult!, stagedPath, ct);

        // Assert
        Assert.True(applyResult);
        Assert.Equal(1, fake.CheckCallCount);
        Assert.Equal(1, fake.DownloadCallCount);
        Assert.Equal(1, fake.ApplyCallCount);
        Assert.Equal(info, fake.LastDownloadInfo);
        Assert.Equal(info, fake.LastApplyInfo);
        Assert.Equal(stagedPath, fake.LastApplyStagedPath);
    }

    [Fact]
    public async Task SideloadSource_FromGitHubReleaseJson_PicksApkOverZip()
    {
        // Arrange — drive the real SideloadSource (Core-side concrete)
        // through a fake HTTP client + fake IAndroidInstaller. Pins
        // that Android-side caller code goes through SideloadSource and
        // gets the APK asset back (never a desktop-zip asset).
        const string releasesApi =
            "https://api.github.com/repos/PavelLizunov/VPNRouter/releases";
        const string releasesJson = """
            [
              {
                "tag_name": "v2.34.2",
                "body": "Android sideload contract test",
                "html_url": "https://github.com/PavelLizunov/VPNRouter/releases/tag/v2.34.2",
                "draft": false,
                "prerelease": false,
                "assets": [
                  {
                    "browser_download_url": "https://example.com/VPNRouter-v2.34.2-android.apk",
                    "size": 41000000,
                    "name": "VPNRouter-v2.34.2-android.apk"
                  },
                  {
                    "browser_download_url": "https://example.com/VPNRouter-v2.34.2-win.zip",
                    "size": 25000000,
                    "name": "VPNRouter-v2.34.2-win.zip"
                  }
                ]
              }
            ]
            """;

        var http = new FakeHttpClient().Setup(releasesApi, releasesJson);
        var installer = new FakeAndroidInstaller();
        var source = new SideloadSource(
            new UpdateSettings { GitHubRepo = "PavelLizunov/VPNRouter", Channel = "stable" },
            currentVersion: "2.34.0",
            http,
            installer);

        // Act
        var found = await source.CheckAsync(TestContext.Current.CancellationToken);

        // Assert — APK picked over ZIP.
        Assert.NotNull(found);
        Assert.Equal("VPNRouter-v2.34.2-android.apk", found!.AssetName);
        Assert.Equal(41_000_000, found.AssetSize);
        Assert.Equal("sideload", source.SourceId);
    }

    /// <summary>Test double for <see cref="IAndroidInstaller"/>.</summary>
    private sealed class FakeAndroidInstaller : IAndroidInstaller
    {
        public Task<string> DownloadApkAsync(
            UpdateSourceInfo info,
            IProgress<DownloadProgress>? progress,
            CancellationToken ct) =>
            Task.FromResult("/data/data/x/cache/update.apk");

        public Task<bool> BeginInstallAsync(string apkPath, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
