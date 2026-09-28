#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VPNRouter.Tests;

public sealed class SingBoxManagerRestartInProgressSuppressionTests
{
    [Fact]
    public void Source_RestartInProgressFlag_DeclaredAsVolatile()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.Contains("private volatile bool _restartInProgress", src);
    }

    [Fact]
    public void Source_Restart_SetsFlagTrueBeforeStopInternal()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.Lifecycle.cs");

        var restartHeader = src.IndexOf("public void Restart()", StringComparison.Ordinal);
        Assert.True(restartHeader >= 0, "Restart() method not found in SingBoxManager.cs");

        var window = src.Substring(restartHeader, Math.Min(3000, src.Length - restartHeader));

        var flagSetIdx = window.IndexOf("_restartInProgress = true", StringComparison.Ordinal);
        var stopCallIdx = window.IndexOf("StopInternal(releaseLock: false)", StringComparison.Ordinal);

        Assert.True(flagSetIdx >= 0,
            "Expected `_restartInProgress = true` inside Restart() before StopInternal call.");
        Assert.True(stopCallIdx >= 0,
            "Expected `StopInternal(releaseLock: false)` inside Restart() (this is the load-bearing line that triggers the OS Exited race).");
        Assert.True(flagSetIdx < stopCallIdx,
            "`_restartInProgress = true` must appear BEFORE StopInternal call. Otherwise the OS Exited callback can fire before the flag is set, leaking through to Crashed.Invoke. " +
            $"flagSetIdx={flagSetIdx}, stopCallIdx={stopCallIdx}");
    }

    [Fact]
    public void Source_Restart_ClearsFlagInFinally()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.Lifecycle.cs");

        var restartHeader = src.IndexOf("public void Restart()", StringComparison.Ordinal);
        var window = src.Substring(restartHeader, Math.Min(8000, src.Length - restartHeader));

        var finallyIdx = window.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyIdx > 0, "Expected a `finally` block inside Restart() to clear the flag.");

        var clearIdx = window.IndexOf("_restartInProgress = false", finallyIdx, StringComparison.Ordinal);
        Assert.True(clearIdx > 0,
            "Expected `_restartInProgress = false` inside the finally block. Without this, an exception from LaunchProcess leaves the flag stuck TRUE and all future genuine crashes get wrongly suppressed.");
    }

    [Fact]
    public void Source_OnProcessExited_ChecksFlagAndIntentionalKillExitCodes()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        Assert.True(handler >= 0, "OnProcessExited method not found");

        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        Assert.True(flagCheckIdx > 0,
            "Expected `_restartInProgress` referenced inside OnProcessExited as the gate condition for the suppression branch.");

        Assert.Contains("-1", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
        Assert.Contains("137", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
        Assert.Contains("143", window.Substring(flagCheckIdx, Math.Min(500, window.Length - flagCheckIdx)));
    }

    [Fact]
    public void Source_OnProcessExited_SuppressionBranchLogsAtInformationLevel()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        Assert.True(flagCheckIdx > 0);

        var branchWindow = window.Substring(flagCheckIdx, Math.Min(1500, window.Length - flagCheckIdx));
        var infoIdx = branchWindow.IndexOf("_logger.Information", StringComparison.Ordinal);
        var warningIdx = branchWindow.IndexOf("_logger.Warning", StringComparison.Ordinal);
        var errorIdx = branchWindow.IndexOf("_logger.Error", StringComparison.Ordinal);

        Assert.True(infoIdx >= 0,
            "Expected `_logger.Information(...)` inside the suppression branch — must log audit trail at INF level so the post-ship scanners don't false-flag.");
        if (warningIdx >= 0)
            Assert.True(infoIdx < warningIdx,
                "`_logger.Information` for the suppression branch must come BEFORE the `_logger.Warning` for the (separate) exit-code-0 branch.");
        if (errorIdx >= 0)
            Assert.True(infoIdx < errorIdx,
                "`_logger.Information` for the suppression branch must come BEFORE the `_logger.Error` for the (separate) exit-code-nonzero branch.");
    }

    [Fact]
    public void Source_OnProcessExited_SuppressionBranchReturnsEarly()
    {
        var src = ReadSourceFile("VPNRouter.Core", "Services", "SingBoxManager.cs");

        var handler = src.IndexOf("private void OnProcessExited()", StringComparison.Ordinal);
        var window = src.Substring(handler, Math.Min(8000, src.Length - handler));

        var flagCheckIdx = window.IndexOf("_restartInProgress", StringComparison.Ordinal);
        var crashedInvokeIdx = window.IndexOf("Crashed?.Invoke", StringComparison.Ordinal);
        Assert.True(crashedInvokeIdx > flagCheckIdx,
            "Crashed?.Invoke must appear AFTER the _restartInProgress guard, otherwise the guard can't prevent the invoke.");

        var between = window.Substring(flagCheckIdx, crashedInvokeIdx - flagCheckIdx);
        Assert.Contains("return;", between);
    }

    private static string ReadSourceFile(params string[] segments)
    {
        var thisAssembly = typeof(VPNRouter.Core.Services.SingBoxManager).Assembly;
        var binDir = Path.GetDirectoryName(thisAssembly.Location)!;
        var dir = new DirectoryInfo(binDir);
        while (dir != null)
        {
            var candidate = Path.Combine((new[] { dir.FullName }).Concat(segments).ToArray());
            if (File.Exists(candidate)) return ReadAllParts(candidate);
            dir = dir.Parent;
        }
        var fallback = Path.Combine((new[] { Environment.CurrentDirectory }).Concat(segments).ToArray());
        if (!File.Exists(fallback))
            throw new FileNotFoundException($"Source file not found: {string.Join("/", segments)}");
        return ReadAllParts(fallback);
    }

    private static string ReadAllParts(string primaryPath)
    {
        var dir = Path.GetDirectoryName(primaryPath)!;
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        var parts = Directory.GetFiles(dir, stem + "*.cs")
            .Where(p =>
            {
                var fn = Path.GetFileName(p);
                return fn == stem + ".cs" || fn.StartsWith(stem + ".", StringComparison.Ordinal);
            })
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join("\n", parts.Select(File.ReadAllText));
    }
}
