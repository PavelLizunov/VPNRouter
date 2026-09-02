using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class StopCommand : Command
{
    internal const string StopEventPrefix = "VPNRouter_CLI_Stop_";

    private enum OwnerStopResult
    {
        Unavailable,
        Exited,
        TimedOut
    }

    private enum ExactProcessState
    {
        DeadOrReplaced,
        Alive,
        Unknown
    }

    private enum ChildStopResult
    {
        Stopped,
        AlreadyGone,
        Refused
    }

    public override int Execute(CommandContext context)
    {
        RunState? observed;
        try
        {
            observed = StateFile.Read();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not read CLI run state");
            AnsiConsole.MarkupLine("[red]Could not read CLI run state.[/]");
            return 1;
        }

        if (observed is null)
        {
            AnsiConsole.MarkupLine("[yellow]VPN Router is not running.[/]");
            return 0;
        }

        if (observed.RunGeneration == Guid.Empty)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Legacy CLI state has no run identity; refusing to signal, kill, or delete it. Stop its original terminal or restart it with this version.[/]");
            return 1;
        }

        var generation = observed.RunGeneration;
        var expectedOwner = PersistedIdentity(
            observed.OwnerPid,
            observed.OwnerStartedAtUtcTicks,
            observed.OwnerExecutablePath);
        var expectedChild = PersistedIdentity(
            observed.SingBoxPid,
            observed.SingBoxStartedAtUtcTicks,
            observed.SingBoxExecutablePath);
        if (expectedOwner is null || expectedChild is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]CLI state has incomplete process identity; refusing every destructive action.[/]");
            return 1;
        }

        var ownerResult = TrySignalOwnerAndWait(expectedOwner.Value, generation);
        if (ownerResult == OwnerStopResult.TimedOut)
        {
            AnsiConsole.MarkupLine(
                "[red]Timed out waiting for the exact CLI owner; state and child were preserved.[/]");
            return 1;
        }

        if (ownerResult == OwnerStopResult.Unavailable)
        {
            var ownerState = ProbeExactProcess(expectedOwner.Value);
            if (ownerState != ExactProcessState.DeadOrReplaced)
            {
                AnsiConsole.MarkupLine(ownerState == ExactProcessState.Alive
                    ? "[yellow]The exact CLI owner is alive but its stop capability is unavailable; refusing fallback kill.[/]"
                    : "[yellow]The CLI owner identity could not be verified; refusing fallback kill.[/]");
                return 1;
            }
        }

        RunState target = observed;
        try
        {
            var latest = StateFile.Read();
            if (latest is null && ownerResult == OwnerStopResult.Exited)
            {
                AnsiConsole.MarkupLine("[green]VPN Router stopped.[/]");
                return 0;
            }

            if (latest is not null)
            {
                if (latest.RunGeneration != generation)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]A newer CLI run replaced the observed generation; it was not touched.[/]");
                    return 1;
                }

                target = latest;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not re-read CLI run state");
            AnsiConsole.MarkupLine("[red]Could not re-read CLI run state.[/]");
            return 1;
        }

        expectedChild = PersistedIdentity(
            target.SingBoxPid,
            target.SingBoxStartedAtUtcTicks,
            target.SingBoxExecutablePath);
        if (expectedChild is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]The observed generation lost its exact child identity; refusing fallback kill and cleanup.[/]");
            return 1;
        }

        // state.json is user-readable status, not the destructive authority.
        // The TUN owner's v2 durable record must independently bind this exact
        // owner and child before fallback may open the process for termination.
        if (!ProcessOwnership.IsCurrentRuntimeOwnerPair(
                expectedOwner.Value,
                expectedChild.Value))
        {
            AnsiConsole.MarkupLine(
                "[yellow]CLI state no longer matches the runtime owner record; refusing fallback kill.[/]");
            return 1;
        }

        var childResult = TryStopExactChild(expectedChild.Value);
        if (childResult == ChildStopResult.Refused)
            return 1;
        if (childResult == ChildStopResult.AlreadyGone)
        {
            AnsiConsole.MarkupLine(
                "[yellow]The persisted child is gone but state remains; refusing to assume no later child exists.[/]");
            return 1;
        }

        try
        {
            if (!StateFile.ClearIfGeneration(generation))
            {
                AnsiConsole.MarkupLine(
                    "[yellow]A newer CLI run now owns state; its state was preserved.[/]");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not clear exact CLI run state");
            AnsiConsole.MarkupLine("[red]Could not clear the exact CLI run state.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]VPN Router stopped.[/]");
        return 0;
    }

    internal static string BuildStopEventName(int ownerPid, Guid generation)
    {
        if (ownerPid <= 0) throw new ArgumentOutOfRangeException(nameof(ownerPid));
        if (generation == Guid.Empty) throw new ArgumentException("Run generation is required.", nameof(generation));
        return $"{StopEventPrefix}{ownerPid}_{generation:N}";
    }

    private static OwnedProcessIdentity? PersistedIdentity(
        int pid,
        long startedAtUtcTicks,
        string executablePath)
        => pid > 0 && startedAtUtcTicks > 0 && !string.IsNullOrWhiteSpace(executablePath)
            ? new OwnedProcessIdentity(pid, startedAtUtcTicks, executablePath)
            : null;

    private static OwnerStopResult TrySignalOwnerAndWait(
        OwnedProcessIdentity expectedOwner,
        Guid generation)
    {
        try
        {
            using var ownerProcess = Process.GetProcessById(expectedOwner.Pid);
            var pinnedOwnerHandle = ownerProcess.SafeHandle;
            if (pinnedOwnerHandle.IsInvalid || pinnedOwnerHandle.IsClosed)
                return OwnerStopResult.Unavailable;

            var currentOwner = ProcessOwnership.TryReadProcessIdentity(ownerProcess);
            if (currentOwner is not { } current
                || !ProcessOwnership.IsSameProcessIdentity(expectedOwner, current))
                return OwnerStopResult.Unavailable;

            if (!EventWaitHandle.TryOpenExisting(
                    BuildStopEventName(expectedOwner.Pid, generation),
                    out var ownerEvent))
                return OwnerStopResult.Unavailable;

            using (ownerEvent)
                ownerEvent.Set();

            var exited = ownerProcess.WaitForExit(5000);
            GC.KeepAlive(pinnedOwnerHandle);
            return exited ? OwnerStopResult.Exited : OwnerStopResult.TimedOut;
        }
        catch (ArgumentException)
        {
            return OwnerStopResult.Unavailable;
        }
        catch (InvalidOperationException)
        {
            return OwnerStopResult.Unavailable;
        }
        catch
        {
            return OwnerStopResult.Unavailable;
        }
    }

    private static ExactProcessState ProbeExactProcess(OwnedProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            var pinnedHandle = process.SafeHandle;
            ExactProcessState result;
            if (pinnedHandle.IsInvalid || pinnedHandle.IsClosed)
            {
                result = process.HasExited
                    ? ExactProcessState.DeadOrReplaced
                    : ExactProcessState.Unknown;
            }
            else
            {
                var current = ProcessOwnership.TryReadProcessIdentity(process);
                result = current is { } value
                    ? ProcessOwnership.IsSameProcessIdentity(expected, value)
                        ? ExactProcessState.Alive
                        : ExactProcessState.DeadOrReplaced
                    : process.HasExited
                        ? ExactProcessState.DeadOrReplaced
                        : ExactProcessState.Unknown;
            }

            GC.KeepAlive(pinnedHandle);
            return result;
        }
        catch (ArgumentException)
        {
            return ExactProcessState.DeadOrReplaced;
        }
        catch (InvalidOperationException)
        {
            return ExactProcessState.DeadOrReplaced;
        }
        catch
        {
            return ExactProcessState.Unknown;
        }
    }

    private static ChildStopResult TryStopExactChild(OwnedProcessIdentity expectedChild)
    {
        try
        {
            using var process = Process.GetProcessById(expectedChild.Pid);
            var pinnedWindowsHandle = process.SafeHandle;
            if (pinnedWindowsHandle.IsInvalid || pinnedWindowsHandle.IsClosed)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Could not pin PID {expectedChild.Pid}; refusing to kill.[/]");
                return ChildStopResult.Refused;
            }

            var currentChild = ProcessOwnership.TryReadOwnedSingBoxIdentity(process);
            if (currentChild is not { } current)
            {
                if (process.HasExited)
                    return ChildStopResult.AlreadyGone;

                AnsiConsole.MarkupLine(
                    $"[yellow]PID {expectedChild.Pid} is not a VPNRouter-owned sing-box process; refusing to kill.[/]");
                return ChildStopResult.Refused;
            }

            if (!ProcessOwnership.IsSameProcessIdentity(expectedChild, current))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]PID {expectedChild.Pid} no longer matches the persisted child identity; refusing to kill.[/]");
                return ChildStopResult.Refused;
            }

            process.Kill(entireProcessTree: true);
            var exited = process.WaitForExit(5000);
            GC.KeepAlive(pinnedWindowsHandle);
            if (!exited)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Timed out waiting for sing-box PID {expectedChild.Pid} to stop.[/]");
                return ChildStopResult.Refused;
            }

            AnsiConsole.MarkupLine($"[green]sing-box stopped (was PID {expectedChild.Pid})[/]");
            return ChildStopResult.Stopped;
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine($"[grey]sing-box (PID {expectedChild.Pid}) already stopped[/]");
            return ChildStopResult.AlreadyGone;
        }
        catch (InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[grey]sing-box (PID {expectedChild.Pid}) already stopped[/]");
            return ChildStopResult.AlreadyGone;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not stop exact sing-box child");
            AnsiConsole.MarkupLine("[red]Could not stop the exact sing-box child.[/]");
            return ChildStopResult.Refused;
        }
    }
}
