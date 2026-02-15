using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class StatusCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var state = StateFile.Read();

        var rule = new Rule("[cyan]VPN Router Status[/]");
        AnsiConsole.Write(rule);

        if (state == null)
        {
            AnsiConsole.MarkupLine("[bold red]Status:[/]          Stopped");
            AnsiConsole.Write(new Rule());
            return 0;
        }

        // Check if sing-box is actually running
        bool isRunning = false;
        ProcessMetrics? metrics = null;

        if (state.SingBoxPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.SingBoxPid);
                isRunning = !proc.HasExited;

                if (isRunning)
                {
                    proc.Refresh();
                    metrics = new ProcessMetrics
                    {
                        MemoryMb = proc.WorkingSet64 / 1024 / 1024,
                        CpuTime = proc.TotalProcessorTime,
                        StartTime = proc.StartTime
                    };
                }
            }
            catch (ArgumentException) { /* process not found */ }
        }

        var uptime = isRunning
            ? FormatUptime(DateTime.Now - state.StartedAt)
            : "—";

        var statusText = isRunning
            ? "[bold green]Running[/]"
            : "[bold red]Crashed[/]";

        // Main status table
        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("");
        table.AddColumn("");

        table.AddRow("[grey]Status:[/]",          statusText);
        table.AddRow("[grey]Uptime:[/]",           uptime);
        table.AddRow("[grey]Active Profile:[/]",   $"[cyan]{state.ActiveProfile}[/]");
        table.AddRow("[grey]sing-box PID:[/]",     isRunning ? state.SingBoxPid.ToString() : "[red]dead[/]");
        table.AddRow("[grey]Started at:[/]",       state.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Monitored processes
        if (state.ProcessNames.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Monitored Processes[/] ({state.ProcessNames.Count}):");
            foreach (var name in state.ProcessNames.Take(20))
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] [grey]{name}[/]");
            }
            if (state.ProcessNames.Count > 20)
                AnsiConsole.MarkupLine($"  [grey]... and {state.ProcessNames.Count - 20} more[/]");
            AnsiConsole.WriteLine();
        }

        // Metrics
        if (metrics != null)
        {
            AnsiConsole.MarkupLine("[bold]Health:[/]");
            AnsiConsole.MarkupLine($"  Memory:     [yellow]{metrics.MemoryMb} MB[/]");
            AnsiConsole.MarkupLine($"  CPU time:   [yellow]{metrics.CpuTime:hh\\:mm\\:ss}[/]");
        }

        // Log paths
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Logs:[/]");
        AnsiConsole.MarkupLine($"  [grey]%ProgramData%\\VPNRouter\\logs\\vpnrouter.log[/]");
        AnsiConsole.MarkupLine($"  [grey]%ProgramData%\\VPNRouter\\logs\\singbox.log[/]");

        AnsiConsole.Write(new Rule());
        return 0;
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}
