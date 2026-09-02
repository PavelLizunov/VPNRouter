using System.Diagnostics;
using System.Globalization;

namespace VPNRouter.Core.Services;

public partial class SingBoxManager
{
    private enum OwnedTargetState
    {
        Matching,
        GoneOrReplaced,
        IdentityUnavailable
    }

    /// <summary>
    /// Stop the one sing-box identity published by this VPNRouter owner.
    /// Linux uses pidfd for the direct and elevated helper paths. macOS has
    /// no process descriptor, so it performs a fresh identity check followed
    /// immediately by an exact-PID sudo kill. No branch falls back to a name
    /// or command-line pattern.
    /// </summary>
    private bool LinuxStopEscalationChain()
    {
        var target = ProcessOwnership.FindOwnedSingBox(ProcessOwnership.ConfiguredExePath);
        if (target is not { } owned)
        {
            _logger.Error(
                "[SingBoxManager] Unix stop refused: no exact VPNRouter-owned sing-box identity is available");
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            var direct = UnixOwnedProcessSignal.SignalLinux(owned, signal: 15);
            if (direct == UnixOwnedSignalResult.TargetGone)
                return true;
            if (direct == UnixOwnedSignalResult.IdentityMismatch)
            {
                _logger.Warning(
                    "[SingBoxManager] Linux stop: owned PID {Pid} was replaced; refusing to signal its replacement",
                    owned.Pid);
                return true;
            }

            if (direct == UnixOwnedSignalResult.Signaled)
            {
                Thread.Sleep(800);
                var state = InspectOwnedTarget(owned);
                if (state == OwnedTargetState.GoneOrReplaced)
                {
                    _logger.Information(
                        "[SingBoxManager] Linux stop: exact PID {Pid} exited after pidfd SIGTERM",
                        owned.Pid);
                    return true;
                }
                if (state == OwnedTargetState.IdentityUnavailable)
                    return RefuseUnknownIdentity(owned.Pid);
                _logger.Information(
                    "[SingBoxManager] Linux stop: PID {Pid} survived pidfd SIGTERM; escalating",
                    owned.Pid);
            }
            else
            {
                _logger.Warning(
                    "[SingBoxManager] Linux stop: direct pidfd signal returned {Result}; escalating",
                    direct);
            }

            var hostPath = ResolveSignalHelperHost();
            if (hostPath is null)
            {
                _logger.Error(
                    "[SingBoxManager] Linux stop refused: current VPNRouter helper executable is unavailable");
                return false;
            }

            var pkexec = LinuxRuntimeEnvironment.ResolvePkexec();
            if (pkexec != null)
            {
                _ = TrySpawnAndWait(
                    pkexec,
                    BuildLinuxOwnedSignalHelperArguments(hostPath, owned, signal: 9),
                    30_000,
                    "pkexec exact pidfd SIGKILL");
                Thread.Sleep(500);

                var state = InspectOwnedTarget(owned);
                if (state == OwnedTargetState.GoneOrReplaced)
                {
                    _logger.Information(
                        "[SingBoxManager] Linux stop: exact PID {Pid} exited after pkexec pidfd SIGKILL",
                        owned.Pid);
                    return true;
                }
                if (state == OwnedTargetState.IdentityUnavailable)
                    return RefuseUnknownIdentity(owned.Pid);
            }
            else
            {
                _logger.Warning("[SingBoxManager] Linux stop: trusted pkexec not found");
            }

            _ = TrySpawnAndWait(
                "/usr/bin/sudo",
                BuildLinuxOwnedSignalHelperArguments(hostPath, owned, signal: 9, nonInteractiveSudo: true),
                5_000,
                "sudo -n exact pidfd SIGKILL");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var state = InspectOwnedTarget(owned);
            if (state == OwnedTargetState.GoneOrReplaced)
                return true;
            if (state == OwnedTargetState.IdentityUnavailable)
                return RefuseUnknownIdentity(owned.Pid);

            _logger.Warning(
                "[SingBoxManager] macOS has no pidfd; signaling freshly validated exact PID {Pid}",
                owned.Pid);
            _ = TrySpawnAndWait(
                "/usr/bin/sudo",
                BuildMacExactKillArguments(owned.Pid),
                5_000,
                "sudo -n exact PID SIGKILL");
        }
        else
        {
            return false;
        }

        Thread.Sleep(500);
        var finalState = InspectOwnedTarget(owned);
        if (finalState == OwnedTargetState.GoneOrReplaced)
        {
            _logger.Information(
                "[SingBoxManager] Unix stop: exact owned PID {Pid} is gone",
                owned.Pid);
            return true;
        }

        if (finalState == OwnedTargetState.IdentityUnavailable)
            return RefuseUnknownIdentity(owned.Pid);

        var causes = OperatingSystem.IsMacOS()
            ? "sudoers NOPASSWD exact-kill grant is missing or invalid"
            : "pkexec/polkit agent is unavailable and sudo NOPASSWD is not configured";
        _logger.Error(
            "[SingBoxManager] Unix stop failed: exact VPNRouter-owned PID {Pid} remains alive. " +
            "Manual intervention: verify that PID's start time and executable path, then run `sudo /bin/kill -KILL -- {Pid}`. " +
            "Possible cause: {Cause}.",
            owned.Pid,
            owned.Pid,
            causes);
        return false;
    }

    private bool RefuseUnknownIdentity(int pid)
    {
        _logger.Error(
            "[SingBoxManager] Unix stop refused: PID {Pid} exists but its exact identity cannot be re-read",
            pid);
        return false;
    }

    private static OwnedTargetState InspectOwnedTarget(OwnedProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            if (process.HasExited) return OwnedTargetState.GoneOrReplaced;
            var current = ProcessOwnership.TryReadOwnedSingBoxIdentity(process);
            if (current is null) return OwnedTargetState.IdentityUnavailable;
            return ProcessOwnership.IsSameProcessIdentity(expected, current.Value)
                ? OwnedTargetState.Matching
                : OwnedTargetState.GoneOrReplaced;
        }
        catch (ArgumentException)
        {
            return OwnedTargetState.GoneOrReplaced;
        }
        catch (InvalidOperationException)
        {
            return OwnedTargetState.GoneOrReplaced;
        }
        catch
        {
            return OwnedTargetState.IdentityUnavailable;
        }
    }

    internal static IReadOnlyList<string> BuildLinuxOwnedSignalHelperArguments(
        string hostPath,
        OwnedProcessIdentity target,
        int signal,
        bool nonInteractiveSudo = false)
    {
        var args = new List<string>();
        if (nonInteractiveSudo) args.Add("-n");
        args.Add(hostPath);
        args.Add(UnixOwnedProcessSignal.HelperFlag);
        args.Add(target.Pid.ToString(CultureInfo.InvariantCulture));
        args.Add(target.StartedAtUtcTicks.ToString(CultureInfo.InvariantCulture));
        args.Add(target.ExecutablePath);
        args.Add(signal.ToString(CultureInfo.InvariantCulture));
        return args;
    }

    internal static IReadOnlyList<string> BuildMacExactKillArguments(int pid)
        => new[] { "-n", "/bin/kill", "-KILL", "--", pid.ToString(CultureInfo.InvariantCulture) };

    private static string? ResolveSignalHelperHost()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;

        var name = Path.GetFileNameWithoutExtension(path);
        return name is "VPNRouter.App" or "VPNRouter.CLI" ? path : null;
    }

    private bool TrySpawnAndWait(
        string fileName,
        IReadOnlyList<string> args,
        int timeoutMs,
        string label)
    {
        try
        {
            var result = _runner.RunAsync(new ProcessRequest(
                    ExecutablePath: fileName,
                    Arguments: args,
                    Timeout: TimeSpan.FromMilliseconds(timeoutMs)))
                .GetAwaiter()
                .GetResult();

            _logger.Information(
                "[SingBoxManager] Unix stop: {Label} exit={Code} timeout={TimedOut}",
                label,
                result.ExitCode,
                result.TimedOut);
            return !result.TimedOut && result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[SingBoxManager] Unix stop: {Label} threw", label);
            return false;
        }
    }
}
