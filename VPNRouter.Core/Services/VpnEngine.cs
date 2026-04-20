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
    private IProcessMonitor? _etw;
    private IFirewallManager? _firewall;
    private Profile? _activeProfile;
    private ScanResult? _scanResult;
    private readonly IProcessScanner _scanner;
    private readonly Func<IFirewallManager> _firewallFactory;
    private readonly Func<IProcessMonitor> _monitorFactory;
    private readonly ILogger? _logger;

    private bool _disposed;

    // ─── Public state ────────────────────────────────────────────────────────

    public bool IsRunning => _singBox?.IsRunning() ?? false;
    public string ActiveProfileName => _activeProfile?.Name ?? string.Empty;
    public int? SingBoxPid => _singBox?.Pid;
    public List<string> MonitoredProcesses => _scanResult?.ProcessNames ?? new();

    /// <summary>"custom" or "generated" — set during StartAsync.</summary>
    public string ActiveConfigMode { get; private set; } = "generated";

    /// <summary>"split" or "full" — set during StartAsync.</summary>
    public string ActiveRoutingMode { get; private set; } = "split";

    /// <summary>IP/host of the active server (for status display).</summary>
    public string ActiveServerAddress { get; private set; } = string.Empty;

    // ─── Events for UI ───────────────────────────────────────────────────────

    /// <summary>Fired when engine status changes (e.g. "Loading profiles...", "sing-box started")</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fired when a targeted process is detected by ETW</summary>
    public event Action<string, int>? ProcessDetected;

    /// <summary>Fired on sing-box restart attempt (attemptNumber, maxAttempts)</summary>
    public event Action<int, int>? RestartAttempted;

    /// <summary>Fired on validation warnings</summary>
    public event Action<string>? Warning;

    public VpnEngine(
        IProcessScanner scanner,
        Func<IFirewallManager> firewallFactory,
        Func<IProcessMonitor> monitorFactory,
        ILogger? logger = null)
    {
        _scanner = scanner;
        _firewallFactory = firewallFactory;
        _monitorFactory = monitorFactory;
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
        AppPaths.EnsureDirectories();

        // 0a. Flush DNS cache to prevent leakage of pre-VPN resolved entries
        if (settings.App.FlushDnsOnStart)
        {
            DnsFlusher.Flush(_logger);
        }

        // 0b. Download geo rule sets if RU bypass is enabled and files missing
        if (settings.App.BypassRussianTraffic && !GeoDataDownloader.AreGeoFilesAvailable())
        {
            OnStatus("Downloading geo data...");
            try
            {
                var downloader = new GeoDataDownloader(_logger);
                var ok = await downloader.EnsureGeoFilesAsync(ct);
                if (!ok)
                    _logger?.Warning("[VpnEngine] Geo data download failed — RU bypass will be disabled for this session");
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[VpnEngine] Geo data download error — RU bypass will be disabled");
            }
        }

        var isCustomConfig = (settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);
        ActiveConfigMode = isCustomConfig ? "custom" : "generated";
        ActiveRoutingMode = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase) ? "full" : "split";

        // 1. Validate config source
        if (isCustomConfig)
        {
            // Resolve custom config path: try multi-config list first, fallback to single path
            var customPath = ResolveCustomConfigPath(settings);
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                throw new InvalidOperationException(
                    $"Custom config not found: {customPath}. Add a config in the Servers tab.");

            var rawJson = File.ReadAllText(customPath);
            var (isValid, errors) = CustomConfigInjector.Validate(rawJson);
            if (!isValid)
                throw new InvalidOperationException(
                    $"Custom config validation failed: {string.Join("; ", errors)}");

            // Extract server address for status display
            try { var (_, srv) = CustomConfigInjector.ParseConfigInfo(rawJson); ActiveServerAddress = srv; }
            catch { ActiveServerAddress = ""; }
        }
        else
        {
            var allServers = settings.Vless.GetEffectiveServers()
                .Where(s => !string.IsNullOrWhiteSpace(s.Server) && s.Server != "your.server.com")
                .ToList();
            if (allServers.Count == 0)
                throw new InvalidOperationException("VLESS server not configured.");

            settings.Vless.Servers = allServers;
            // Show the active server's IP (what actually runs), not just [0]
            var activeServers = settings.Vless.GetActiveServers();
            ActiveServerAddress = activeServers.Count > 0 ? activeServers[0].Server : allServers[0].Server;
        }

        ct.ThrowIfCancellationRequested();

        // 2. Load profiles
        OnStatus("Loading profiles...");
        var sources = BuildProfileSources(settings);
        var manager = new ProfileManager(sources, _logger);
        var collection = await manager.LoadAsync(ct);

        // 2a. Merge user-added apps into default groups (custom_group_apps)
        if (settings.CustomGroupApps?.Count > 0)
        {
            foreach (var (groupName, extras) in settings.CustomGroupApps)
            {
                var profile = collection.Profiles.FirstOrDefault(p =>
                    p.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (profile == null) continue;
                foreach (var app in extras ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    if (profile.Processes.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
            }
        }

        // 2b. Inject user-created categories (custom_categories) as profiles
        if (settings.CustomCategories?.Count > 0)
        {
            foreach (var cat in settings.CustomCategories)
            {
                if (string.IsNullOrWhiteSpace(cat.Name)) continue;
                if (collection.Profiles.Any(p => p.Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var profile = new Profile
                {
                    Name = cat.Name,
                    Description = "User category",
                    DnsMode = "vpn_only",
                    BlockOnVpnFail = false,
                    Processes = new List<ProcessRule>()
                };
                foreach (var app in cat.Apps ?? new())
                {
                    if (string.IsNullOrWhiteSpace(app)) continue;
                    var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                    profile.Processes.Add(new ProcessRule
                    {
                        Name = name, IncludeChildren = true, ScanPatterns = new[] { name }
                    });
                }
                collection.Profiles.Add(profile);
            }
        }

        // v2.22.0-r1: dump catalogue at boot so we can eyeball from logs
        // whether a Linux build loaded the Linux variant, etc.
        _logger?.Information(
            "[VpnEngine] Loaded profile catalogue ({Count}): {Names}",
            collection.Profiles.Count,
            string.Join(", ", collection.Profiles.Select(p => p.Name)));

        ct.ThrowIfCancellationRequested();

        // 3. Resolve active profile — tolerant path
        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        var profileName = settings.ActiveProfile;

        if (string.IsNullOrEmpty(profileName) && !isFullTunnel && !isCustomConfig)
            throw new InvalidOperationException("No active profile specified in config.");

        if (!string.IsNullOrEmpty(profileName))
        {
            var names = profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            // v2.22.0-r1: tolerant merge — log+skip unknown names, fall back
            // to remaining resolved ones. Previously a single missing name
            // threw "Profile 'Foo' not found" and killed the whole start.
            // That surfaced as "Messengers not found" on Linux when user's
            // yaml still referenced a renamed/absent group.
            var merged = manager.MergeProfilesTolerant(names, out var missing);
            if (merged == null)
            {
                // Everything missed — genuine broken config, keep the hard error
                throw new InvalidOperationException(
                    $"None of the requested profiles exist: {string.Join(", ", names)}. " +
                    $"Available: {string.Join(", ", collection.Profiles.Select(p => p.Name))}");
            }
            _activeProfile = merged;

            if (missing.Count > 0)
            {
                var msg = $"Skipped unknown profile(s): {string.Join(", ", missing)}";
                Warning?.Invoke(msg);
                // Rewrite ActiveProfile to drop missing names so this self-heals
                // on the next launch. Uses currently-resolved name(s).
                var sanitized = string.Join(",",
                    names.Where(n => !missing.Contains(n, StringComparer.OrdinalIgnoreCase)));
                if (!string.Equals(sanitized, profileName, StringComparison.Ordinal))
                {
                    settings.ActiveProfile = sanitized;
                    try
                    {
                        SettingsLoader.Save(settings);
                        _logger?.Information(
                            "[VpnEngine] ActiveProfile migrated: '{Old}' → '{New}'",
                            profileName, sanitized);
                    }
                    catch (Exception saveEx)
                    {
                        _logger?.Warning(saveEx,
                            "[VpnEngine] Failed to persist ActiveProfile migration");
                    }
                }
            }
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
        _scanResult = _scanner.ScanForProfile(_activeProfile);
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
            var customPath = ResolveCustomConfigPath(settings);
            // Resolve ProgramData copy path (may already exist from import)
            var activeEntry = settings.App.CustomConfigs
                .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig);
            var configName = activeEntry?.Name ?? "custom";
            var localCopy = CustomConfigInjector.GetProgramDataPath(configName);

            // If ProgramData copy doesn't exist, copy from source
            if (!File.Exists(localCopy) && File.Exists(customPath))
            {
                localCopy = CustomConfigInjector.CopyToProgramData(customPath, configName);
                _logger?.Information("[VpnEngine] Custom config copied to {Path}", localCopy);
            }

            var rawJson = File.ReadAllText(localCopy);
            configJson = CustomConfigInjector.Inject(rawJson, _scanResult.ProcessNames, settings);
            OnStatus($"Custom config '{configName}' injected with process routing");
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

        // 6. Ensure sing-box binary exists and is up-to-date
        var exePath = OperatingSystem.IsWindows()
            ? Environment.ExpandEnvironmentVariables(settings.SingBox.ExecutablePath)
            : AppPaths.SingBoxExePath;
        var bundledPath = Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");

        if (File.Exists(bundledPath))
        {
            bool needDeploy = !File.Exists(exePath);

            if (!needDeploy)
            {
                // Deploy if bundled version differs (size change = different build/version)
                var installedSize = new FileInfo(exePath).Length;
                var bundledSize = new FileInfo(bundledPath).Length;
                needDeploy = installedSize != bundledSize;
            }

            if (needDeploy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                File.Copy(bundledPath, exePath, overwrite: true);
                _logger?.Information("[VpnEngine] Deployed sing-box from bundle to {Path}", exePath);
            }
        }
        else if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }

        ct.ThrowIfCancellationRequested();

        // 7. Firewall block rules
        _firewall = _firewallFactory();
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

        // Warm up TUN + proxy connection. After TUN creation, Windows needs time to rebuild
        // routing tables. First packets may get lost. Retry until connectivity works.
        OnStatus("Warming up network...");
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    await http.GetStringAsync("https://www.gstatic.com/generate_204", ct);
                    _logger?.Information("[VpnEngine] TUN ready after {Ms}ms (attempt {Attempt})",
                        sw.ElapsedMilliseconds, attempt);
                    OnStatus($"Connected (PID {_singBox.Pid})");
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger?.Debug("[VpnEngine] Warm-up attempt {Attempt}: {Error}",
                        attempt, ex.GetType().Name);
                }
            }
            _logger?.Warning("[VpnEngine] TUN warm-up failed after {Ms}ms", sw.ElapsedMilliseconds);
            OnStatus($"Connected (PID {_singBox.Pid})");
        }, ct);

        ct.ThrowIfCancellationRequested();

        // 9. Firewall rules stay DISABLED while VPN is running.
        // sing-box TUN handles routing — firewall block is only needed if sing-box crashes.
        // HealthMonitor.OnSingBoxCrashed() calls EnableBlockRules() when sing-box dies,
        // and DisableBlockRules() after successful restart.
        if (_activeProfile.BlockOnVpnFail)
        {
            OnStatus("Firewall leak protection ready (armed for VPN failure)");
        }

        // 10. Process monitor + HealthMonitor
        _etw = _monitorFactory();
        _healthMonitor = new HealthMonitor(
            _singBox, _scanner, _firewall,
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

        // Apply Windows DNS hardening: disable SMHNR, set TUN metric.
        // Closes the leak vector where Windows sends DNS to ALL adapters in
        // parallel — leaks via secondary VPN tunnels (e.g. AmneziaWG) bypass
        // sing-box entirely. No-op on macOS.
#if PLATFORM_WINDOWS
        WindowsDnsHardening.Apply(_logger);
#endif

        OnStatus("VPN Router is running");
    }

    // ─── Apply (hot-reload config changes) ──────────────────────────────────

    /// <summary>
    /// Hot-reload config after user changed app list (groups, custom apps, categories).
    /// Re-resolves profile, re-scans processes, regenerates sing-box config,
    /// then tries Clash API hot-reload. Falls back to full restart on failure.
    /// Returns true on success (either hot-reload or restart).
    ///
    /// <para>
    /// v2.20.4: <paramref name="forceRestart"/> bypasses the hot-reload
    /// attempt and goes straight to stop+launch. Callers changing
    /// structural things — especially <see cref="AppSettings.AppConfig.RoutingMode"/>
    /// (split ↔ full) — must pass true. sing-box's Clash API <c>PUT /configs</c>
    /// accepts the new config and reports success, but the TUN route table
    /// and DNS rules from the previous config remain active for already-
    /// established connections. Users saw "toggle does nothing" because
    /// the API returned 200 and we returned success. A full process restart
    /// is the only way to guarantee the new routing mode takes effect.
    /// </para>
    /// </summary>
    public async Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct = default, bool forceRestart = false)
    {
        if (_singBox == null || !_singBox.IsRunning())
        {
            _logger?.Warning("[VpnEngine] Apply called but sing-box not running");
            return false;
        }

        OnStatus("Applying config changes...");

        try
        {
            // Re-resolve active profile (repeat StartAsync profile logic)
            var sources = BuildProfileSources(settings);
            var manager = new ProfileManager(sources, _logger);
            var collection = await manager.LoadAsync(ct);

            // Merge custom_group_apps
            if (settings.CustomGroupApps?.Count > 0)
            {
                foreach (var (groupName, extras) in settings.CustomGroupApps)
                {
                    var p = collection.Profiles.FirstOrDefault(x =>
                        x.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                    if (p == null) continue;
                    foreach (var app in extras ?? new())
                    {
                        if (string.IsNullOrWhiteSpace(app)) continue;
                        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                        if (p.Processes.Any(pr => pr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        p.Processes.Add(new ProcessRule { Name = name, IncludeChildren = true, ScanPatterns = new[] { name } });
                    }
                }
            }

            // Inject custom_categories
            if (settings.CustomCategories?.Count > 0)
            {
                foreach (var cat in settings.CustomCategories)
                {
                    if (string.IsNullOrWhiteSpace(cat.Name)) continue;
                    if (collection.Profiles.Any(p => p.Name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var profile = new Profile
                    {
                        Name = cat.Name,
                        Description = "User category",
                        DnsMode = "vpn_only",
                        BlockOnVpnFail = false,
                        Processes = new List<ProcessRule>()
                    };
                    foreach (var app in cat.Apps ?? new())
                    {
                        if (string.IsNullOrWhiteSpace(app)) continue;
                        var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe";
                        profile.Processes.Add(new ProcessRule { Name = name, IncludeChildren = true, ScanPatterns = new[] { name } });
                    }
                    collection.Profiles.Add(profile);
                }
            }

            // Resolve active profile (tolerant — v2.22.0-r1)
            var profileName = settings.ActiveProfile;
            var isFullTunnel = (settings.App.RoutingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);
            var isCustomConfig = settings.App.ConfigMode?.Equals("custom", StringComparison.OrdinalIgnoreCase) == true;

            if (!string.IsNullOrEmpty(profileName))
            {
                var names = profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var merged = manager.MergeProfilesTolerant(names, out var missing);
                if (merged == null)
                {
                    _logger?.Warning(
                        "[VpnEngine] Apply: none of {Names} exist in catalogue — skipping apply",
                        profileName);
                    return false;
                }
                _activeProfile = merged;
                if (missing.Count > 0)
                {
                    Warning?.Invoke($"Skipped unknown profile(s): {string.Join(", ", missing)}");
                }
            }
            else if (isCustomConfig)
                _activeProfile = new Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
            else
                _activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };

            // Inject custom apps from GUI
            if (settings.CustomApps?.Count > 0 && _activeProfile != null)
            {
                foreach (var app in settings.CustomApps)
                {
                    if (string.IsNullOrEmpty(app)) continue;
                    if (_activeProfile.Processes.Any(p => p.Name.Equals(app, StringComparison.OrdinalIgnoreCase))) continue;
                    _activeProfile.Processes.Add(new ProcessRule
                    {
                        Name = app, IncludeChildren = true, ScanPatterns = new[] { app }
                    });
                }
            }

            // Re-scan processes
            _scanResult = _scanner.ScanForProfile(_activeProfile!);

            // Regenerate config JSON (generated mode only — custom mode uses injected routing)
            string configJson;
            if (isCustomConfig)
            {
                var customPath = Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
                if (!File.Exists(customPath))
                {
                    _logger?.Warning("[VpnEngine] Apply: custom config not found, skipping");
                    return false;
                }
                var rawJson = File.ReadAllText(customPath);
                configJson = CustomConfigInjector.Inject(rawJson, _scanResult.ProcessNames, settings);
            }
            else
            {
                var sbConfig = ConfigGenerator.Generate(_activeProfile!, _scanResult.ProcessNames, settings);
                configJson = ConfigGenerator.Serialize(sbConfig);
            }

            // Try hot-reload first, UNLESS the caller explicitly asked for
            // a full restart (v2.20.4). Structural changes like split↔full
            // tunnel mode need a process restart — hot-reload accepts the
            // new config but leaves existing TUN routes in place, so the
            // user sees no effect.
            if (!forceRestart && _singBox.TryReloadConfigJson(configJson))
            {
                OnStatus($"Applied (hot-reload, PID {_singBox.Pid})");
                _logger?.Information("[VpnEngine] Applied via hot-reload");
                return true;
            }

            if (forceRestart)
                _logger?.Information("[VpnEngine] Forced full restart (structural change)");
            else
                _logger?.Warning("[VpnEngine] Hot-reload failed, falling back to full restart");

            _singBox.ReloadConfigJson(configJson);
            OnStatus($"Applied (restart, PID {_singBox.Pid})");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[VpnEngine] Apply failed");
            OnStatus($"Apply failed: {ex.Message}");
            return false;
        }
    }

    // ─── Stop ────────────────────────────────────────────────────────────────

    public void Stop()
    {
        OnStatus("Stopping...");

#if PLATFORM_WINDOWS
        try { WindowsDnsHardening.Restore(_logger); } catch { }
#endif

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

    // ─── Config resolution ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the custom config file path. Priority:
    /// 1. ActiveCustomConfig name → look up in CustomConfigs list → ProgramData path
    /// 2. Fallback to single CustomConfig path (backward compat)
    /// </summary>
    private static string ResolveCustomConfigPath(AppSettings settings)
    {
        // Try multi-config list
        if (settings.App.CustomConfigs?.Count > 0)
        {
            // Pick active config by name, or fall back to first in list
            var entry = !string.IsNullOrEmpty(settings.App.ActiveCustomConfig)
                ? settings.App.CustomConfigs
                    .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig)
                    ?? settings.App.CustomConfigs[0]
                : settings.App.CustomConfigs[0];

            var path = Environment.ExpandEnvironmentVariables(entry.Path);
            if (File.Exists(path))
                return path;

            var pdPath = CustomConfigInjector.GetProgramDataPath(entry.Name);
            if (File.Exists(pdPath))
                return pdPath;
        }

        // Fallback: single custom_config path (backward compat)
        return Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
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

        // v2.21.9: platform-aware bundled profiles. Previously BuildProfileSources
        // always loaded default.json (Windows layout with .exe process names
        // and group names like "Discord_Privacy" / "Work_Suite" / "Browsers" /
        // "Terminal"). On Linux SettingsLoader + MainWindowViewModel already
        // route to default-linux.json for UI load/display, but this engine
        // path still pulled default.json at runtime — so Apply ran with the
        // WRONG profile catalogue. User hit "Profile 'Messengers' not found"
        // because the UI offered Linux-style profiles but the engine only
        // knew Windows-style ones.
        //
        // Now we prefer the platform-specific variant if it exists, and
        // fall back to default.json so Windows builds keep working.
        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));

        // Generic default.json always added as a fallback at slightly lower
        // priority so profiles referenced by BOTH files (e.g. SimpleSplit's
        // Browsers + Discord_Privacy + Work_Suite) resolve against the
        // platform variant first.
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));

        // User profiles directory under AppPaths (where ProfilesDir lives)
        var platformProfiles = Path.Combine(AppPaths.ProfilesDir, platformDefaultName);
        if (File.Exists(platformProfiles))
            sources.Add(new LocalProfileSource(platformProfiles, 85));
        var userDefault = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userDefault) && !userDefault.Equals(platformProfiles, StringComparison.Ordinal))
            sources.Add(new LocalProfileSource(userDefault, 83));

        // Built-in fallback
        sources.Add(new BuiltInProfileSource());
        return sources;
    }
}
