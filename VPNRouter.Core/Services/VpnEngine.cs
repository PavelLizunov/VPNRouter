using System.IO;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Unified VPN engine that encapsulates the full lifecycle:
/// load config → resolve profile → scan processes → generate sing-box config →
/// firewall → start sing-box → ETW monitor → health monitor.
///
/// Used by GUI (in-process), and can replace CLI/Service startup logic.
/// </summary>
public class VpnEngine : IDisposable
{
    private SingBoxManager? _singBox;
    private HealthMonitor? _healthMonitor;
    private EtwProcessMonitor? _etw;
    private FirewallManager? _firewall;
    private Profile? _activeProfile;
    private ScanResult? _scanResult;
    private readonly ILogger? _logger;

    private bool _disposed;

    // ─── Public state ────────────────────────────────────────────────────────

    public bool IsRunning => _singBox?.IsRunning() ?? false;
    public string ActiveProfileName => _activeProfile?.Name ?? string.Empty;
    public int? SingBoxPid => _singBox?.Pid;
    public List<string> MonitoredProcesses => _scanResult?.ProcessNames ?? new();

    // ─── Events for UI ───────────────────────────────────────────────────────

    /// <summary>Fired when engine status changes (e.g. "Loading profiles...", "sing-box started")</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fired when a targeted process is detected by ETW</summary>
    public event Action<string, int>? ProcessDetected;

    /// <summary>Fired on sing-box restart attempt (attemptNumber, maxAttempts)</summary>
    public event Action<int, int>? RestartAttempted;

    /// <summary>Fired on validation warnings</summary>
    public event Action<string>? Warning;

    public VpnEngine(ILogger? logger = null)
    {
        _logger = logger;
    }

    // ─── Start ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Full VPN startup sequence. Throws on fatal errors.
    /// Checks CancellationToken after each major step to allow clean abort
    /// when the service receives a stop signal during startup.
    /// </summary>
    public async Task StartAsync(AppSettings settings, CancellationToken ct = default)
    {
        // 0. Ensure required directories exist
        var logsDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\VPNRouter\logs");
        Directory.CreateDirectory(logsDir);

        var isCustomConfig = (settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

        // 1. Validate config source
        if (isCustomConfig)
        {
            var customPath = Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                throw new InvalidOperationException(
                    $"Custom config not found: {customPath}. Set app.custom_config in config.yaml.");

            var rawJson = File.ReadAllText(customPath);
            var (isValid, errors) = CustomConfigInjector.Validate(rawJson);
            if (!isValid)
                throw new InvalidOperationException(
                    $"Custom config validation failed: {string.Join("; ", errors)}");
        }
        else
        {
            var servers = settings.Vless.GetEffectiveServers();
            if (servers.Count == 0 || servers.Any(s => string.IsNullOrWhiteSpace(s.Server) || s.Server == "your.server.com"))
                throw new InvalidOperationException("VLESS server not configured.");
        }

        ct.ThrowIfCancellationRequested();

        // 2. Load profiles
        OnStatus("Loading profiles...");
        var sources = BuildProfileSources(settings);
        var manager = new ProfileManager(sources, _logger);
        var collection = await manager.LoadAsync(ct);
        _logger?.Information("[VpnEngine] Loaded {Count} profiles", collection.Profiles.Count);

        ct.ThrowIfCancellationRequested();

        // 3. Resolve active profile
        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        var profileName = settings.ActiveProfile;

        if (string.IsNullOrEmpty(profileName) && !isFullTunnel && !isCustomConfig)
            throw new InvalidOperationException("No active profile specified in config.");

        if (!string.IsNullOrEmpty(profileName))
        {
            var names = profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            _activeProfile = names.Length == 1
                ? manager.GetProfile(names[0])
                : manager.MergeProfiles(names);
        }
        else if (isCustomConfig)
        {
            // Custom config with no profile — process routing handled by user's config
            _activeProfile = new Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
        }
        else
        {
            // Full tunnel with no profiles — empty profile, all traffic goes through VPN
            _activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }

        // Inject custom apps from GUI
        if (settings.CustomApps?.Count > 0)
        {
            foreach (var app in settings.CustomApps)
            {
                if (!string.IsNullOrEmpty(app) &&
                    !_activeProfile.Processes.Any(p => p.Name.Equals(app, StringComparison.OrdinalIgnoreCase)))
                {
                    _activeProfile.Processes.Add(new ProcessRule
                    {
                        Name = app,
                        IncludeChildren = true,
                        ScanPatterns = new[] { app }
                    });
                }
            }
        }

        OnStatus($"Profile: {_activeProfile.Name} ({_activeProfile.Processes.Count} rules)");
        ct.ThrowIfCancellationRequested();

        // 4. Scan processes (synchronous — can take 1-3s, check token after)
        OnStatus("Scanning processes...");
        var scanner = new ProcessScanner(_logger);
        _scanResult = scanner.ScanForProfile(_activeProfile);
        _logger?.Information("[VpnEngine] Resolved {Count} process names", _scanResult.ProcessNames.Count);

        ct.ThrowIfCancellationRequested();

        // 4.5. Auto-detect WireGuard/AmneziaWG subnets and exclude from TUN
        var detectedSubnets = NetworkInterfaceDetector.DetectWireGuardSubnets(
            settings.Tun.InterfaceName, _logger);

        if (detectedSubnets.Count > 0)
        {
            var merged = new HashSet<string>(settings.Tun.RouteExcludeAddress, StringComparer.OrdinalIgnoreCase);
            foreach (var subnet in detectedSubnets)
                merged.Add(subnet);
            settings.Tun.RouteExcludeAddress = merged.ToList();

            _logger?.Information("[VpnEngine] Auto-excluded WG/AWG subnets: {Subnets}",
                string.Join(", ", detectedSubnets));
        }

        ct.ThrowIfCancellationRequested();

        // 5. Generate + validate config
        string configJson;
        if (isCustomConfig)
        {
            var customPath = Environment.ExpandEnvironmentVariables(settings.App.CustomConfig!);
            var rawJson = File.ReadAllText(customPath);
            configJson = CustomConfigInjector.Inject(rawJson, _scanResult.ProcessNames, settings);
            OnStatus("Custom config injected with process routing");
        }
        else
        {
            var sbConfig = ConfigGenerator.Generate(_activeProfile, _scanResult.ProcessNames, settings);
            var validation = LeakProtection.ValidateConfig(sbConfig);

            foreach (var warn in validation.Warnings)
            {
                _logger?.Warning("[VpnEngine] {Warn}", warn);
                Warning?.Invoke(warn);
            }

            if (!validation.IsValid)
            {
                var errors = string.Join("; ", validation.Errors);
                throw new InvalidOperationException($"Config validation failed: {errors}");
            }

            configJson = ConfigGenerator.Serialize(sbConfig);
        }

        ct.ThrowIfCancellationRequested();

        // 6. Ensure sing-box binary exists
        var exePath = Environment.ExpandEnvironmentVariables(settings.SingBox.ExecutablePath);
        if (!File.Exists(exePath))
        {
            // Try to copy from app directory (bundled in ZIP)
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
            if (File.Exists(bundledPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                File.Copy(bundledPath, exePath, overwrite: false);
                _logger?.Information("[VpnEngine] Copied sing-box from bundle to {Path}", exePath);
            }
            else
            {
                throw new FileNotFoundException($"sing-box not found at: {exePath}");
            }
        }

        ct.ThrowIfCancellationRequested();

        // 7. Firewall block rules
        _firewall = new FirewallManager(_logger);
        if (_activeProfile.BlockOnVpnFail)
        {
            _firewall.CreateBlockRules(_scanResult.ProcessNames);
            OnStatus("Firewall block rules created (disabled)");
        }

        ct.ThrowIfCancellationRequested();

        // 8. Start sing-box
        OnStatus("Starting sing-box...");
        _singBox = new SingBoxManager(settings.SingBox, _logger);
        _singBox.StartWithJson(configJson);

        // Wait up to 5s for startup
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (_singBox.IsRunning()) break;
        }

        if (!_singBox.IsRunning())
        {
            _firewall.DeleteAllRules();
            throw new Exception("sing-box failed to start within 5 seconds. Check logs.");
        }

        _logger?.Information("[VpnEngine] sing-box started (PID {Pid})", _singBox.Pid);
        OnStatus($"sing-box started (PID {_singBox.Pid})");

        ct.ThrowIfCancellationRequested();

        // 9. Firewall rules stay DISABLED while VPN is running.
        // sing-box TUN handles routing — firewall block is only needed if sing-box crashes.
        // HealthMonitor.OnSingBoxCrashed() calls EnableBlockRules() when sing-box dies,
        // and DisableBlockRules() after successful restart.
        if (_activeProfile.BlockOnVpnFail)
        {
            OnStatus("Firewall leak protection ready (armed for VPN failure)");
        }

        // 10. ETW + HealthMonitor
        _etw = new EtwProcessMonitor(_logger);
        _healthMonitor = new HealthMonitor(
            _singBox, scanner, _firewall,
            settings.Monitoring, _logger);

        _etw.ProcessStarted += (_, e) =>
        {
            var isTargeted = _activeProfile.Processes
                .Any(r => r.ScanPatterns
                    .Any(p => ProcessScanner.MatchesPattern(e.ProcessName + ".exe", p)));

            if (isTargeted)
            {
                ProcessDetected?.Invoke(e.ProcessName, e.ProcessId);
                _healthMonitor.OnNewProcessDetected(e.ProcessName);
            }
        };

        _healthMonitor.RestartAttempted += (_, attempt) =>
        {
            RestartAttempted?.Invoke(attempt, settings.Monitoring.MaxRestartAttempts);
        };

        _etw.Start();
        _healthMonitor.Start(_activeProfile, settings, _scanResult);

        OnStatus("VPN Router is running");
    }

    // ─── Stop ────────────────────────────────────────────────────────────────

    public void Stop()
    {
        OnStatus("Stopping...");

        try { _healthMonitor?.Stop(); } catch { }
        try { _etw?.Stop(); } catch { }

        if (_activeProfile?.BlockOnVpnFail == true)
        {
            try { _firewall?.DisableBlockRules(); } catch { }
            try { _firewall?.DeleteAllRules(); } catch { }
        }

        try { _singBox?.Stop(); } catch { }
        try { _firewall?.Dispose(); } catch { }

        _singBox = null;
        _healthMonitor = null;
        _etw = null;
        _firewall = null;

        OnStatus("Stopped");
        _logger?.Information("[VpnEngine] Stopped");
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRunning) Stop();
        GC.SuppressFinalize(this);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void OnStatus(string message)
    {
        _logger?.Information("[VpnEngine] {Status}", message);
        StatusChanged?.Invoke(message);
    }

    private static List<IProfileSource> BuildProfileSources(AppSettings settings)
    {
        var sources = new List<IProfileSource>();
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

        // App directory profiles (bundled)
        var appDir = AppContext.BaseDirectory;
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 80));

        // %ProgramData% profiles
        var programDataProfiles = Environment.ExpandEnvironmentVariables(
            @"%ProgramData%\VPNRouter\profiles\default.json");
        if (File.Exists(programDataProfiles))
            sources.Add(new LocalProfileSource(programDataProfiles, 85));

        // Built-in fallback
        sources.Add(new BuiltInProfileSource());
        return sources;
    }
}
