namespace VPNRouter.Tests;

public sealed class UnixStopSourceGuardTests
{
    [Fact]
    public void LinuxPidFdHelper_BindsIdentityBeforeSignal()
    {
        var source = ReadRepoFile("VPNRouter.Core", "Services", "UnixOwnedProcessSignal.cs");
        var open = source.IndexOf("PidFdOpen(SysPidFdOpen", StringComparison.Ordinal);
        var read = source.IndexOf("TryReadOwnedSingBoxIdentity(process)", StringComparison.Ordinal);
        var compare = source.IndexOf("IsSameProcessIdentity(expected, current.Value)", StringComparison.Ordinal);
        var signal = source.IndexOf("PidFdSendSignal(SysPidFdSendSignal", StringComparison.Ordinal);

        Assert.True(open >= 0 && open < read && read < compare && compare < signal,
            "pidfd open and fresh identity comparison must precede pidfd signaling");
        Assert.Contains("signal is not (9 or 15)", source);
    }

    [Fact]
    public void UnixStopAndUpdatePaths_HaveNoPatternBasedSingBoxSignal()
    {
        var files = new[]
        {
            ReadRepoFile("VPNRouter.Core", "Services", "SingBoxManager.LinuxStop.cs"),
            ReadRepoFile("VPNRouter.Core", "Services", "SingBoxManager.Lifecycle.cs"),
            ReadRepoFile("VPNRouter.Core", "Services", "UpdateChecker.cs"),
            ReadRepoFile("packaging", "linux", "vpnrouter-update-helper")
        };

        foreach (var source in files)
        {
            Assert.DoesNotContain("pkill", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pgrep", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ElevatedStop_UsesExactIdentityAndReportsFailure()
    {
        var stop = ReadRepoFile("VPNRouter.Core", "Services", "SingBoxManager.LinuxStop.cs");
        Assert.Contains("ProcessOwnership.FindOwnedSingBox", stop);
        Assert.Contains("BuildLinuxOwnedSignalHelperArguments", stop);
        Assert.Contains("BuildMacExactKillArguments", stop);
        Assert.Contains("InspectOwnedTarget(owned)", stop);
        Assert.Contains("name is \"VPNRouter.App\" or \"VPNRouter.CLI\"", stop);

        var lifecycle = ReadRepoFile("VPNRouter.Core", "Services", "SingBoxManager.Lifecycle.cs");
        Assert.Contains("lock (_lifecycleGate)", lifecycle);
        Assert.Contains("var targetHandle = _handle", lifecycle);
        Assert.Contains("ReferenceEquals(_handle, targetHandle)", lifecycle);
        Assert.Contains("if (!_ownsTunLock)", lifecycle);
        Assert.Contains("TryAcquireExclusive()", lifecycle);
        Assert.Contains("_exactStopUnconfirmed", lifecycle);
        Assert.Contains("State = capabilityStopped ? SingBoxState.Stopped : SingBoxState.Failed", lifecycle);
        Assert.Contains("if (releaseLock && capabilityStopped) ReleaseTunOwnership()", lifecycle);
        Assert.Contains("State = unixStopped ? SingBoxState.Stopped : SingBoxState.Failed", lifecycle);
        Assert.Contains("if (releaseLock && unixStopped) ReleaseTunOwnership()", lifecycle);
        Assert.Contains("Restart ignored — manager does not own the TUN lease", lifecycle);
        Assert.Contains("Restart aborted: exact stop was not confirmed", lifecycle);

        var manager = ReadRepoFile("VPNRouter.Core", "Services", "SingBoxManager.cs");
        Assert.Contains("if (_ownsTunLock)", manager);
        Assert.Contains("Dispose preserved TUN ownership", manager);
    }

    [Fact]
    public void InternalHelperEntryPoints_RunBeforeNormalHostInitialization()
    {
        var app = ReadRepoFile("VPNRouter.App", "Program.cs");
        Assert.True(
            app.IndexOf("UnixOwnedProcessSignal.TryHandleHelper", StringComparison.Ordinal)
            < app.IndexOf("CrashReporter.Install", StringComparison.Ordinal));

        var cli = ReadRepoFile("VPNRouter.CLI", "Program.cs");
        Assert.True(
            cli.IndexOf("UnixOwnedProcessSignal.TryHandleHelper", StringComparison.Ordinal)
            < cli.IndexOf("Directory.CreateDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void LinuxUpdateHelper_SignalsExactIdentityBeforeCopy()
    {
        var helper = ReadRepoFile("packaging", "linux", "vpnrouter-update-helper");
        var signal = helper.IndexOf("--vpnrouter-internal-signal-owned-v1", StringComparison.Ordinal);
        var copy = helper.IndexOf("cp -rfT", StringComparison.Ordinal);

        Assert.Contains("--owned-signal-v1", helper);
        Assert.True(signal >= 0 && signal < copy, "exact owned signal must complete before copy");
        Assert.Contains("exit 6", helper);

        var updater = ReadRepoFile("VPNRouter.Core", "Services", "UpdateChecker.cs");
        Assert.Contains("HelperSupportsExactOwnedSignal(helper)", updater);
        Assert.Contains("AppendOwnedSignalArguments(helperArgs, ownedTarget)", updater);
        Assert.Contains("Exact owned sing-box stop failed", updater);
    }

    [Fact]
    public void MacSudoers_GrantsExactKillRatherThanPatternSweep()
    {
        var source = ReadRepoFile("VPNRouter.App", "ViewModels", "MainWindowViewModel.cs");
        Assert.Contains("NOPASSWD: /bin/kill -KILL -- [0-9]*", source);
        Assert.DoesNotContain("NOPASSWD: /usr/bin/pkill *", source);
        Assert.Contains("exact kill", source);
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
