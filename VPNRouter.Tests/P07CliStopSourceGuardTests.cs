namespace VPNRouter.Tests;

/// <summary>
/// P07 source-shape guards: pin the CLI stop ownership gate (CLI-2),
/// owner-signal protocol (CLI-1), and Android error scrub (AND-1).
/// </summary>
public sealed class P07CliStopSourceGuardTests
{
    [Fact]
    public void StopCommand_HasOwnershipGate_AndOwnerSignal()
    {
        var stop = ReadCliFile("Commands", "StopCommand.cs").ReplaceLineEndings("\n");
        Assert.Equal(
            2,
            stop.Split(
                "ProcessOwnership.TryReadOwnedSingBoxIdentity",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("observedIdentity is null && !proc.HasExited", stop);
        Assert.Contains("ProcessOwnership.IsSameProcessIdentity", stop);
        Assert.Contains(
            "var pinnedWindowsHandle = OperatingSystem.IsWindows()\n"
            + "                    ? proc.SafeHandle\n"
            + "                    : null;",
            stop);
        Assert.Contains(
            "pinnedWindowsHandle is { IsInvalid: true } or { IsClosed: true }",
            stop);
        Assert.Contains("var exited = proc.WaitForExit(5000)", stop);
        Assert.Contains("GC.KeepAlive(pinnedWindowsHandle)", stop);
        Assert.Contains("StopEventPrefix", stop);
        Assert.Contains("EventWaitHandle.TryOpenExisting", stop);
        Assert.Contains("ownerEvent.Set()", stop);

        Assert.Equal(
            1,
            stop.Split("proc.Kill(", StringSplitOptions.None).Length - 1);
        var firstRead = stop.IndexOf("TryReadOwnedSingBoxIdentity", StringComparison.Ordinal);
        var ownerWait = stop.IndexOf("TrySignalOwnerAndWait(state.OwnerPid)", StringComparison.Ordinal);
        var pinnedHandle = stop.IndexOf("var pinnedWindowsHandle", StringComparison.Ordinal);
        var revalidation = stop.LastIndexOf("TryReadOwnedSingBoxIdentity", StringComparison.Ordinal);
        var comparison = stop.IndexOf("IsSameProcessIdentity", StringComparison.Ordinal);
        var kill = stop.IndexOf("proc.Kill(", StringComparison.Ordinal);
        var waitForExit = stop.IndexOf("var exited = proc.WaitForExit(5000)", StringComparison.Ordinal);
        var keepAlive = stop.IndexOf("GC.KeepAlive(pinnedWindowsHandle)", StringComparison.Ordinal);
        var clear = stop.LastIndexOf("StateFile.Clear()", StringComparison.Ordinal);
        Assert.True(
            firstRead >= 0
            && firstRead < ownerWait
            && ownerWait < pinnedHandle
            && pinnedHandle < revalidation
            && revalidation < comparison
            && comparison < kill
            && kill < waitForExit
            && waitForExit < keepAlive
            && keepAlive < clear,
            "capture must precede the wait; pinned-handle comparison must precede Kill/state cleanup");

        var start = ReadCliFile("Commands", "StartCommand.cs");
        Assert.Contains("OwnerPid = Environment.ProcessId", start);
        Assert.Contains("new EventWaitHandle(", start);
        Assert.Contains("cts.Cancel()", start);
    }

    [Fact]
    public void Android_StartTunnel_ScrubsError_NoRawThrowable()
    {
        var source = ReadRepoFile("VPNRouter.Android", "VpnRouterService.java");
        Assert.Contains("scrubSecrets(e.getMessage())", source);
        // Throwable.toString() embeds the unsanitized message in the
        // stack-trace header — must not be passed to Log.e on this path.
        Assert.DoesNotContain("safeMsg, e)", source);
    }

    private static string ReadCliFile(params string[] segments)
        => ReadRepoFile(new[] { "VPNRouter.CLI" }.Concat(segments).ToArray());

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
