// Phase 2G (2026-05-21) — UpdateChecker test coverage.
//
// Why: UpdateChecker is 1387 LOC and was the leak site for the v2.31.7
// helper.cmd CMD parser bug that broke 100% of user upgrades for ~7 days.
// Adjacent HelperCmdParserGuardTests pins the CMD template; this file pins
// the layer one level up — SemVer parsing, version comparison (including
// the rolling-rN policy lesson from v2.25.0-r1→r2), channel awareness
// (stable skips prereleases), GitHub API response shape (happy + empty +
// 404 + malformed), and asset selection (per-platform suffix, lite-update
// rejection).
//
// Brief: plans/phase2G-updatechecker-tests-2026-05-21.md.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

/// <summary>
/// Pin <see cref="UpdateChecker"/>'s decision logic — the part that
/// decides whether a release is newer, whether the channel allows it,
/// and which asset to download. The platform-specific apply path
/// (helper.cmd / ditto / pkexec) is covered separately by
/// <see cref="HelperCmdParserGuardTests"/> (CMD template) and
/// <see cref="UpdateBackupTests"/> (snapshot helper).
///
/// <para>Internal access via <c>InternalsVisibleTo("VPNRouter.Tests")</c>
/// on <c>VPNRouter.Core.csproj</c> — gives us
/// <see cref="UpdateChecker.TryParseSemVer"/> + the
/// <see cref="UpdateChecker.SemVer"/> readonly struct without exposing
/// them to the public surface.</para>
/// </summary>
public sealed class UpdateCheckerTests
{
    // ─── SemVer parsing ──────────────────────────────────────────────────

    [Fact]
    public void TryParseSemVer_StableTag_PrefixedV_Parses()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var v));
        Assert.Equal(new Version(2, 35, 0), v.Core);
        Assert.Null(v.Rc);
    }

    [Fact]
    public void TryParseSemVer_StableTag_NoVPrefix_Parses()
    {
        // GitHubReleaseSource strips the leading 'v' before passing to
        // the parser, but the parser itself is defensive and strips it
        // again. Pin both call shapes work.
        Assert.True(UpdateChecker.TryParseSemVer("2.35.0", out var v));
        Assert.Equal(new Version(2, 35, 0), v.Core);
        Assert.Null(v.Rc);
    }

    [Fact]
    public void TryParseSemVer_RollingCandidate_PrefixedV_ParsesRcNumber()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r1", out var v));
        Assert.Equal(new Version(2, 35, 0), v.Core);
        Assert.Equal(1, v.Rc);
    }

    [Fact]
    public void TryParseSemVer_RollingCandidate_DoubleDigit_NumericNotLexicographic()
    {
        // r18 must parse as Rc=18, not as the string "18". The numeric
        // comparison below (r10 > r2) depends on this.
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r18", out var v));
        Assert.Equal(18, v.Rc);
    }

    [Fact]
    public void TryParseSemVer_UpperCaseVPrefix_Parses()
    {
        // Defensive — GitHub never emits this but the parser tolerates it.
        Assert.True(UpdateChecker.TryParseSemVer("V2.35.0", out var v));
        Assert.Equal(new Version(2, 35, 0), v.Core);
    }

    [Fact]
    public void TryParseSemVer_PlatformSuffix_Rejected()
    {
        // Legacy mac/linux build tags pre-rolling-rN scheme. The doc
        // comment on TryParseSemVer explicitly calls these out as
        // intentionally rejected so the update flow doesn't pick them
        // up by accident.
        Assert.False(UpdateChecker.TryParseSemVer("v1.0.0-mac", out _));
        Assert.False(UpdateChecker.TryParseSemVer("v2.0.0-beta.1", out _));
    }

    [Fact]
    public void TryParseSemVer_NullOrWhitespace_Rejected()
    {
        Assert.False(UpdateChecker.TryParseSemVer(null, out _));
        Assert.False(UpdateChecker.TryParseSemVer(string.Empty, out _));
        Assert.False(UpdateChecker.TryParseSemVer("   ", out _));
    }

    [Fact]
    public void TryParseSemVer_NonNumericCore_Rejected()
    {
        // Version.TryParse rejects these — pin the fall-through.
        Assert.False(UpdateChecker.TryParseSemVer("v.alpha", out _));
        Assert.False(UpdateChecker.TryParseSemVer("v2.x.0", out _));
        Assert.False(UpdateChecker.TryParseSemVer("vX.Y.Z", out _));
    }

    [Fact]
    public void TryParseSemVer_NegativeRc_Rejected()
    {
        // `-r-1` shouldn't exist in the wild but the parser uses
        // `rc < 0` as the validity gate. Pin it.
        Assert.False(UpdateChecker.TryParseSemVer("v2.35.0-r-1", out _));
    }

    // ─── Version comparison ──────────────────────────────────────────────

    [Fact]
    public void CompareTo_StableBeatsAnyRollingCandidateOfSameCore()
    {
        // The v2.25.0-r1→r2 lesson (see CLAUDE.local.md Release Process
        // section). semver-major rule: prerelease < same-core stable.
        // Pre-r2 we shipped v2.25.0-r1 with `AppVersion.Version = "2.25.0"`
        // — once we fixed AppVersion to "2.25.0-r1", clients on r1 saw
        // stable 2.25.0 as "newer" and the auto-update fired. Pin THIS
        // behaviour because it's load-bearing for the whole rolling-rN
        // release model.
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var stable));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r18", out var rc));

        Assert.True(stable.CompareTo(rc) > 0, "stable must be newer than rN of the same core");
        Assert.True(rc.CompareTo(stable) < 0, "rN must be older than stable of the same core");
    }

    [Fact]
    public void CompareTo_RollingCandidatesAreNumericNotLexicographic()
    {
        // r10 > r2 numerically. If a future refactor accidentally
        // compares Rc as string, r10 < r2 lexicographically and a user
        // on r10 sees r2 as "newer" → downgrade dialog. Pin numeric.
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r10", out var r10));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r2", out var r2));

        Assert.True(r10.CompareTo(r2) > 0, "r10 must be greater than r2 (numeric)");
        Assert.True(r2.CompareTo(r10) < 0);
    }

    [Fact]
    public void CompareTo_NewerCoreBeatsOlderCoreStable()
    {
        // 2.35.1-r1 > 2.35.0 stable. The user on 2.35.1-r1 should NOT
        // see 2.35.0 stable as "newer" and silently downgrade — that
        // was the failure mode pre-v2.25.1.
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var oldStable));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.1-r1", out var newRc));

        Assert.True(newRc.CompareTo(oldStable) > 0,
            "core-version bump beats any prerelease of older core");
        Assert.True(oldStable.CompareTo(newRc) < 0);
    }

    [Fact]
    public void CompareTo_SameTagEqualsZero()
    {
        // Self-equality. The update flow uses CompareTo > 0 as the
        // strict-newer predicate; equality means "up to date".
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var a));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var b));
        Assert.Equal(0, a.CompareTo(b));

        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r5", out var aRc));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r5", out var bRc));
        Assert.Equal(0, aRc.CompareTo(bRc));
    }

    // ─── GitHub API response shape + channel filter + asset selection ────

    [Fact]
    public async Task CheckAsync_StableChannel_SkipsPrereleaseAssets()
    {
        // Stable channel + only a prerelease newer than current →
        // null. Pre-2D-3 (when channels were a magic-string compare in
        // each call site) this was the easiest path to ship a broken
        // upgrade-prompt to stable users.
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape("v2.35.0-r1", Prerelease: true, IncludeWinAsset: true)));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_ExperimentalChannel_AcceptsPrereleaseAssets()
    {
        // Same input as above but channel=experimental → the prerelease
        // is eligible and CheckAsync returns the matching UpdateSourceInfo.
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape("v2.35.0-r1", Prerelease: true, IncludeWinAsset: true)));

        var settings = new UpdateSettings { Channel = "experimental", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        Assert.NotNull(info);
        Assert.Equal("2.35.0-r1", info!.Version);
        Assert.True(info.IsPrerelease);
    }

    [Fact]
    public async Task CheckAsync_EmptyReleaseList_ReturnsNull()
    {
        // GitHub API returns []. Must NOT throw — silent-null per
        // IUpdateSource.CheckAsync contract.
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", "[]");

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_Non200Response_ReturnsNull()
    {
        // GitHub returns 404 / 500 / rate-limit. Per the IUpdateSource
        // contract: "Implementations MUST NOT throw on transient network
        // errors — those return null instead so the caller can silently
        // retry on the next poll interval."
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", new HttpResponse(
            StatusCode: 404,
            Headers: new Dictionary<string, string>(),
            Body: Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"),
            Duration: TimeSpan.FromMilliseconds(1)));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsNull()
    {
        // GitHub returns 200 but body is junk (proxy ate it, gateway
        // returned HTML error page, etc.). The JsonException catch
        // inside GitHubReleaseSource swallows + returns null.
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", "not-actually-json {{{");

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_LiteUpdateAssetNotPickedAsFull()
    {
        // build.ps1 publishes both VPNRouter-v{version}-win.zip (full)
        // and VPNRouter-update-v{version}-win.zip (lite, DLL-only).
        // FindFullAsset must reject the one whose name contains "update".
        // If it picked the lite asset as full, the installer would
        // unpack a DLL-only ZIP into the staging dir and the validator
        // would throw "VPNRouter.GUI.exe/dll not found" → user sees a
        // useless error after waiting for a download.
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape(
                "v2.35.0",
                Prerelease: false,
                IncludeWinAsset: true,
                IncludeLiteAsset: true)));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync();

        // The platform suffix is host-dependent. On Linux CI we expect
        // the linux full asset; on Windows dev we expect the win full
        // asset. macOS would see null (the synthetic release matrix
        // only includes win + linux — see BuildReleasesJson defaults).
        //
        // Tighten the assertion (2026-05-21 review pass): explicitly
        // gate by platform so a future synthetic-matrix change that
        // accidentally drops the OS-matching asset surfaces as a
        // failure here, not a vacuous pass.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotNull(info);
            Assert.DoesNotContain("update", info!.AssetName, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // macOS / other: synthetic release has no matching asset,
            // so null is the expected outcome.
            Assert.Null(info);
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal GitHub Releases API JSON response containing the
    /// supplied release shapes. Only the fields GitHubReleaseSource
    /// actually consumes are emitted (tag_name / prerelease / draft /
    /// html_url / body / assets) so the test stays focused on shape
    /// regressions, not field-name regressions.
    /// </summary>
    private static string BuildReleasesJson(params ReleaseShape[] releases)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < releases.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var r = releases[i];
            sb.Append('{');
            sb.Append("\"tag_name\":\"").Append(r.TagName).Append("\",");
            sb.Append("\"prerelease\":").Append(r.Prerelease ? "true" : "false").Append(',');
            sb.Append("\"draft\":false,");
            sb.Append("\"html_url\":\"https://github.com/PavelLizunov/VPNRouter/releases/tag/").Append(r.TagName).Append("\",");
            sb.Append("\"body\":\"release notes for ").Append(r.TagName).Append("\",");
            sb.Append("\"assets\":[");
            var first = true;
            // Strip leading 'v' for asset filenames (matches build.ps1
            // naming: VPNRouter-v{tag-without-v}-win.zip).
            var ver = r.TagName.StartsWith("v") ? r.TagName.Substring(1) : r.TagName;
            if (r.IncludeWinAsset)
            {
                if (!first) sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-win.zip", 25_000_000);
                first = false;
            }
            if (r.IncludeLinuxAsset)
            {
                if (!first) sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-linux.tar.gz", 26_000_000);
                first = false;
            }
            if (r.IncludeLiteAsset)
            {
                if (!first) sb.Append(',');
                AppendAsset(sb, $"VPNRouter-update-v{ver}-win.zip", 3_500_000);
                first = false;
            }
            sb.Append("]}");
        }
        sb.Append(']');
        return sb.ToString();

        static void AppendAsset(StringBuilder sb, string name, long size)
        {
            sb.Append('{');
            sb.Append("\"name\":\"").Append(name).Append("\",");
            sb.Append("\"size\":").Append(size).Append(',');
            sb.Append("\"browser_download_url\":\"https://github.com/PavelLizunov/VPNRouter/releases/download/synthetic/").Append(name).Append('"');
            sb.Append('}');
        }
    }

    /// <summary>
    /// Pin a synthetic release row for the JSON builder. Default
    /// shape: full Windows + Linux assets, not a prerelease, not a
    /// draft.
    /// </summary>
    private sealed record ReleaseShape(
        string TagName,
        bool Prerelease = false,
        bool IncludeWinAsset = true,
        bool IncludeLinuxAsset = true,
        bool IncludeLiteAsset = false);

    /// <summary>
    /// No-op <see cref="IDesktopInstaller"/>. GitHubReleaseSource only
    /// delegates to the installer in DownloadAsync / ApplyAsync — never
    /// in CheckAsync — so the tests in this file (all CheckAsync) never
    /// touch it. Pinning a throw-on-call shape would surface a
    /// regression where CheckAsync accidentally starts a download.
    /// </summary>
    private sealed class NullInstaller : IDesktopInstaller
    {
        public static readonly NullInstaller Instance = new();

        public Task<string> DownloadAndStageAsync(
            UpdateSourceInfo info,
            IProgress<DownloadProgress>? progress,
            System.Threading.CancellationToken ct) =>
            throw new InvalidOperationException(
                "NullInstaller.DownloadAndStageAsync called — CheckAsync must not download.");

        public Task<bool> ApplyStagedAsync(
            UpdateSourceInfo info,
            string stagedPath,
            System.Threading.CancellationToken ct) =>
            throw new InvalidOperationException(
                "NullInstaller.ApplyStagedAsync called — CheckAsync must not apply.");
    }
}
