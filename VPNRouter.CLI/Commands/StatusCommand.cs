using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class StatusCommand : Command
{
    public override int Execute(CommandContext context)
    {
        // Always perform the side-effect-free runtime probe. It reads
        // config.yaml directly on every invocation, even when CLI state is
        // absent, so GUI/Service ownership is observable without allowing the
        // configured path to mutate the durable runtime-owner record.
        var runtime = RuntimeStatusDetector.GetVpnRuntime();
        var state = StateFile.Read();

        var rule = new Rule("[cyan]VPN Router Status[/]");
        AnsiConsole.Write(rule);

        var stateWrittenAtUtc = GetStateWrittenAtUtc();
        var stateIsExact = state is not null
                           && RuntimeStatusDetector.PersistedCliStateMatches(
                               state.SingBoxPid,
                               stateWrittenAtUtc);
        var detailedRunning = stateIsExact
                              && runtime is not null
                              && state!.SingBoxPid == runtime.Pid;
        var detailedCrashed = stateIsExact
                              && runtime is null
                              && state is not null
                              && !RuntimeStatusDetector.IsPersistedChildAlive(state.SingBoxPid);

        if (!detailedRunning && !detailedCrashed)
        {
            if (runtime is not null)
            {
                var ownerTable = new Table().NoBorder().HideHeaders();
                ownerTable.AddColumn("");
                ownerTable.AddColumn("");
                ownerTable.AddRow("[grey]Status:[/]", "[bold green]Running[/]");
                ownerTable.AddRow("[grey]Owner:[/]", "[cyan]VPNRouter GUI or Service[/]");
                AnsiConsole.Write(ownerTable);
            }
            else
            {
                AnsiConsole.MarkupLine("[bold red]Status:[/]          Stopped");
            }

            AnsiConsole.Write(new Rule());
            return 0;
        }

        var exactState = state!;
        var isRunning = detailedRunning;
        ProcessMetrics? metrics = null;

        if (isRunning && runtime is not null)
        {
            try
            {
                using var process = Process.GetProcessById(runtime.Pid);
                if (!process.HasExited
                    && process.StartTime.ToUniversalTime().Ticks
                       == runtime.StartedAt.ToUniversalTime().Ticks)
                {
                    process.Refresh();
                    metrics = new ProcessMetrics
                    {
                        MemoryMb = process.WorkingSet64 / 1024 / 1024,
                        CpuTime = process.TotalProcessorTime,
                        StartTime = process.StartTime
                    };
                }
            }
            catch
            {
                // Process exited between the exact identity probe and metrics.
            }
        }

        var uptime = isRunning
            ? FormatUptime(DateTime.Now - exactState.StartedAt)
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
        table.AddRow("[grey]Active Profile:[/]",   $"[cyan]{Markup.Escape(exactState.ActiveProfile)}[/]");
        table.AddRow("[grey]sing-box PID:[/]",     isRunning ? exactState.SingBoxPid.ToString() : "[red]dead[/]");
        table.AddRow("[grey]Started at:[/]",       exactState.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Monitored processes
        if (exactState.ProcessNames.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Monitored Processes[/] ({exactState.ProcessNames.Count}):");
            foreach (var name in exactState.ProcessNames.Take(20))
            {
                AnsiConsole.MarkupLine($"  [green]✓[/] [grey]{Markup.Escape(name)}[/]");
            }
            if (exactState.ProcessNames.Count > 20)
                AnsiConsole.MarkupLine($"  [grey]... and {exactState.ProcessNames.Count - 20} more[/]");
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

    private static DateTime GetStateWrittenAtUtc()
    {
        try
        {
            return File.GetLastWriteTimeUtc(VPNRouter.Core.AppPaths.StatePath);
        }
        catch
        {
            return default;
        }
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
