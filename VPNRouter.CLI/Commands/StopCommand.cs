using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace VPNRouter.CLI.Commands;

public class StopCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var state = StateFile.Read();
        if (state == null)
        {
            AnsiConsole.MarkupLine("[yellow]VPN Router is not running.[/]");
            return 0;
        }

        // Kill sing-box process if still alive
        if (state.SingBoxPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.SingBoxPid);
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
}
