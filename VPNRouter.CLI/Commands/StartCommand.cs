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

        // 2. Override profile from CLI if specified
        if (!string.IsNullOrEmpty(settings.Profile))
            appSettings.ActiveProfile = settings.Profile;

        if (string.IsNullOrEmpty(appSettings.ActiveProfile))
        {
            AnsiConsole.MarkupLine("[red]✗ No profile specified.[/]");
            AnsiConsole.MarkupLine("[yellow]  Use:[/] vpnrouter start --profile Gaming_Full");
            AnsiConsole.MarkupLine("[yellow]  Or set:[/] active_profile in config.yaml");
            return 1;
        }

        // 3. Check admin rights
        if (!AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required for TUN interface.[/]");
            AnsiConsole.MarkupLine("[yellow]  Run as Administrator or via Windows Service.[/]");
            return 1;
        }

        // 4. Dry-run: generate config, validate, write to disk, exit
        if (settings.DryRun)
        {
            return await DryRunAsync(appSettings);
        }

        // 5. Start VPN via engine
        using var engine = new VpnEngine(
            new ProcessScanner(Serilog.Log.Logger),
            () => new FirewallManager(Serilog.Log.Logger),
            () => new EtwProcessMonitor(Serilog.Log.Logger),
            Serilog.Log.Logger);

        engine.StatusChanged += msg =>
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(msg)}");

        engine.ProcessDetected += (name, pid) =>
            AnsiConsole.MarkupLine($"[grey]  → new process: {name} (PID {pid})[/]");

        engine.RestartAttempted += (attempt, max) =>
            AnsiConsole.MarkupLine($"[yellow]⚠ sing-box restarting (attempt {attempt}/{max})[/]");

        engine.Warning += msg =>
            AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(msg)}[/]");

        try
        {
            await engine.StartAsync(appSettings);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Save state for status command
        StateFile.Write(new RunState
        {
            ActiveProfile = engine.ActiveProfileName,
            SingBoxPid = engine.SingBoxPid ?? 0,
            StartedAt = DateTime.Now,
            ProcessNames = engine.MonitoredProcesses
        });

        AnsiConsole.MarkupLine($"\n[bold green]VPN Router is running.[/]");
        AnsiConsole.MarkupLine($"[grey]Profile:[/] [cyan]{engine.ActiveProfileName}[/]  [grey]|[/]  [grey]Processes:[/] [cyan]{engine.MonitoredProcesses.Count}[/]  [grey]|[/]  [grey]ETW:[/] [cyan]active[/]");
        AnsiConsole.MarkupLine($"[grey]Press Ctrl+C to stop.[/]\n");

        // 6. Block and handle Ctrl+C
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        // 7. Graceful shutdown
        AnsiConsole.MarkupLine("\n[yellow]Stopping...[/]");
        engine.Stop();
        StateFile.Clear();
        AnsiConsole.MarkupLine("[green]✓[/] Stopped.");

        return 0;
    }

    private static async Task<int> DryRunAsync(AppSettings settings)
    {
        try
        {
            // Load profiles & resolve
            var sources = BuildDryRunSources(settings);
            var manager = new ProfileManager(sources, Serilog.Log.Logger);
            var collection = await manager.LoadAsync();

            var isCustomMode = (settings.App.ConfigMode ?? "generated")
                .Equals("custom", StringComparison.OrdinalIgnoreCase);

            Core.Models.Profile profile;
            var profileNames = (settings.ActiveProfile ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (profileNames.Length > 0)
            {
                profile = profileNames.Length == 1
                    ? manager.GetProfile(profileNames[0])
                    : manager.MergeProfiles(profileNames);
            }
            else if (isCustomMode)
            {
                profile = new Core.Models.Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ No profile specified.[/]");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]✓[/] Profile: [cyan]{Markup.Escape(profile.Name)}[/] — {Markup.Escape(profile.Description)}");
            AnsiConsole.MarkupLine($"  Process rules: [yellow]{profile.Processes.Count}[/]");
            AnsiConsole.MarkupLine($"  DNS mode: [yellow]{profile.DnsMode}[/]");

            // Scan & generate
            var scanner = new ProcessScanner(Serilog.Log.Logger);
            var scan = scanner.ScanForProfile(profile);
            AnsiConsole.MarkupLine($"[green]✓[/] Resolved [cyan]{scan.ProcessNames.Count}[/] process names");

            string configJson;
            if (isCustomMode)
            {
                var customPath = Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
                if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                {
                    AnsiConsole.MarkupLine($"[red]✗ Custom config not found: {customPath}[/]");
                    return 1;
                }

                var rawJson = File.ReadAllText(customPath);
                var (isValid, customErrors) = CustomConfigInjector.Validate(rawJson);
                if (!isValid)
                {
                    AnsiConsole.MarkupLine("[red]✗ Custom config validation failed:[/]");
                    foreach (var e in customErrors)
                        AnsiConsole.MarkupLine($"  [red]• {e}[/]");
                    return 1;
                }

                configJson = CustomConfigInjector.Inject(rawJson, scan.ProcessNames, settings);
                AnsiConsole.MarkupLine("[green]✓[/] Custom config injected with process routing");
            }
            else
            {
                var sbConfig = ConfigGenerator.Generate(profile, scan.ProcessNames, settings);
                var validation = LeakProtection.ValidateConfig(sbConfig);

                foreach (var w in validation.Warnings)
                    AnsiConsole.MarkupLine($"[yellow]⚠ {w}[/]");

                if (!validation.IsValid)
                {
                    AnsiConsole.MarkupLine("[red]✗ Config validation failed:[/]");
                    foreach (var e in validation.Errors)
                        AnsiConsole.MarkupLine($"  [red]• {e}[/]");
                    return 1;
                }

                configJson = ConfigGenerator.Serialize(sbConfig);
            }

            // Write config
            var configDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "current.json");
            File.WriteAllText(configPath, configJson);

            AnsiConsole.MarkupLine($"[green]✔[/] Config written to: [grey]{configPath}[/]");
            AnsiConsole.MarkupLine("[cyan]Dry run complete — sing-box not started.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static List<Core.Interfaces.IProfileSource> BuildDryRunSources(AppSettings settings)
    {
        var sources = new List<Core.Interfaces.IProfileSource>();
        int priority = 10;

        foreach (var src in settings.ProfileSources)
        {
            switch (src.Type?.ToLowerInvariant())
            {
                case "github" when !string.IsNullOrEmpty(src.Url):
                    sources.Add(new GitHubProfileSource(src.Url, priority));
                    break;
                case "local" when !string.IsNullOrEmpty(src.Path):
                    sources.Add(new LocalProfileSource(src.Path, priority + 10));
                    break;
            }
            priority += 10;
        }

        var appDir = AppContext.BaseDirectory;
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 80));

        sources.Add(new BuiltInProfileSource());
        return sources;
    }
}
