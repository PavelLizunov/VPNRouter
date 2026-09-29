using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using VPNRouter.Core;
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
    private readonly ISettingsStore _settingsStore;

    public StartCommand() : this(null) { }

    public StartCommand(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore ?? RealSettingsStore.Instance;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, StartSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new FigletText("VPNRouter").Color(Color.Cyan1));

        AppSettings appSettings;
        try
        {
            appSettings = _settingsStore.Load(settings.ConfigPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Failed to load config:[/] {ex.Message}");
            return 1;
        }

        if (!string.IsNullOrEmpty(settings.Profile))
            appSettings.ActiveProfile = settings.Profile;

        if (string.IsNullOrEmpty(appSettings.ActiveProfile))
        {
            AnsiConsole.MarkupLine("[red]✗ No profile specified.[/]");
            AnsiConsole.MarkupLine("[yellow]  Use:[/] vpnrouter start --profile Gaming_Full");
            AnsiConsole.MarkupLine("[yellow]  Or set:[/] active_profile in config.yaml");
            return 1;
        }

        if (!settings.DryRun && !AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required for TUN interface.[/]");
            AnsiConsole.MarkupLine("[yellow]  Run as Administrator or via Windows Service.[/]");
            AnsiConsole.MarkupLine("[grey]  (--dry-run works without admin — generates + validates config only)[/]");
            return 1;
        }

        var resolved = await SubscriptionResolver.ResolveAsync(
            appSettings,
            refreshFromNetwork: true,
            Serilog.Log.Logger);
        if (resolved > 0)
            AnsiConsole.MarkupLine($"[grey]  → resolved {resolved} server(s) from subscription[/]");

        var isCustomMode = string.Equals(appSettings.App.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase);
        var hasVlessSource = appSettings.Vless.Servers.Count > 0 ||
                             !string.IsNullOrWhiteSpace(appSettings.Vless.Server);
        if (!isCustomMode && !hasVlessSource)
        {
            AnsiConsole.MarkupLine("[red]✗ No VLESS servers configured.[/]");
            if (string.Equals(appSettings.App.ConfigMode, "subscribe", StringComparison.OrdinalIgnoreCase))
                AnsiConsole.MarkupLine("[yellow]  Subscription returned 0 servers. Check the subscription URL or add servers manually.[/]");
            else
                AnsiConsole.MarkupLine("[yellow]  Add a subscription via GUI, or populate vless.servers / vless.server in config.yaml.[/]");
            return 1;
        }

        if (settings.DryRun)
        {
            return await DryRunAsync(appSettings);
        }

        var runGeneration = Guid.NewGuid();
        using var ownerProcess = Process.GetCurrentProcess();
        var ownerIdentity = ProcessOwnership.TryReadProcessIdentity(ownerProcess);
        if (ownerIdentity is not { } owner)
        {
            AnsiConsole.MarkupLine("[red]Could not capture the CLI owner identity.[/]");
            return 1;
        }

        VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!VPNRouter.Core.Services.TunOwnershipLock.IsOwnedByAnyone())
                    VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);
            }
            catch { }
        };

        using var engine = VPNRouter.Core.Platform.PlatformServices
            .CreateVpnEngine(Serilog.Log.Logger);

        engine.StatusChanged += msg =>
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(msg)}");

        engine.ProcessDetected += (name, pid) =>
            AnsiConsole.MarkupLine($"[grey]  → new process: {name} (PID {pid})[/]");

        engine.RestartAttempted += (attempt, max) =>
            AnsiConsole.MarkupLine($"[yellow]⚠ sing-box restarting (attempt {attempt}/{max})[/]");

        engine.Warning += msg =>
            AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(msg)}[/]");

        var childStateGate = new object();
        OwnedProcessIdentity? latestChildIdentity = null;
        var statePublished = false;
        engine.SingBoxStarted += newPid =>
        {
            lock (childStateGate)
            {
                try
                {
                    var child = TryCaptureOwnedChild(newPid);
                    if (child is not { } identity
                        || latestChildIdentity is { } latest
                        && latest.StartedAtUtcTicks > identity.StartedAtUtcTicks)
                        return;

                    latestChildIdentity = identity;
                    if (statePublished)
                        StateFile.TryUpdateChild(runGeneration, identity);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Could not publish restarted sing-box identity");
                }
            }
        };

        try
        {
            await engine.StartAsync(appSettings);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopWait = null;
        EventWaitHandle? legacyStopEvent = null;
        RegisteredWaitHandle? legacyStopWait = null;
        try
        {
            try
            {
                stopEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    StopCommand.BuildStopEventName(owner.Pid, runGeneration),
                    StopCommand.StopEventOptions,
                    out var stopEventCreated);
                if (!stopEventCreated)
                    throw new InvalidOperationException("Generation stop event already exists.");
                stopWait = ThreadPool.RegisterWaitForSingleObject(
                    stopEvent,
                    (_, _) => cts.Cancel(),
                    state: null,
                    timeout: Timeout.InfiniteTimeSpan,
                    executeOnlyOnce: true);

                legacyStopEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    StopCommand.StopEventPrefix + owner.Pid,
                    StopCommand.StopEventOptions,
                    out var legacyStopEventCreated);
                if (!legacyStopEventCreated)
                    throw new InvalidOperationException("Legacy stop event already exists.");
                legacyStopWait = ThreadPool.RegisterWaitForSingleObject(
                    legacyStopEvent,
                    (_, _) => cts.Cancel(),
                    state: null,
                    timeout: Timeout.InfiniteTimeSpan,
                    executeOnlyOnce: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not create CLI stop capabilities");
                AnsiConsole.MarkupLine("[red]Could not create the CLI stop capability.[/]");
                return 1;
            }

            lock (childStateGate)
            {
                var currentChild = TryCaptureOwnedChild(engine.SingBoxPid ?? 0);
                if (currentChild is { } current
                    && (latestChildIdentity is not { } latest
                        || current.StartedAtUtcTicks >= latest.StartedAtUtcTicks))
                    latestChildIdentity = current;

                if (latestChildIdentity is not { } child)
                {
                    AnsiConsole.MarkupLine("[red]Could not capture the owned sing-box identity.[/]");
                    return 1;
                }

                try
                {
                    StateFile.Write(new RunState
                    {
                        ActiveProfile = engine.ActiveProfileName,
                        SingBoxPid = child.Pid,
                        OwnerPid = owner.Pid,
                        StartedAt = DateTime.Now,
                        ProcessNames = engine.MonitoredProcesses,
                        RunGeneration = runGeneration,
                        OwnerStartedAtUtcTicks = owner.StartedAtUtcTicks,
                        OwnerExecutablePath = owner.ExecutablePath,
                        SingBoxStartedAtUtcTicks = child.StartedAtUtcTicks,
                        SingBoxExecutablePath = child.ExecutablePath
                    });
                    statePublished = true;
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Could not publish CLI run state");
                    AnsiConsole.MarkupLine("[red]Could not publish CLI run state.[/]");
                    return 1;
                }
            }

            AnsiConsole.MarkupLine("\n[bold green]VPN Router is running.[/]");
            AnsiConsole.MarkupLine($"[grey]Profile:[/] [cyan]{engine.ActiveProfileName}[/]  [grey]|[/]  [grey]Processes:[/] [cyan]{engine.MonitoredProcesses.Count}[/]  [grey]|[/]  [grey]ETW:[/] [cyan]active[/]");
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]\n");

            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }

            AnsiConsole.MarkupLine("\n[yellow]Stopping...[/]");
            engine.Stop();
            if (!StateFile.ClearIfGeneration(runGeneration))
                AnsiConsole.MarkupLine("[yellow]A newer CLI run owns state; its state was preserved.[/]");
            AnsiConsole.MarkupLine("[green]✓[/] Stopped.");
            return 0;
        }
        finally
        {
            legacyStopWait?.Unregister(null);
            legacyStopEvent?.Dispose();
            stopWait?.Unregister(null);
            stopEvent?.Dispose();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static OwnedProcessIdentity? TryCaptureOwnedChild(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            var identity = ProcessOwnership.TryReadProcessIdentity(process);
            return identity is { } child
                   && ProcessOwnership.IsTrustedRuntimePath(
                       child.ExecutablePath,
                       AppPaths.BinDir,
                       ProcessOwnership.ConfiguredExePath)
                ? child
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int> DryRunAsync(AppSettings settings)
    {
        try
        {
            var sources = VpnEngine.BuildProfileSources(settings);
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
                var validation = LeakProtection.ValidateConfig(sbConfig, settings);

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

            var configDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\config");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "current.json");
            AppPaths.WritePrivateText(configPath, configJson);

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

}
