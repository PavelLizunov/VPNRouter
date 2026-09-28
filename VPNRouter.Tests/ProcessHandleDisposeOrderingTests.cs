#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class ProcessHandleDisposeOrderingTests
{
    [Fact]
    public void Dispose_DisablesEventsBeforeKill_SourcePin()
    {
        var src = LoadProcessRunnerSource();
        Assert.SkipUnless(src != null, "ProcessRunner.cs source not reachable from test cwd — source-pin skipped");

        var stripped = StripLineComments(src!);

        var oneline = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"\s+", " ");

        var disposeIdx = oneline.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(disposeIdx >= 0, "ProcessRunner.cs must contain a 'public void Dispose()' method on ProcessHandle");

        var disposeRegion = oneline.Substring(
            disposeIdx,
            Math.Min(800, oneline.Length - disposeIdx));

        var enableIdx = disposeRegion.IndexOf(
            "_process.EnableRaisingEvents = false;",
            StringComparison.Ordinal);
        Assert.True(enableIdx > 0,
            "ProcessHandle.Dispose must set _process.EnableRaisingEvents = false " +
            "BEFORE killing the process (intentional-stop pattern that every " +
            "long-lived spawn consumer relies on transitively).");

        var killIdx = disposeRegion.IndexOf("Kill(", StringComparison.Ordinal);
        Assert.True(killIdx > 0,
            "ProcessHandle.Dispose must invoke Kill (directly or via the Kill helper).");

        Assert.True(enableIdx < killIdx,
            "Intentional-stop ordering violated: " +
            "_process.EnableRaisingEvents = false MUST come BEFORE Kill " +
            $"inside ProcessHandle.Dispose. Got EnableRaisingEvents at " +
            $"{enableIdx}, Kill at {killIdx}. This is the centralised " +
            "SingBoxManager intentional-stop invariant — Kill() must NOT " +
            "trigger the Exited callback for an intentional Dispose.");
    }

    [Fact]
    public void ProcessHandle_Kill_BeforeDispose_FiresKill_NotEnableRaisingEvents()
    {
        var src = LoadProcessRunnerSource();
        Assert.SkipUnless(src != null, "ProcessRunner.cs source not reachable from test cwd — source-pin skipped");

        var stripped = StripLineComments(src!);
        var oneline = System.Text.RegularExpressions.Regex.Replace(
            stripped, @"\s+", " ");

        var killIdx = oneline.IndexOf(
            "public void Kill(bool entireProcessTree",
            StringComparison.Ordinal);
        Assert.True(killIdx >= 0,
            "ProcessRunner.cs must contain ProcessHandle.Kill(bool entireProcessTree = ...)");

        var nextPublic = oneline.IndexOf("public ", killIdx + 1, StringComparison.Ordinal);
        var endIdx = nextPublic < 0 ? Math.Min(oneline.Length, killIdx + 600)
                                    : Math.Min(nextPublic, killIdx + 600);
        var killRegion = oneline.Substring(killIdx, endIdx - killIdx);

        Assert.DoesNotContain("EnableRaisingEvents = false", killRegion);
    }

    private static string? LoadProcessRunnerSource()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "VPNRouter.Core", "Services", "ProcessRunner.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        return null;
    }

    private static string StripLineComments(string src)
    {
        return string.Join('\n',
            src.Split('\n').Select(l =>
                l.Contains("//") ? l[..l.IndexOf("//")] : l));
    }
}
