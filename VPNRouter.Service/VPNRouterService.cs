using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

/// <summary>
/// Windows Service implementation.
/// Runs as a BackgroundService hosted by Microsoft.Extensions.Hosting.
/// Lifecycle: OnStartAsync → ExecuteAsync (blocks) → OnStopAsync.
/// </summary>
public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;

    private SingBoxManager? _singBox;
    private HealthMonitor? _healthMonitor;
    private EtwProcessMonitor? _etw;
    private FirewallManager? _firewall;
    private AppSettings? _settings;
    private Core.Models.Profile? _activeProfile;

    private const string EventSourceName = "VPNRouter";
    private const string EventLogName = "Application";

    public VPNRouterService(ILogger<VPNRouterService> logger)
    {
        _logger = logger;
        EnsureEventSource();
    }

    // ─── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[VPNRouterService] ExecuteAsync started");

        try
        {
            await StartVpnAsync(stoppingToken);

            // Keep service alive until stop is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[VPNRouterService] Stop requested");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[VPNRouterService] Fatal error in ExecuteAsync");
            WriteEventLog($"Fatal error: {ex.Message}", EventLogEntryType.Error);
            throw; // causes service to report failure and stop
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[VPNRouterService] Stopping");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        CleanupAll();

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("[VPNRouterService] Stopped");
        WriteEventLog("VPN Router service stopped", EventLogEntryType.Information);
    }

    // ─── VPN Startup ──────────────────────────────────────────────────────────

    private async Task StartVpnAsync(CancellationToken ct)
    {
        // 1. Load settings
        _settings = SettingsLoader.Load();
        _logger.LogInformation("[VPNRouterService] Loaded config, active profile: {Profile}",
            _settings.ActiveProfile);

        // 2. Load profiles
        var sources = BuildProfileSources(_settings);
        var profileManager = new ProfileManager(sources,
            Serilog.Log.Logger);

        var collection = await profileManager.LoadAsync(ct);
        _logger.LogInformation("[VPNRouterService] Loaded {Count} profiles", collection.Profiles.Count);

        // 3. Resolve active profile
        var profileNames = _settings.ActiveProfile
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        _activeProfile = profileNames.Length == 1
            ? profileManager.GetProfile(profileNames[0])
            : profileManager.MergeProfiles(profileNames);

        _logger.LogInformation("[VPNRouterService] Active profile: {Profile} ({Count} rules)",
            _activeProfile.Name, _activeProfile.Processes.Count);

        // 4. Scan processes
        var scanner = new ProcessScanner(Serilog.Log.Logger);
        var scan = scanner.ScanForProfile(_activeProfile);
        _logger.LogInformation("[VPNRouterService] Resolved {Count} process names", scan.ProcessNames.Count);

        // 5. Generate + validate config
        var sbConfig = ConfigGenerator.Generate(_activeProfile, scan.ProcessNames, _settings);
        var validation = LeakProtection.ValidateConfig(sbConfig);

        foreach (var warn in validation.Warnings)
            _logger.LogWarning("[VPNRouterService] Config warning: {Warn}", warn);

        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException($"Config validation failed: {errors}");
        }

        // 6. Firewall block rules
        _firewall = new FirewallManager(Serilog.Log.Logger);
        if (_activeProfile.BlockOnVpnFail)
        {
            _firewall.CreateBlockRules(scan.ProcessNames);
            _logger.LogInformation("[VPNRouterService] Firewall block rules created (disabled)");
        }

        // 7. Start sing-box
        _singBox = new SingBoxManager(_settings.SingBox, Serilog.Log.Logger);
        _singBox.Start(sbConfig);

        // Wait up to 5s for sing-box to start
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (_singBox.IsRunning()) break;
        }

        if (!_singBox.IsRunning())
            throw new Exception("sing-box failed to start within 5 seconds");

        _logger.LogInformation("[VPNRouterService] sing-box started (PID {Pid})", _singBox.Pid);
        WriteEventLog($"sing-box started (PID {_singBox.Pid}), profile: {_activeProfile.Name}",
            EventLogEntryType.Information);

        // 8. Enable firewall rules now VPN is up
        if (_activeProfile.BlockOnVpnFail)
        {
            _firewall.EnableBlockRules();
            _logger.LogInformation("[VPNRouterService] Firewall block rules enabled");
        }

        // 9. ETW + HealthMonitor
        _etw = new EtwProcessMonitor(Serilog.Log.Logger);
        _healthMonitor = new HealthMonitor(
            _singBox, scanner, _firewall,
            _settings.Monitoring, Serilog.Log.Logger);

        _etw.ProcessStarted += (_, e) =>
        {
            var isTargeted = _activeProfile.Processes
                .Any(r => r.ScanPatterns
                    .Any(p => ProcessScanner.MatchesPattern(e.ProcessName + ".exe", p)));

            if (isTargeted)
                _healthMonitor.OnNewProcessDetected(e.ProcessName);
        };

        _healthMonitor.RestartAttempted += (_, attempt) =>
            WriteEventLog($"sing-box restart attempt {attempt}/{_settings.Monitoring.MaxRestartAttempts}",
                EventLogEntryType.Warning);

        _etw.Start();
        _healthMonitor.Start(_activeProfile, _settings, scan);

        _logger.LogInformation("[VPNRouterService] All components running");
    }

    // ─── Cleanup ──────────────────────────────────────────────────────────────

    private void CleanupAll()
    {
        try { _healthMonitor?.Stop(); } catch { }
        try { _etw?.Stop(); }           catch { }

        if (_activeProfile?.BlockOnVpnFail == true)
        {
            try { _firewall?.DisableBlockRules(); } catch { }
            try { _firewall?.DeleteAllRules(); }    catch { }
        }

        try { _singBox?.Stop(); } catch { }
        try { _firewall?.Dispose(); } catch { }

        _logger.LogInformation("[VPNRouterService] Cleanup complete");
    }

    // ─── Event Log ────────────────────────────────────────────────────────────

    private static void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
                EventLog.CreateEventSource(EventSourceName, EventLogName);
        }
        catch { /* may require elevated rights first run */ }
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            EventLog.WriteEntry(EventSourceName, message, type);
        }
        catch { /* ignore if source not yet created */ }
    }

    // ─── Profile source helper ────────────────────────────────────────────────

    private static List<Core.Interfaces.IProfileSource> BuildProfileSources(AppSettings settings)
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

        // Fallback: default.json shipped with the service binary
        var exeDir = AppContext.BaseDirectory;
        var defaultJson = Path.Combine(exeDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 80));

        sources.Add(new BuiltInProfileSource());
        return sources;
    }
}
