using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ApplyFreeConfigReadinessTests
{
    [Fact]
    public void ApplyFreeConfigAsync_SetsIsConnectedTrue_OnlyOnConnectedOutcome()
    {
        var src = ReadRepoFile("VPNRouter.App", "ViewModels", "MainWindowViewModel.FreeConfigs.cs");
        Assert.Contains("if (outcome != Internals.TwoPhaseStartOutcome.Connected)", src);
        Assert.Contains("IsConnected = false;", src);

        var guard = src.IndexOf("if (outcome != Internals.TwoPhaseStartOutcome.Connected)", StringComparison.Ordinal);
        var earlyReturn = src.IndexOf("return false;", guard, StringComparison.Ordinal);
        var green = src.IndexOf("IsConnected = true;", earlyReturn, StringComparison.Ordinal);
        Assert.True(guard >= 0 && earlyReturn > guard, "Non-Connected outcomes must return before greening");
        Assert.True(green > earlyReturn, "IsConnected = true must follow the non-Connected early return");
    }

    [Fact]
    public void ToggleConnectionAsync_StillGreensOnlyOnConnected()
    {
        var src = ReadRepoFile("VPNRouter.App", "ViewModels", "MainWindowViewModel.Connection.cs");
        Assert.Contains("if (outcome == Internals.TwoPhaseStartOutcome.Connected)", src);
        Assert.Contains("else if (outcome == Internals.TwoPhaseStartOutcome.StartTaskCompleted)", src);
        var startTaskIdx = src.IndexOf("else if (outcome == Internals.TwoPhaseStartOutcome.StartTaskCompleted)", StringComparison.Ordinal);
        Assert.True(startTaskIdx >= 0);
        var startTaskBlock = src.Replace("\r\n", "\n").Substring(startTaskIdx,
            Math.Min(500, src.Length - startTaskIdx));
        Assert.DoesNotContain("IsConnected = true;", startTaskBlock);
    }

    private static string ReadRepoFile(params string[] segments)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
             dir != null;
             dir = dir.Parent)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(segments)} near {AppContext.BaseDirectory}");
    }
}
