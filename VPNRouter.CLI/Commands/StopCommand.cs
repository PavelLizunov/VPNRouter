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
        // (guards against OS PID reuse).
        if (state.SingBoxPid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(state.SingBoxPid);
                if (!ProcessOwnership.IsOwnedSingBox(proc))
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

        // Legacy fallback: kill the sing-box child directly.
        if (state.SingBoxPid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(state.SingBoxPid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
                AnsiConsole.MarkupLine($"[green]✓[/] sing-box stopped (was PID {state.SingBoxPid})");
            }
            catch (ArgumentException)
            {
                AnsiConsole.MarkupLine($"[grey]sing-box (PID {state.SingBoxPid}) already stopped[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Error stopping sing-box:[/] {ex.Message}");
            }
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
