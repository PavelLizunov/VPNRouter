namespace VPNRouter.Tests;

/// <summary>
/// P07 source-shape guards: pin the CLI start/stop generation contract,
/// StateFile persistence invariants, and Android error scrub (AND-1).
/// </summary>
public sealed class P07CliStopSourceGuardTests
{
    [Fact]
    public void StartCommand_ApprovedGenerationContract_HasRequiredSourceShape()
    {
        var start = ReadCliFile("Commands", "StartCommand.cs");

        Assert.Contains("var runGeneration = Guid.NewGuid()", start);
        Assert.Contains("ProcessOwnership.TryReadProcessIdentity(ownerProcess)", start);
        Assert.Contains("StateFile.TryUpdateChild(runGeneration, identity)", start);
        Assert.Contains("StopCommand.BuildStopEventName(owner.Pid, runGeneration)", start);
        Assert.Contains("OwnerStartedAtUtcTicks = owner.StartedAtUtcTicks", start);
        Assert.Contains("SingBoxStartedAtUtcTicks = child.StartedAtUtcTicks", start);
        Assert.Contains("StateFile.ClearIfGeneration(runGeneration)", start);
        Assert.DoesNotContain("StateFile.Clear()", start);

        var eventCreation = start.IndexOf("stopEvent = new EventWaitHandle(", StringComparison.Ordinal);
        var registration = start.IndexOf("ThreadPool.RegisterWaitForSingleObject(", StringComparison.Ordinal);
        var publication = start.IndexOf("StateFile.Write(new RunState", StringComparison.Ordinal);
        Assert.True(
            eventCreation >= 0
            && eventCreation < registration
            && registration < publication,
            "The generation-qualified event must be created and registered before state publication.");
    }

    [Fact]
    public void StopCommand_ApprovedGenerationContract_HasRequiredSourceShape()
    {
        var stop = ReadCliFile("Commands", "StopCommand.cs");

        Assert.Contains("observed.RunGeneration == Guid.Empty", stop);
        Assert.Contains("observed.OwnerStartedAtUtcTicks", stop);
        Assert.Contains("observed.OwnerExecutablePath", stop);
        Assert.Contains("target.SingBoxStartedAtUtcTicks", stop);
        Assert.Contains("target.SingBoxExecutablePath", stop);
        Assert.Contains("BuildStopEventName(expectedOwner.Pid, generation)", stop);
        Assert.Contains("ProcessOwnership.TryReadProcessIdentity(ownerProcess)", stop);
        Assert.Contains("ProcessOwnership.TryReadOwnedSingBoxIdentity(process)", stop);
        Assert.Contains("ProcessOwnership.IsSameProcessIdentity", stop);
        Assert.Contains("var pinnedOwnerHandle = ownerProcess.SafeHandle", stop);
        Assert.Contains("var pinnedWindowsHandle = OperatingSystem.IsWindows()", stop);
        Assert.Contains("latest.RunGeneration != generation", stop);
        Assert.Contains("StateFile.ClearIfGeneration(generation)", stop);
        Assert.DoesNotContain("StateFile.Clear()", stop);
        Assert.DoesNotContain("Process.GetProcessesByName", stop);
        Assert.DoesNotContain("Process.GetProcesses()", stop);
        Assert.Equal(1, stop.Split(".Kill(", StringSplitOptions.None).Length - 1);

        var executeEnd = stop.IndexOf("internal static string BuildStopEventName", StringComparison.Ordinal);
        var execute = stop[..executeEnd];
        var legacyGuard = execute.IndexOf("observed.RunGeneration == Guid.Empty", StringComparison.Ordinal);
        var ownerCall = execute.IndexOf("TrySignalOwnerAndWait(expectedOwner.Value, generation)", StringComparison.Ordinal);
        var replacementGuard = execute.IndexOf("latest.RunGeneration != generation", StringComparison.Ordinal);
        var childCall = execute.IndexOf("TryStopExactChild(expectedChild.Value)", StringComparison.Ordinal);
        var clear = execute.IndexOf("StateFile.ClearIfGeneration(generation)", StringComparison.Ordinal);
        Assert.True(
            legacyGuard >= 0
            && legacyGuard < ownerCall
            && ownerCall < replacementGuard
            && replacementGuard < childCall
            && childCall < clear,
            "Legacy/replacement guards must precede exact child stop and conditional cleanup.");

        var ownerHelperStart = stop.IndexOf("private static OwnerStopResult TrySignalOwnerAndWait", StringComparison.Ordinal);
        var ownerHelperEnd = stop.IndexOf("private static bool IsExactProcessAlive", StringComparison.Ordinal);
        var ownerHelper = stop[ownerHelperStart..ownerHelperEnd];
        Assert.True(
            ownerHelper.IndexOf("TryReadProcessIdentity", StringComparison.Ordinal)
                < ownerHelper.IndexOf("EventWaitHandle.TryOpenExisting", StringComparison.Ordinal)
            && ownerHelper.IndexOf("EventWaitHandle.TryOpenExisting", StringComparison.Ordinal)
                < ownerHelper.IndexOf("ownerEvent.Set()", StringComparison.Ordinal)
            && ownerHelper.IndexOf("ownerEvent.Set()", StringComparison.Ordinal)
                < ownerHelper.IndexOf("WaitForExit(5000)", StringComparison.Ordinal),
            "The exact owner must be pinned and compared before signaling and waiting.");

        var childHelper = stop[stop.IndexOf("private static bool TryStopExactChild", StringComparison.Ordinal)..];
        Assert.True(
            childHelper.IndexOf("TryReadOwnedSingBoxIdentity", StringComparison.Ordinal)
                < childHelper.IndexOf("IsSameProcessIdentity", StringComparison.Ordinal)
            && childHelper.IndexOf("IsSameProcessIdentity", StringComparison.Ordinal)
                < childHelper.IndexOf(".Kill(", StringComparison.Ordinal)
            && childHelper.IndexOf(".Kill(", StringComparison.Ordinal)
                < childHelper.IndexOf("WaitForExit(5000)", StringComparison.Ordinal),
            "The pinned child identity must be compared before the sole Kill and wait.");
    }

    [Fact]
    public void StateFile_ApprovedGenerationContract_HasRequiredSourceShape()
    {
        var stateFile = ReadCliFile("Helpers", "StateFile.cs");

        Assert.Contains("RunGeneration", stateFile);
        Assert.Contains("OwnerStartedAtUtcTicks", stateFile);
        Assert.Contains("SingBoxStartedAtUtcTicks", stateFile);
        Assert.Contains("new Mutex(", stateFile);
        Assert.Contains("mutex.WaitOne(StateLockTimeout)", stateFile);
        Assert.Contains("AbandonedMutexException", stateFile);
        Assert.Contains("AppPaths.CreatePrivateFile(tmp)", stateFile);
        Assert.Contains("stream.Flush(true)", stateFile);
        Assert.Contains("File.Move(tmp, path, overwrite: true)", stateFile);
        Assert.Contains("current.RunGeneration != generation", stateFile);
        Assert.Contains("TryUpdateChild", stateFile);
        Assert.Contains("ClearIfGeneration", stateFile);
        Assert.DoesNotContain("public static void Clear()", stateFile);

        var generationCheck = stateFile.LastIndexOf("current.RunGeneration != generation", StringComparison.Ordinal);
        var delete = stateFile.IndexOf("File.Delete(path)", StringComparison.Ordinal);
        Assert.True(generationCheck >= 0 && generationCheck < delete,
            "Conditional clear must compare generation while holding the shared lock before deletion.");
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
