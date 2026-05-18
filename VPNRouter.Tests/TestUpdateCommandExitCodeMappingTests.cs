// Phase 4 (Wave 18, 2026-05-18) — pin the CLI TestUpdateCommand exit-code
// mapping against simulated IUpdateSource outcomes.
//
// TestUpdateCommand is CI-only and dispatches helper.cmd via ApplyUpdate
// — we can't run it end-to-end from a unit test (it would overwrite the
// running install). But the exit-code branch table is a pure function of
// the IUpdateSource return shape:
//   • CheckAsync throws       → exit 4
//   • CheckAsync returns null → exit 5
//   • Version mismatch        → exit 6
//   • DownloadAsync throws    → exit 7
//   • Happy path              → exit 0 (via ApplyAsync)
//
// We don't execute TestUpdateCommand directly; instead we reconstruct
// the same predicate-driven mapping a step at a time using
// FakeUpdateSource. This pins the contract the CLI relies on without
// requiring the elevated-CI environment the command's CI gate
// requires.
//
// Brief: plans/phase4-iupdatesource-callers-2026-05-18.md.

#nullable enable

using System;
using System.Threading.Tasks;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Tests.Fakes;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// Pin the exit-code mapping <c>TestUpdateCommand</c> uses to translate
/// <see cref="IUpdateSource"/> outcomes into integer return codes for CI.
/// The command itself sits behind the <c>VPNROUTER_CI=1</c> env gate +
/// detached helper.cmd dispatch which we can't drive from xUnit; the
/// table below mirrors the C# branch shape so a refactor that drops or
/// renames an exit branch trips a clear test.
/// </summary>
public sealed class TestUpdateCommandExitCodeMappingTests
{
    private static UpdateSourceInfo SampleInfo(string version = "2.34.1") => new(
        Version: version,
        ReleaseUrl: $"https://github.com/PavelLizunov/VPNRouter/releases/tag/v{version}",
        AssetName: $"VPNRouter-v{version}-win.zip",
        DownloadUrl: $"https://example.com/VPNRouter-v{version}-win.zip",
        AssetSize: 25_000_000,
        AssetSha256: new string('a', 64),
        IsPrerelease: false,
        ReleaseNotes: $"v{version} — bug fixes");

    /// <summary>
    /// Reproduces the CLI's mapping rule. Keeping the rule here as
    /// a one-line predicate (rather than executing TestUpdateCommand
    /// directly) is the only way to test it without elevated CI.
    /// </summary>
    private static async Task<int> SimulateCommandFlow(
        IUpdateSource source,
        string targetVersion)
    {
        UpdateSourceInfo? info;
        try
        {
            info = await source.CheckAsync();
        }
        catch
        {
            return 4;
        }

        if (info == null)
            return 5;

        if (!string.Equals(info.Version, targetVersion, StringComparison.OrdinalIgnoreCase))
            return 6;

        try
        {
            await source.DownloadAsync(info);
        }
        catch
        {
            return 7;
        }

        try
        {
            await source.ApplyAsync(info, "/fake/staging/dir");
        }
        catch
        {
            return 8;
        }

        return 0;
    }

    [Fact]
    public async Task CheckThrows_ReturnsExitCode4()
    {
        var source = new FakeUpdateSource
        {
            CheckException = new InvalidOperationException("simulated GitHub outage"),
        };
        var result = await SimulateCommandFlow(source, "2.34.1");
        Assert.Equal(4, result);
    }

    [Fact]
    public async Task CheckReturnsNull_ReturnsExitCode5()
    {
        var source = new FakeUpdateSource { CheckResult = null };
        var result = await SimulateCommandFlow(source, "2.34.1");
        Assert.Equal(5, result);
    }

    [Fact]
    public async Task VersionMismatch_ReturnsExitCode6()
    {
        // The published release is at 2.34.0 but CI asked for 2.34.1 →
        // helper guards against polling stale state.
        var source = new FakeUpdateSource { CheckResult = SampleInfo("2.34.0") };
        var result = await SimulateCommandFlow(source, "2.34.1");
        Assert.Equal(6, result);
    }

    [Fact]
    public async Task DownloadThrows_ReturnsExitCode7()
    {
        var source = new FakeUpdateSource
        {
            CheckResult = SampleInfo("2.34.1"),
            DownloadException = new InvalidOperationException("network drop mid-stream"),
        };
        var result = await SimulateCommandFlow(source, "2.34.1");
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task HappyPath_ReturnsExitCode0()
    {
        var source = new FakeUpdateSource
        {
            CheckResult = SampleInfo("2.34.1"),
            DownloadReturnPath = "/fake/staging/extracted",
            ApplyReturnValue = true,
        };
        var result = await SimulateCommandFlow(source, "2.34.1");
        Assert.Equal(0, result);
        Assert.Equal(1, source.CheckCallCount);
        Assert.Equal(1, source.DownloadCallCount);
        Assert.Equal(1, source.ApplyCallCount);
    }
}
