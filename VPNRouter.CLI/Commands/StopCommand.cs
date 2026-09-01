using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class StopCommand : Command
{
    // CLI-1: named-event prefix for the stop-request protocol.
    // `start` creates VPNRouter_CLI_Stop_{pid}; `stop` signals it.
    internal const string StopEventPrefix = "VPNRouter_CLI_Stop_";

    public override int Execute(CommandContext context)
    {
        var state = StateFile.Read();
        if (state == null)
        {
            AnsiConsole.MarkupLine("[yellow]VPN Router is not running.[/]");
            return 0;
        }

        // CLI-2: refuse to kill a PID that is not a VPNRouter-owned sing-box
        // (guards against OS PID reuse). Keep the complete identity so the
        // post-wait fallback can prove it is still the same process.
        OwnedProcessIdentity? observedIdentity = null;
        if (state.SingBoxPid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(state.SingBoxPid);
                observedIdentity = ProcessOwnership.TryReadOwnedSingBoxIdentity(proc);
                if (observedIdentity is null && !proc.HasExited)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]PID {state.SingBoxPid} is not a VPNRouter-owned sing-box process — refusing to kill[/]");
                    return 1;
                }
            }
            catch (ArgumentException)
            {
                // Process already gone — fall through to cleanup.
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]✗ Error verifying sing-box identity:[/] {ex.Message}");
                return 1;
            }
        }

        // CLI-1: signal the owner (start) process to shut down gracefully.
        // Windows-only; Linux/macOS fall back to the legacy child-kill.
        if (OperatingSystem.IsWindows() && state.OwnerPid > 0)
        {
            if (TrySignalOwnerAndWait(state.OwnerPid))
            {
                if (StateFile.Read() is not null)
                    StateFile.Clear();
                AnsiConsole.MarkupLine("[green]✓[/] VPN Router stopped.");
                return 0;
            }
        }

        // Legacy fallback: kill the sing-box child directly, but only if the
        // freshly-opened process still has the exact identity observed above.
        if (state.SingBoxPid > 0 && observedIdentity is { } expectedIdentity)
        {
            try
            {
                using var proc = Process.GetProcessById(state.SingBoxPid);
                // On Windows, retaining this native handle prevents the PID
                // from being recycled between identity comparison and Kill.
                var pinnedWindowsHandle = OperatingSystem.IsWindows()
                    ? proc.SafeHandle
                    : null;
                if (pinnedWindowsHandle is { IsInvalid: true } or { IsClosed: true })
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Could not pin PID {state.SingBoxPid} — refusing to kill[/]");
                    return 1;
                }

                var currentIdentity = ProcessOwnership.TryReadOwnedSingBoxIdentity(proc);
                if (currentIdentity is not { } current)
                {
                    if (proc.HasExited)
                    {
                        AnsiConsole.MarkupLine($"[grey]sing-box (PID {state.SingBoxPid}) already stopped[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]PID {state.SingBoxPid} is no longer a VPNRouter-owned sing-box process — refusing to kill[/]");
                        return 1;
                    }
                }
                else
                {
                    if (!ProcessOwnership.IsSameProcessIdentity(expectedIdentity, current))
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]PID {state.SingBoxPid} changed identity while stop was waiting — refusing to kill[/]");
                        return 1;
                    }

                    proc.Kill(entireProcessTree: true);
                    var exited = proc.WaitForExit(5000);
                    GC.KeepAlive(pinnedWindowsHandle);
                    if (!exited)
                    {
                        AnsiConsole.MarkupLine(
                            $"[red]✗ Timed out waiting for sing-box PID {state.SingBoxPid} to stop[/]");
                        return 1;
                    }
                    AnsiConsole.MarkupLine($"[green]✓[/] sing-box stopped (was PID {state.SingBoxPid})");
                }
            }
            catch (ArgumentException)
            {
                AnsiConsole.MarkupLine($"[grey]sing-box (PID {state.SingBoxPid}) already stopped[/]");
            }
            catch (InvalidOperationException)
            {
                AnsiConsole.MarkupLine($"[grey]sing-box (PID {state.SingBoxPid}) already stopped[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error stopping sing-box:[/] {ex.Message}");
                return 1;
            }
        }
        else if (state.SingBoxPid > 0)
        {
            AnsiConsole.MarkupLine($"[grey]sing-box (PID {state.SingBoxPid}) already stopped[/]");
        }

        StateFile.Clear();
        AnsiConsole.MarkupLine("[green]✓[/] VPN Router stopped.");
        return 0;
    }

    private static bool TrySignalOwnerAndWait(int ownerPid)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(
                    StopEventPrefix + ownerPid, out var ownerEvent))
                return false;

            using (ownerEvent)
                ownerEvent.Set();

            try
            {
                using var ownerProc = Process.GetProcessById(ownerPid);
                return ownerProc.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }
}
