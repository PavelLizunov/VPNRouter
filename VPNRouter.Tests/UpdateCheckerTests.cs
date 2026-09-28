#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;

namespace VPNRouter.Tests;

public sealed class UpdateCheckerTests
{
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
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r18", out var v));
        Assert.Equal(18, v.Rc);
    }

    [Fact]
    public void TryParseSemVer_UpperCaseVPrefix_Parses()
    {
        Assert.True(UpdateChecker.TryParseSemVer("V2.35.0", out var v));
        Assert.Equal(new Version(2, 35, 0), v.Core);
    }

    [Fact]
    public void TryParseSemVer_PlatformSuffix_Rejected()
    {
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
        Assert.False(UpdateChecker.TryParseSemVer("v.alpha", out _));
        Assert.False(UpdateChecker.TryParseSemVer("v2.x.0", out _));
        Assert.False(UpdateChecker.TryParseSemVer("vX.Y.Z", out _));
    }

    [Fact]
    public void TryParseSemVer_NegativeRc_Rejected()
    {
        Assert.False(UpdateChecker.TryParseSemVer("v2.35.0-r-1", out _));
    }

    [Fact]
    public void EscapeShellArgument_SingleQuotesEscapedCorrectly()
    {
        Assert.Equal("plainPath", UpdateChecker.EscapeShellArgument("plainPath"));
        Assert.Equal("path'\\''sWithQuote", UpdateChecker.EscapeShellArgument("path'sWithQuote"));
        Assert.Equal("path'\\''with'\\''multiple'\\''quotes", UpdateChecker.EscapeShellArgument("path'with'multiple'quotes"));
    }

    [Fact]
    public void EscapeShellArgument_RoundTripsAdversarialValueThroughPosixShell()
    {
        if (OperatingSystem.IsWindows()) return;

        const string value = "path with spaces \"double\" $HOME $(printf injected) `printf injected`\nnext's";
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"printf %s '{UpdateChecker.EscapeShellArgument(value)}'");

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(value, output);
    }

    [Fact]
    public void CompareTo_StableBeatsAnyRollingCandidateOfSameCore()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var stable));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r18", out var rc));

        Assert.True(stable.CompareTo(rc) > 0, "stable must be newer than rN of the same core");
        Assert.True(rc.CompareTo(stable) < 0, "rN must be older than stable of the same core");
    }

    [Fact]
    public void CompareTo_RollingCandidatesAreNumericNotLexicographic()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r10", out var r10));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r2", out var r2));

        Assert.True(r10.CompareTo(r2) > 0, "r10 must be greater than r2 (numeric)");
        Assert.True(r2.CompareTo(r10) < 0);
    }

    [Fact]
    public void CompareTo_NewerCoreBeatsOlderCoreStable()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var oldStable));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.1-r1", out var newRc));

        Assert.True(newRc.CompareTo(oldStable) > 0,
            "core-version bump beats any prerelease of older core");
        Assert.True(oldStable.CompareTo(newRc) < 0);
    }

    [Fact]
    public void CompareTo_SameTagEqualsZero()
    {
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var a));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0", out var b));
        Assert.Equal(0, a.CompareTo(b));

        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r5", out var aRc));
        Assert.True(UpdateChecker.TryParseSemVer("v2.35.0-r5", out var bRc));
        Assert.Equal(0, aRc.CompareTo(bRc));
    }

    [Fact]
    public async Task CheckAsync_StableChannel_SkipsPrereleaseAssets()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape("v2.35.0-r1", Prerelease: true, IncludeWinAsset: true)));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_ExperimentalChannel_AcceptsPrereleaseAssets()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape("v2.35.0-r1", Prerelease: true, IncludeWinAsset: true)));
        http.Setup("releases/download/synthetic/", new string('a', 64));

        var settings = new UpdateSettings { Channel = "experimental", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("2.35.0-r1", info!.Version);
        Assert.True(info.IsPrerelease);
    }

    [Fact]
    public async Task CheckAsync_EmptyReleaseList_ReturnsNull()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", "[]");

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_Non200Response_ReturnsNull()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", new HttpResponse(
            StatusCode: 404,
            Headers: new Dictionary<string, string>(),
            Body: Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"),
            Duration: TimeSpan.FromMilliseconds(1)));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsNull()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", "not-actually-json {{{");

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(info);
    }

    [Fact]
    public async Task CheckAsync_LiteUpdateAssetNotPickedAsFull()
    {
        var http = new FakeHttpClient();
        http.Setup("api.github.com/repos/", BuildReleasesJson(
            new ReleaseShape(
                "v2.35.0",
                Prerelease: false,
                IncludeWinAsset: true,
                IncludeLiteAsset: true)));
        http.Setup("releases/download/synthetic/", new string('a', 64));

        var settings = new UpdateSettings { Channel = "stable", GitHubRepo = "PavelLizunov/VPNRouter" };
        var source = new GitHubReleaseSource(settings, "2.34.0", http, NullInstaller.Instance);

        var info = await source.CheckAsync(TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotNull(info);
            Assert.DoesNotContain("update", info!.AssetName, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Null(info);
        }
    }

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
            var ver = r.TagName.StartsWith("v") ? r.TagName.Substring(1) : r.TagName;
            if (r.IncludeWinAsset)
            {
                if (!first) sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-win.zip", 25_000_000);
                sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-win.zip.sha256", 64);
                first = false;
            }
            if (r.IncludeLinuxAsset)
            {
                if (!first) sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-linux.tar.gz", 26_000_000);
                sb.Append(',');
                AppendAsset(sb, $"VPNRouter-v{ver}-linux.tar.gz.sha256", 64);
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

    private sealed record ReleaseShape(
        string TagName,
        bool Prerelease = false,
        bool IncludeWinAsset = true,
        bool IncludeLinuxAsset = true,
        bool IncludeLiteAsset = false);

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
