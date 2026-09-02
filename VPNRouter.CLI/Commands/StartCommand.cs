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
    // Phase 4 Wave 19 (v3.0 refactor): ISettingsStore ctor injection for
    // testability. Default <see cref="RealSettingsStore.Instance"/> preserves
    // the pre-3G-1 static-loader behaviour; tests can pass InMemorySettingsStore.
    private readonly ISettingsStore _settingsStore;

    public StartCommand() : this(null) { }

    public StartCommand(ISettingsStore? settingsStore)
    {
        _settingsStore = settingsStore ?? RealSettingsStore.Instance;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, StartSettings settings)
    {
        AnsiConsole.Write(new FigletText("VPNRouter").Color(Color.Cyan1));

        // 1. Load app settings
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

        // 3. Check admin rights — but NOT for --dry-run.
        // Dry-run only generates + validates a config JSON; no TUN adapter,
        // no firewall rules, no registry writes. Forcing admin there made
        // the flag useless for what it's for (debugging from a regular
        // shell before spawning an elevated run).
        if (!settings.DryRun && !AdminHelper.IsAdmin())
        {
            AnsiConsole.MarkupLine("[red]✗ Administrator rights required for TUN interface.[/]");
            AnsiConsole.MarkupLine("[yellow]  Run as Administrator or via Windows Service.[/]");
            AnsiConsole.MarkupLine("[grey]  (--dry-run works without admin — generates + validates config only)[/]");
            return 1;
        }

        // 3b. Resolve subscription-mode settings → flat Vless.Servers list.
        // Without this, subscribe-mode configs fail validation with "No 'proxy'
        // outbound defined" because ConfigGenerator only reads Vless.* fields.
        // Matches the logic in VPNRouterService.cs so CLI / Service are equivalent.
        var resolved = await SubscriptionResolver.ResolveAsync(
            appSettings,
            refreshFromNetwork: true,
            Serilog.Log.Logger);
        if (resolved > 0)
            AnsiConsole.MarkupLine($"[grey]  → resolved {resolved} server(s) from subscription[/]");

        // 3c. Pre-flight: verify we actually have a viable VLESS outbound source
        // before we burn cycles on ConfigGenerator + LeakProtection only to fail
        // with the cryptic "No 'proxy' outbound defined". Custom mode skips this
        // check because it supplies its own JSON (and CustomConfigInjector handles
        // missing-file errors separately).
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

        // 4. Dry-run: generate config, validate, write to disk, exit
        if (settings.DryRun)
        {
            return await DryRunAsync(appSettings);
        }

        var runGeneration = Guid.NewGuid();
        using var ownerProcess = Process.GetCurrentProcess();
        var ownerIdentity = ProcessOwnership.TryReadProcessIdentity(ownerProcess);
        if (ownerIdentity is not { } owner)
        {
            AnsiConsole.MarkupLine("[red]✗ Could not capture the CLI owner identity.[/]");
            return 1;
        }

        // v2.40.0-r10 #4 (core-audit): sweep leftover firewall kill-switch
        // rules before taking VPN ownership. The GUI front-end has always
        // done this on startup (App/Program.cs); the CLI did not, so a CLI
        // crash that left block rules enabled would strand the user's internet
        // until the GUI happened to run. `start` is admin-gated and is taking
        // ownership here, so an unconditional sweep mirrors the GUI.
        VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);

        // v2.40.0-r10 #2 (core-audit): also sweep on process exit so an
        // abnormal teardown that skips the engine's clean DeleteAllRules
        // doesn't leave the kill-switch blocking the internet. Gated on
        // !IsOwnedByAnyone() so we never nuke another live instance's rules;
        // the startup sweep is the fail-closed backstop if we exit while
        // still holding the lock.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (!VPNRouter.Core.Services.TunOwnershipLock.IsOwnedByAnyone())
                    VPNRouter.Core.Services.FirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);
            }
            catch { }
        };

        // 5. Start VPN via engine
        // 3G-4 (v3.0 refactor): use the PlatformServices factory instead of
        // direct construction — keeps the platform-specific scanner /
        // firewall / monitor wiring in one place. On Windows the produced
        // services are identical to the prior hand-wired set (ProcessScanner,
        // FirewallManager, EtwProcessMonitor).
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

        // Keep the persisted child capability current after HealthMonitor
        // restarts, but only while this exact run still owns state.json. The
        // initial launch fires before publication and therefore no-ops here;
        // the complete initial identity is written below.
        engine.SingBoxStarted += newPid =>
        {
            try
            {
                using var process = Process.GetProcessById(newPid);
                var child = ProcessOwnership.TryReadOwnedSingBoxIdentity(process);
                if (child is { } identity)
                    StateFile.TryUpdateChild(runGeneration, identity);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Could not publish restarted sing-box identity");
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

        // 6. Create and register the generation-qualified stop capability
        // before state publication. AutoResetEvent retains an early signal, so
        // a Stop that can read this generation can never race event creation.
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopWait = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                stopEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    StopCommand.BuildStopEventName(owner.Pid, runGeneration));
                stopWait = ThreadPool.RegisterWaitForSingleObject(
                    stopEvent,
                    (_, _) => cts.Cancel(),
                    state: null,
                    timeout: Timeout.InfiniteTimeSpan,
                    executeOnlyOnce: true);
            }

            var childPid = engine.SingBoxPid ?? 0;
            OwnedProcessIdentity? childIdentity = null;
            if (childPid > 0)
            {
                try
                {
                    using var childProcess = Process.GetProcessById(childPid);
                    childIdentity = ProcessOwnership.TryReadOwnedSingBoxIdentity(childProcess);
                }
                catch (ArgumentException)
                {
                    // The child exited before its capability could be published.
                }
            }

            if (childIdentity is not { } child)
            {
                AnsiConsole.MarkupLine("[red]✗ Could not capture the owned sing-box identity.[/]");
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
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ Could not publish CLI run state:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            AnsiConsole.MarkupLine("\n[bold green]VPN Router is running.[/]");
            AnsiConsole.MarkupLine($"[grey]Profile:[/] [cyan]{engine.ActiveProfileName}[/]  [grey]|[/]  [grey]Processes:[/] [cyan]{engine.MonitoredProcesses.Count}[/]  [grey]|[/]  [grey]ETW:[/] [cyan]active[/]");
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]\n");

            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            catch (OperationCanceledException) { }

            // 7. Graceful shutdown. A replacement generation is never deleted.
            AnsiConsole.MarkupLine("\n[yellow]Stopping...[/]");
            engine.Stop();
            if (!StateFile.ClearIfGeneration(runGeneration))
                AnsiConsole.MarkupLine("[yellow]A newer CLI run owns state; its state was preserved.[/]");
            AnsiConsole.MarkupLine("[green]✓[/] Stopped.");
            return 0;
        }
        finally
        {
            stopWait?.Unregister(null);
            stopEvent?.Dispose();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> DryRunAsync(AppSettings settings)
    {
        try
        {
            // Load profiles & resolve. #7 (cleanup 2026-07-10): dry-run uses the
            // SAME source list as a real start (ProfileSourceFactory.Create) — the
            // old private BuildDryRunSources was a near-duplicate that silently
            // dropped the %ProgramData%\VPNRouter\profiles source, so a dry-run
            // could preview a different profile set than the actual start used.
            var sources = ProfileSourceFactory.Create(settings);
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
                // Bug-r9-F-DEFENSIVE: settings passed for outbound-IP cross-check.
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

            // Write config
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
