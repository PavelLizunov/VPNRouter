using System.IO;
using VPNRouter.Core.Services;

namespace VPNRouter.Tests;

/// <summary>
/// v2.49 regression coverage for the connected-Apply structural baseline.
/// </summary>
public sealed class VpnEngineApplyStructuralChangeTests
{
    [Fact]
    public void DetectStructuralChanges_IdenticalState_NoChanges()
    {
        var changes = VpnEngine.DetectStructuralChanges(
            "generated", "GENERATED", "split", "SPLIT", "tun", "tun",
            "include:chrome.exe", "include:chrome.exe");

        Assert.False(changes.ConfigModeChanged);
        Assert.False(changes.RoutingModeChanged);
        Assert.False(changes.TunChanged);
        Assert.False(changes.AppRoutingChanged);
    }

    [Theory]
    [InlineData("generated", "custom", "split", "split", "tun", "tun", "apps", "apps", true, false, false, false)]
    [InlineData("generated", "generated", "split", "full", "tun", "tun", "apps", "apps", false, true, false, false)]
    [InlineData("generated", "generated", "split", "split", "tun-a", "tun-b", "apps", "apps", false, false, true, false)]
    [InlineData("generated", "generated", "split", "split", "tun", "tun", "include:a", "include:b", false, false, false, true)]
    [InlineData("generated", "generated", "split", "split", "tun", "tun", "include:Chrome.exe", "include:chrome.exe", false, false, false, true)]
    public void DetectStructuralChanges_OneAxisChanges_ReportsThatAxis(
        string activeConfigMode,
        string candidateConfigMode,
        string activeRoutingMode,
        string candidateRoutingMode,
        string activeTunFingerprint,
        string candidateTunFingerprint,
        string activeAppFingerprint,
        string candidateAppFingerprint,
        bool expectedConfigModeChanged,
        bool expectedRoutingModeChanged,
        bool expectedTunChanged,
        bool expectedAppRoutingChanged)
    {
        var changes = VpnEngine.DetectStructuralChanges(
            activeConfigMode,
            candidateConfigMode,
            activeRoutingMode,
            candidateRoutingMode,
            activeTunFingerprint,
            candidateTunFingerprint,
            activeAppFingerprint,
            candidateAppFingerprint);

        Assert.Equal(expectedConfigModeChanged, changes.ConfigModeChanged);
        Assert.Equal(expectedRoutingModeChanged, changes.RoutingModeChanged);
        Assert.Equal(expectedTunChanged, changes.TunChanged);
        Assert.Equal(expectedAppRoutingChanged, changes.AppRoutingChanged);
    }

    [Fact]
    public void ApplyGatedAsync_CapturesLiveBaselineBeforeHotReloadPipeline()
    {
        var source = LoadVpnEngineSource();
        if (source == null) return;

        var captureIndex = source.IndexOf(
            "var oldRoutingMode = ActiveRoutingMode;",
            StringComparison.Ordinal);
        var pipelineIndex = source.IndexOf(
            "new StartupContext(settings, StartupMode.HotReload)",
            StringComparison.Ordinal);

        Assert.True(captureIndex >= 0, "Apply must capture the live routing baseline.");
        Assert.True(pipelineIndex > captureIndex,
            "The live baseline must be captured before StartupPipeline mutates candidate state.");
    }

    [Fact]
    public void ApplyGatedAsync_FailurePathsRestoreLiveBaseline()
    {
        var source = LoadVpnEngineSource();
        if (source == null) return;

        var restoreCount = source.Split("RestoreActiveBaseline();", StringSplitOptions.None).Length - 1;

        Assert.True(restoreCount >= 2,
            "Pipeline failure and exception paths must both restore the live Apply baseline.");
        Assert.Contains(
            "ActiveAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!configCommitted)",
            source,
            StringComparison.Ordinal);
    }

    private static string? LoadVpnEngineSource()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "VPNRouter.Core",
                "Services",
                "VpnEngine.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        return null;
    }
}
