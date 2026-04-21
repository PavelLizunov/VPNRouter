using Spectre.Console;
using Spectre.Console.Cli;
using VPNRouter.Core;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

/// <summary>
/// <c>vpnrouter doctor</c> — diagnostic health check. Thin wrapper that
/// calls <see cref="HealthCheck.RunAll"/> and prints the results with
/// Spectre.Console colouring. Exit code 0 if everything is OK, 1 on
/// warnings, 2 on errors.
///
/// Actual check logic lives in <see cref="HealthCheck"/> so the UI
/// "Run Health Check" menu item can reuse it.
/// </summary>
public class DoctorCommand : Command
{
    public override int Execute(CommandContext context)
    {
        AnsiConsole.Write(new Rule("[cyan]VPNRouter Doctor[/]"));
        AnsiConsole.MarkupLine($"Version: [green]{AppVersion.Version}[/]");
        AnsiConsole.MarkupLine($"Data dir: [dim]{AppPaths.DataDir}[/]");
        AnsiConsole.WriteLine();

        var results = HealthCheck.RunAll();
        int warnings = 0, errors = 0;

        foreach (var r in results)
        {
            switch (r.Severity)
            {
                case HealthCheck.Level.Ok:
                    AnsiConsole.MarkupLine($"[green]\u2714 OK[/]    {Markup.Escape(r.Message)}");
                    break;
                case HealthCheck.Level.Warn:
                    AnsiConsole.MarkupLine($"[yellow]\u26A0 WARN[/]  {Markup.Escape(r.Message)}");
                    warnings++;
                    break;
                case HealthCheck.Level.Err:
                    AnsiConsole.MarkupLine($"[red]\u2716 ERR[/]   {Markup.Escape(r.Message)}");
                    errors++;
                    break;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule());
        if (errors == 0 && warnings == 0)
        {
            AnsiConsole.MarkupLine("[bold green]All checks passed.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine(
            $"[bold]Summary:[/] [yellow]{warnings} warning(s)[/], [red]{errors} error(s)[/]");
        return errors > 0 ? 2 : 1;
    }
}
