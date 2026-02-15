using Spectre.Console;
using Spectre.Console.Cli;
using System.ServiceProcess;

namespace VPNRouter.CLI.Commands;

// ─── service install ──────────────────────────────────────────────────────────

public class ServiceInstallSettings : CommandSettings
{
    [CommandOption("--exe <PATH>")]
    [System.ComponentModel.Description(
        "Path to VPNRouter.Service.exe. Defaults to VPNRouter.Service.exe in the same directory.")]
    public string? ExePath { get; set; }
}

public class ServiceInstallCommand : Command<ServiceInstallSettings>
{
    public override int Execute(CommandContext context, ServiceInstallSettings settings)
    {
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required to install a Windows Service.[/]");
            return 1;
        }

        // Locate the service binary
        var exePath = settings.ExePath;
        if (string.IsNullOrEmpty(exePath))
        {
            var dir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(dir, "VPNRouter.Service.exe"),
                Path.Combine(dir, "..", "VPNRouter.Service", "VPNRouter.Service.exe"),
                Path.Combine(dir, "..", "service", "VPNRouter.Service.exe")
            };

            exePath = candidates.FirstOrDefault(File.Exists);

            if (exePath == null)
            {
                AnsiConsole.MarkupLine("[red]✗ Cannot find VPNRouter.Service.exe[/]");
                AnsiConsole.MarkupLine("[yellow]  Use:[/] vpnrouter service install --exe <path>");
                return 1;
            }

            exePath = Path.GetFullPath(exePath);
        }

        AnsiConsole.MarkupLine($"[grey]Service binary:[/] {exePath}");

        var result = VPNRouter.Service.ServiceInstaller.Install(exePath);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {result.Message}");
            AnsiConsole.MarkupLine("[grey]Start with:[/] vpnrouter service start");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] {result.Message}");
        }

        return result.Success ? 0 : 1;
    }
}

// ─── service uninstall ────────────────────────────────────────────────────────

public class ServiceUninstallCommand : Command
{
    public override int Execute(CommandContext context)
    {
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required.[/]");
            return 1;
        }

        var result = VPNRouter.Service.ServiceInstaller.Uninstall();

        if (result.Success)
            AnsiConsole.MarkupLine($"[green]✓[/] {result.Message}");
        else
            AnsiConsole.MarkupLine($"[red]✗[/] {result.Message}");

        return result.Success ? 0 : 1;
    }
}

// ─── service start ────────────────────────────────────────────────────────────

public class ServiceStartCommand : Command
{
    public override int Execute(CommandContext context)
    {
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required.[/]");
            return 1;
        }

        var result = VPNRouter.Service.ServiceInstaller.Start();

        if (result.Success)
            AnsiConsole.MarkupLine($"[green]✓[/] {result.Message}");
        else
            AnsiConsole.MarkupLine($"[red]✗[/] {result.Message}");

        return result.Success ? 0 : 1;
    }
}

// ─── service stop ─────────────────────────────────────────────────────────────

public class ServiceStopCommand : Command
{
    public override int Execute(CommandContext context)
    {
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required.[/]");
            return 1;
        }

        var result = VPNRouter.Service.ServiceInstaller.Stop();

        if (result.Success)
            AnsiConsole.MarkupLine($"[green]✓[/] {result.Message}");
        else
            AnsiConsole.MarkupLine($"[red]✗[/] {result.Message}");

        return result.Success ? 0 : 1;
    }
}

// ─── service status ───────────────────────────────────────────────────────────

public class ServiceStatusCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var isInstalled = VPNRouter.Service.ServiceInstaller.IsInstalled();

        var rule = new Rule("[cyan]Windows Service Status[/]");
        AnsiConsole.Write(rule);

        if (!isInstalled)
        {
            AnsiConsole.MarkupLine("[bold red]Not installed[/]");
            AnsiConsole.MarkupLine($"[grey]Service name:[/] {VPNRouter.Service.ServiceInstaller.ServiceName}");
            AnsiConsole.MarkupLine("[grey]Install with:[/] vpnrouter service install");
            AnsiConsole.Write(new Rule());
            return 0;
        }

        var status = VPNRouter.Service.ServiceInstaller.GetStatus();

        var statusColor = status switch
        {
            ServiceControllerStatus.Running  => "green",
            ServiceControllerStatus.Stopped  => "red",
            ServiceControllerStatus.Paused   => "yellow",
            _                                => "grey"
        };

        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("");
        table.AddColumn("");

        table.AddRow("[grey]Service name:[/]",  VPNRouter.Service.ServiceInstaller.ServiceName);
        table.AddRow("[grey]Display name:[/]",  VPNRouter.Service.ServiceInstaller.DisplayName);
        table.AddRow("[grey]Status:[/]",        $"[bold {statusColor}]{status}[/]");
        table.AddRow("[grey]Installed:[/]",     "[green]Yes[/]");
        table.AddRow("[grey]Startup type:[/]",  "[grey]Automatic (Delayed)[/]");

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Logs:[/]  %ProgramData%\\VPNRouter\\logs\\vpnrouter.log");
        AnsiConsole.MarkupLine("[grey]Events:[/] Event Viewer → Windows Logs → Application → Source: VPNRouter");

        AnsiConsole.Write(new Rule());
        return 0;
    }
}
