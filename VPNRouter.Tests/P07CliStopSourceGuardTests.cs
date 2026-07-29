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
        var stop = ReadCliFile("Commands", "StopCommand.cs");
        Assert.Contains("ProcessOwnership.IsOwnedSingBox", stop);
        Assert.Contains("StopEventPrefix", stop);
        Assert.Contains("EventWaitHandle.TryOpenExisting", stop);
        Assert.Contains("ownerEvent.Set()", stop);
        Assert.True(
            stop.IndexOf("IsOwnedSingBox", StringComparison.Ordinal)
            < stop.IndexOf(".Kill(", StringComparison.Ordinal),
            "ownership gate must precede the legacy Kill fallback");

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
