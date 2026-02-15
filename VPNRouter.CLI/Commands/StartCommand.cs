using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public class StartSettings : CommandSettings
{
    [CommandOption("-p|--profile <PROFILE>")]
    [Description("Profile name(s) to activate. Comma-separated to merge. Example: Gaming_Full or \"Discord_Privacy,Work_Suite\"")]
    [DefaultValue("")]
    public string Profile { get; set; } = string.Empty;

    [CommandOption("-c|--config <PATH>")]
    [Description("Path to config.yaml (default: %ProgramData%\\VPNRouter\\config.yaml)")]
    public string? ConfigPath { get; set; }

    [CommandOption("--dry-run")]
    [Description("Generate config and validate without starting sing-box")]
    public bool DryRun { get; set; }
}

public class StartCommand : AsyncCommand<StartSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, StartSettings settings)
    {
        AnsiConsole.Write(new FigletText("VPNRouter").Color(Color.Cyan1));

        // 1. Load app settings
        AppSettings appSettings;
        try
        {
            appSettings = SettingsLoader.Load(settings.ConfigPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Failed to load config:[/] {ex.Message}");
            return 1;
        }

        // 2. Validate VLESS config (supports both legacy single-server and multi-server)
        var effectiveServers = appSettings.Vless.GetEffectiveServers();
        if (effectiveServers.Count == 0 ||
            effectiveServers.Any(s => string.IsNullOrWhiteSpace(s.Server) || s.Server == "your.server.com"))
        {
            AnsiConsole.MarkupLine("[red]✗ VLESS server not configured.[/]");
            AnsiConsole.MarkupLine("[yellow]  Edit:[/] %ProgramData%\\VPNRouter\\config.yaml");
            AnsiConsole.MarkupLine("[yellow]  Set:[/] vless.servers or vless.server + vless.uuid");
            return 1;
        }

        // 3. Check admin rights
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required for TUN interface.[/]");
            AnsiConsole.MarkupLine("[yellow]  Run as Administrator or via Windows Service.[/]");
            return 1;
        }

        // 4. Load profiles
        var profileName = string.IsNullOrEmpty(settings.Profile)
            ? appSettings.ActiveProfile
            : settings.Profile;

        if (string.IsNullOrEmpty(profileName))
        {
            AnsiConsole.MarkupLine("[red]✗ No profile specified.[/]");
            AnsiConsole.MarkupLine("[yellow]  Use:[/] vpnrouter start --profile Gaming_Full");
            AnsiConsole.MarkupLine("[yellow]  Or set:[/] active_profile in config.yaml");
            return 1;
        }

        var sources = ProfileSourceFactory.Create(appSettings);
        var manager = new ProfileManager(sources);
        ProfileCollection profiles = null!;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading profiles...", async ctx =>
            {
                profiles = await manager.LoadAsync();
                ctx.Status = $"Loaded {profiles.Profiles.Count} profiles";
            });

        // 5. Resolve profile (single or merged)
        VPNRouter.Core.Models.Profile activeProfile;
        try
        {
            var names = profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            activeProfile = names.Length == 1
                ? manager.GetProfile(names[0])
                : manager.MergeProfiles(names);
        }
        catch (KeyNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Profile: [cyan]{activeProfile.Name}[/] — {activeProfile.Description}");
        AnsiConsole.MarkupLine($"  Process rules: [yellow]{activeProfile.Processes.Count}[/]");
        AnsiConsole.MarkupLine($"  DNS mode: [yellow]{activeProfile.DnsMode}[/]");
        AnsiConsole.MarkupLine($"  Block on VPN fail: [yellow]{activeProfile.BlockOnVpnFail}[/]");

        // 6. Scan processes
        var scanner = new ProcessScanner();
        ScanResult scanResult = null!;

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning processes...", ctx =>
            {
                scanResult = scanner.ScanForProfile(activeProfile);
                ctx.Status($"Found {scanResult.ProcessNames.Count} process names");
            });

        AnsiConsole.MarkupLine($"[green]✓[/] Resolved [cyan]{scanResult.ProcessNames.Count}[/] process names");

        // 7. Generate sing-box config
        var sbConfig = ConfigGenerator.Generate(activeProfile, scanResult.ProcessNames, appSettings);

        // 8. Validate (leak protection)
        var validation = LeakProtection.ValidateConfig(sbConfig);

        if (validation.Warnings.Count > 0)
        {
            foreach (var w in validation.Warnings)
                AnsiConsole.MarkupLine($"[yellow]⚠ {w}[/]");
        }

        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine("[red]✗ Config validation failed:[/]");
            foreach (var e in validation.Errors)
                AnsiConsole.MarkupLine($"  [red]• {e}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]✓[/] Config validated (no leaks detected)");

        if (settings.DryRun)
        {
            // Write config to disk so it can be inspected and validated with sing-box check
            var dryRunConfigDir = System.Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
            System.IO.Directory.CreateDirectory(dryRunConfigDir);
            var dryRunConfigPath = System.IO.Path.Combine(dryRunConfigDir, "current.json");
            System.IO.File.WriteAllText(dryRunConfigPath, ConfigGenerator.Serialize(sbConfig));
            AnsiConsole.MarkupLine($"[green]✔[/] Config written to: [grey]{dryRunConfigPath}[/]");
            AnsiConsole.MarkupLine("[cyan]Dry run complete — sing-box not started.[/]");
            return 0;
        }

        // 9. Check sing-box binary
        var exePath = Environment.ExpandEnvironmentVariables(appSettings.SingBox.ExecutablePath);
        if (!File.Exists(exePath))
        {
            AnsiConsole.MarkupLine($"[red]✗ sing-box not found at:[/] {exePath}");
            if (appSettings.SingBox.AutoDownload)
                AnsiConsole.MarkupLine("[yellow]  Run:[/] vpnrouter singbox download");
            return 1;
        }

        // 10. Setup Firewall rules (block_on_vpn_fail)
        using var firewall = new FirewallManager();
        if (activeProfile.BlockOnVpnFail)
        {
            var blockProcesses = scanResult.ProcessNames;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Creating firewall block rules...", _ =>
                {
                    firewall.CreateBlockRules(blockProcesses);
                });
            AnsiConsole.MarkupLine($"[green]✓[/] Firewall block rules created [grey](disabled until VPN up)[/]");
        }

        // 11. Start sing-box
        var singBox = new SingBoxManager(appSettings.SingBox);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Starting sing-box...", _ =>
            {
                singBox.Start(sbConfig);
                System.Threading.Thread.Sleep(1500); // brief wait for startup
            });

        if (!singBox.IsRunning())
        {
            AnsiConsole.MarkupLine("[red]✗ sing-box failed to start. Check logs:[/]");
            AnsiConsole.MarkupLine($"  [grey]%ProgramData%\\VPNRouter\\logs\\singbox.log[/]");
            firewall.DeleteAllRules();
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] sing-box started [grey](PID: {singBox.Pid})[/]");

        // Enable firewall block rules now that VPN is up
        if (activeProfile.BlockOnVpnFail)
        {
            firewall.EnableBlockRules();
            AnsiConsole.MarkupLine($"[green]✓[/] Firewall block rules [bold]enabled[/] — leak protection active");
        }

        // 12. Start ETW process monitor
        using var etw = new EtwProcessMonitor();
        var healthMonitor = new HealthMonitor(
            singBox, scanner, firewall,
            appSettings.Monitoring);

        etw.ProcessStarted += (_, e) =>
        {
            // Check if new process matches any profile pattern
            var isTargeted = activeProfile.Processes
                .Any(rule => rule.ScanPatterns
                    .Any(p => ProcessScanner.MatchesPattern(e.ProcessName + ".exe", p)));

            if (isTargeted)
            {
                AnsiConsole.MarkupLine($"[grey]  → new process: {e.ProcessName} (PID {e.ProcessId})[/]");
                healthMonitor.OnNewProcessDetected(e.ProcessName);
            }
        };

        etw.Start();
        healthMonitor.Start(activeProfile, appSettings, scanResult);

        healthMonitor.RestartAttempted += (_, attempt) =>
            AnsiConsole.MarkupLine($"[yellow]⚠ sing-box restarting (attempt {attempt}/{appSettings.Monitoring.MaxRestartAttempts})[/]");

        AnsiConsole.MarkupLine($"\n[bold green]VPN Router is running.[/]");
        AnsiConsole.MarkupLine($"[grey]Profile:[/] [cyan]{activeProfile.Name}[/]  [grey]|[/]  [grey]Processes:[/] [cyan]{scanResult.ProcessNames.Count}[/]  [grey]|[/]  [grey]ETW:[/] [cyan]active[/]");
        AnsiConsole.MarkupLine($"[grey]Press Ctrl+C to stop.[/]\n");

        // Save state for status command
        StateFile.Write(new RunState
        {
            ActiveProfile = activeProfile.Name,
            SingBoxPid = singBox.Pid ?? 0,
            StartedAt = DateTime.Now,
            ProcessNames = scanResult.ProcessNames
        });

        // 13. Block and handle Ctrl+C / crash
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            firewall.DeleteAllRules();
            singBox.Stop();
            StateFile.Clear();
        };

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        // 14. Graceful shutdown
        AnsiConsole.MarkupLine("\n[yellow]Stopping...[/]");
        healthMonitor.Stop();
        etw.Stop();

        if (activeProfile.BlockOnVpnFail)
        {
            firewall.DisableBlockRules();
            firewall.DeleteAllRules();
            AnsiConsole.MarkupLine("[green]✓[/] Firewall rules removed");
        }

        singBox.Stop();
        StateFile.Clear();
        AnsiConsole.MarkupLine("[green]✓[/] Stopped.");

        return 0;
    }
}
