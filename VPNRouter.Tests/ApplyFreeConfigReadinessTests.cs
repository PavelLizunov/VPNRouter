using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

/// <summary>
/// 2026-09-16 review P1: ApplyFreeConfigAsync must paint Connected only on
/// TwoPhaseStartOutcome.Connected, matching ToggleConnectionAsync.
/// </summary>
public sealed class ApplyFreeConfigReadinessTests
{
    [Fact]
    public void ApplyFreeConfigAsync_SetsIsConnectedTrue_OnlyOnConnectedOutcome()
    {
        var src = ReadRepoFile("VPNRouter.App", "ViewModels", "MainWindowViewModel.FreeConfigs.cs");
        Assert.Contains("if (outcome != Internals.TwoPhaseStartOutcome.Connected)", src);
        Assert.Contains("IsConnected = false;", src);

        var connectedBlock = src.IndexOf("try { await startTask; } catch { /* event-side Connected is authoritative */ }", StringComparison.Ordinal);
        Assert.True(connectedBlock >= 0, "Connected-path await startTask pin missing");
        var after = src[connectedBlock..];
        Assert.Contains("IsConnected = true;", after);

        var falseGreen = "try { await startTask; } catch { }\n            IsConnected = true;";
        Assert.DoesNotContain(falseGreen, src.Replace("\r\n", "\n"));
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
