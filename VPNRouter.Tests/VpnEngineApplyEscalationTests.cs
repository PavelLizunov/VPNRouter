using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace VPNRouter.Tests;

public sealed class VpnEngineApplyEscalationTests
{
    private const string StructuralAggregation =
        "forceRestart |= configModeChanged || routingModeChanged || tunChanged || appRoutingChanged;";

    [Fact]
    public void ApplyAsync_HasConfigModeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("ConfigMode change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasRoutingModeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("RoutingMode change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasTunFingerprintEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("TUN settings change detected", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_HasEffectiveAppRoutingChangeEscalation()
    {
        var src = LoadVpnEngineSource();
        if (src == null) return;
        Assert.Contains("Effective app routing change detected", src);
        Assert.Contains("ComputeAppRoutingFingerprint", src);
        Assert.Contains(StructuralAggregation, src);
    }

    [Fact]
    public void ApplyAsync_PassesForceRestartToReloadConfigJson()
    {
        var src = LoadVpnEngineSource();
        Assert.True(src != null, "VpnEngine.cs source file could not be loaded.");

        var lines = src.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var nonCommentLines = lines.Select(line =>
        {
            var commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            return commentIdx >= 0 ? line.Substring(0, commentIdx) : line;
        });
        var effectiveSrc = string.Join("\n", nonCommentLines);

        var hasForceRestartArg =
            effectiveSrc.Contains("ReloadConfigJsonWithResult(configJson, forceRestart)", StringComparison.Ordinal) ||
            effectiveSrc.Contains("ReloadConfigJsonWithResult(configJson, true)", StringComparison.Ordinal);
        Assert.True(hasForceRestartArg,
            "VpnEngine.ApplyAsync must pass forceRestart through to SingBoxManager.ReloadConfigJsonWithResult — see v2.31.7-r1 brat fix.");

        var aggregationIndex = effectiveSrc.IndexOf(StructuralAggregation, StringComparison.Ordinal);
        var reloadIndex = effectiveSrc.IndexOf(
            "ReloadConfigJsonWithResult(configJson, forceRestart)",
            StringComparison.Ordinal);
        Assert.True(aggregationIndex >= 0 && reloadIndex > aggregationIndex,
            "Structural changes must aggregate before ReloadConfigJsonWithResult consumes forceRestart.");
    }

    private static string? LoadVpnEngineSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "VPNRouter.Core", "Services", "VpnEngine.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

}
