using Spectre.Console;
using Spectre.Console.Cli;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class CleanupSettings : CommandSettings
{
    [CommandOption("--dry-run")]
    [System.ComponentModel.Description("Print what would be removed and change nothing.")]
    public bool DryRun { get; set; }
}

/// <summary>
/// Removes what VPNRouter leaves in Windows outside its install folder (firewall rules, DNS settings, its driver
/// services, the current user's autostart value). Used by the uninstaller; safe to run any number of times.
/// </summary>
public class CleanupCommand : Command<CleanupSettings>
{
    protected override int Execute(CommandContext context, CleanupSettings settings, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("[yellow]cleanup only applies to Windows.[/]");
            return 0;
        }

        if (!settings.DryRun && !AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required (use --dry-run to only look).[/]");
            return 1;
        }

        if (settings.DryRun && !AdminHelper.IsAdmin())
            AnsiConsole.MarkupLine("[yellow]Not elevated: firewall rules and services may not be listed completely.[/]");

        var cleanup = new SystemCleanup(new ProcessRunner(), new WindowsCleanupRegistry());
        var report = cleanup.Run(settings.DryRun);

        // Plain text on purpose: an uninstaller captures this output, and rule names may contain brackets.
        Console.WriteLine(report.Format());

        return report.HasFailures ? 1 : 0;
    }
}
