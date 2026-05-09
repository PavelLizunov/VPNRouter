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

    /// <summary>
    /// Fingerprint of TUN-layer settings taken at the last successful
    /// start/restart. Compared against the incoming AppSettings on every
    /// ApplyAsync — any mismatch escalates to <c>forceRestart = true</c>
    /// because TUN interface changes (name / IP / MTU / auto_route /
    /// strict_route / route_exclude_address) can't be re-laid by sing-box's
    /// Clash API hot-reload; the adapter has to be destroyed and recreated.
    ///
    /// <para>v2.27.2: introduced alongside RoutingMode auto-detect (v2.27.1)
    /// as part of the "structural change self-heal" family. Callers should
    /// NEVER need to manually pass <c>forceRestart = true</c> — the engine
    /// figures it out.</para>
    /// </summary>
    internal string TunFingerprint { get; private set; } = string.Empty;

    // ─── Events for UI ───────────────────────────────────────────────────────

    /// <summary>Fired when engine status changes (e.g. "Loading profiles...", "sing-box started")</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fired when a targeted process is detected by ETW</summary>
    public event Action<string, int>? ProcessDetected;

    /// <summary>Fired on sing-box restart attempt (attemptNumber, maxAttempts)</summary>
    public event Action<int, int>? RestartAttempted;

    /// <summary>Fired on validation warnings</summary>
    public event Action<string>? Warning;

    /// <summary>Fired after every successful sing-box launch (initial start
    /// AND any restart). Consumers use this to keep persisted state (CLI
    /// state.json, GUI banner PID, doctor lockfile) in sync with the live
    /// process — without it, stop/status commands race against stale PIDs
    /// the moment HealthMonitor does its first restart.</summary>
    public event Action<int>? SingBoxStarted;

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
        // Capture the TUN fingerprint as the baseline for later Apply calls.
        // Any subsequent mismatch forces a full process restart so the TUN
        // adapter and its route table are laid fresh from the new settings.
        TunFingerprint = ComputeTunFingerprint(settings.Tun);

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
            // v2.32.x F-12 (parity audit P0, 2026-05-09): defense-in-depth
            // backstop for silent ConfigMode flips. Before doing any further
            // work, assert that the AppSettings invariants hold: ConfigMode
            // is consistent with the actual configured state. This catches
            // the v2.28.2 silent-leak class of bug at the model level — if
            // a future UI change re-introduces a silent flip, this throws
            // here instead of generating a leaky sing-box config.
            var pregenValidation = LeakProtection.ValidateAppSettings(settings);
            if (!pregenValidation.IsValid)
            {
                var msg = string.Join(" ", pregenValidation.Errors);
                _logger?.Error(
                    "[VpnEngine] AppSettings invariant violation pre-generation: {Errors}",
                    msg);
                throw new InvalidOperationException(msg);
            }

            // v2.28.2: route through the shared resolver so subscribe-mode
            // servers in App.Subscriptions[].Servers are picked up here even
            // if the GUI's MainWindowViewModel pre-aggregation didn't run
            // (e.g. CLI startup, autostart-before-LoadSettings race, hot
            // reload). Single source of truth — same code path as Apply().
            var allServers = VlessServersResolver.Resolve(settings, _logger);

            if (allServers.Count == 0)
            {
                var why = VlessServersResolver.DescribeEmptyReason(settings)
                          ?? "VLESS server not configured.";
                throw new InvalidOperationException(why);
            }

            // Show the active server's IP (what actually runs), not just [0]
            var activeServers = settings.Vless.GetActiveServers();
            ActiveServerAddress = activeServers.Count > 0 ? activeServers[0].Server : allServers[0].Server;
        }

        ct.ThrowIfCancellationRequested();

        // 2. Load profiles
        OnStatus("Loading profiles...");
        // v2.22.4 self-healing step: detect + quarantine a stale
        // user-level catalogue at %ProgramData%\VPNRouter\profiles\default.json
        // (or ~/.config/vpnrouter/profiles/default.json on Unix) BEFORE
        // building the sources list.
        QuarantineStaleUserCatalogue(_logger);

        // v2.23.0: safe-mode forces bundled-only catalogue. Even if
        // the quarantine heuristic kept a user catalogue it's skipped
        // when --safe is set. Purpose: let users recover when ANY
        // user-facing override is wrong, not just stale schema.
        var sources = SafeMode.Enabled
            ? BuildBundledOnlyProfileSources()
            : BuildProfileSources(settings);
        if (SafeMode.Enabled)
            _logger?.Warning("[VpnEngine] Safe mode — using bundled profiles only, ignoring user overrides");
        var manager = new ProfileManager(sources, _logger);
        var collection = await manager.LoadAsync(ct);

        // v2.23.0: safe mode skips all user-level customization below.
        // Custom apps / custom categories / user-group additions are
        // exactly the kind of things that can contain malformed data
        // and break startup — the whole point of safe mode is to let
        // the user get to a working app.
        if (SafeMode.Enabled)
        {
            _logger?.Warning("[VpnEngine] Safe mode — skipping custom apps / categories / group-apps merge");
        }

        // v2.31.6-r10 (Phase F): merge user-added apps into default groups
        // (custom_group_apps) + inject user-created categories
        // (custom_categories) as new profiles. Logic was previously
        // duplicated ~50 LOC verbatim between this StartAsync path and
        // ApplyAsync (drift risk between the two — silent leak class
        // of bug). Extracted to MergeUserCustomization. SafeMode guard
        // stays at this call site since StartAsync's pre-existing
        // semantics respect safe mode (ApplyAsync historically did
        // not — preserving that asymmetry to avoid behavioural change).
        if (!SafeMode.Enabled)
        {
            MergeUserCustomization(collection, settings);
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

        // v2.23.0: safe mode forces full-tunnel regardless of user setting.
        // If the user's split-mode catalogue is what's breaking startup,
        // we still want the app to come up — and full tunnel is the
        // always-works fallback.
        if (SafeMode.Enabled)
        {
            _logger?.Warning("[VpnEngine] Safe mode — forcing full-tunnel routing");
            isFullTunnel = true;
        }

        // v2.22.3: in full-tunnel mode all traffic goes through the proxy
        // regardless of process. Resolving ActiveProfile + scanning its
        // hundreds of processes just wastes time (and on Windows can hang
        // for minutes if the profile catalogue has a pathological entry —
        // user hit this when upgrading had left a stale default.json in
        // %ProgramData%\VPNRouter\profiles\ that didn't match the new
        // schema). Collapse to the empty FullTunnel profile unconditionally.
        if (isFullTunnel)
        {
            _logger?.Information(
                "[VpnEngine] Full-tunnel mode — ignoring ActiveProfile '{Profile}' and skipping process scan",
                profileName ?? "(empty)");
            _activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }
        else if (!string.IsNullOrEmpty(profileName))
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
                        // CRITICAL — v2.30.0-r8: do NOT persist `settings`
                        // directly here. By this point in StartAsync,
                        // VlessServersResolver.Resolve has already mutated
                        // settings.Vless.Servers in-place with the aggregated
                        // subscription server list (subscribe mode). Saving
                        // this object would write that aggregate into
                        // vless.servers in YAML, which on next app launch
                        // resurfaces as fake "manual VLESS servers" in the
                        // VLESS tab.
                        // User report 2026-04-29 (Linux): «конфиги из подписки
                        // закинулись не в Подписки а во Vless». Linux happens
                        // to hit this more often because bundled profile
                        // names differ across platforms (default-linux.json
                        // etc.), increasing the chance the "missing profile"
                        // sanitizer fires and persists.
                        // Fix: reload a fresh copy from YAML (untouched by
                        // the resolver), mutate ONLY ActiveProfile, save.
                        var fresh = SettingsLoader.Load(AppPaths.ConfigYamlPath);
                        fresh.ActiveProfile = sanitized;
                        SettingsLoader.Save(fresh);
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

        // 4. Scan processes. v2.22.4 self-healing: wrap in a 30s timeout.
        // WMI child-lookup on a corrupt catalogue or an overloaded WMI
        // subsystem used to hang forever, freezing startup. If the scan
        // doesn't return, continue with an empty list — VPN still starts,
        // split mode just routes nothing (user sees a warning and can
        // toggle Full mode or clean up the catalogue).
        OnStatus("Scanning processes...");
        _scanResult = null;
        try
        {
            var scanTask = Task.Run(() => _scanner.ScanForProfile(_activeProfile), ct);
            if (scanTask.Wait(TimeSpan.FromSeconds(30), ct))
            {
                _scanResult = scanTask.Result;
            }
            else
            {
                _logger?.Warning(
                    "[VpnEngine] Process scan timed out after 30s — continuing with empty list. " +
                    "Check %ProgramData%\\VPNRouter\\profiles\\ for corrupt entries, or switch to Full tunnel mode.");
                Warning?.Invoke(
                    "Process scan timed out — split mode may not route correctly. " +
                    "Switch to Full mode or reset your catalogue.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[VpnEngine] Process scan failed — continuing with empty list");
            Warning?.Invoke($"Process scan error: {ex.Message}");
        }
        _scanResult ??= new ScanResult { ProcessNames = new List<string>(), ScannedAt = DateTime.Now };
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

        // Note (v2.31.9-r4): the VpnEngine-side
        // TunAdapterDiagnostics.EnsureAdapterEnabledOrAbsent call moved
        // INTO SingBoxManager.LaunchProcess so EVERY start path (user
        // Start, Apply hot-reload-fallback, HealthMonitor recovery)
        // covers it. Keeping a single chokepoint avoids the brat-
        // 2026-05-05 silent-FATAL where Apply restart bypassed the
        // pre-enable.

        _singBox = new SingBoxManager(settings.SingBox, _logger);
        // Re-emit every successful launch (initial + any restart) so consumers
        // can keep persisted PID state in sync — the reason status/stop don't
        // race against stale PIDs after HealthMonitor restarts.
        _singBox.Started += pid => SingBoxStarted?.Invoke(pid);
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
        // v2.31.6-r8: capture _singBox.Pid into a local before the Task.Run so a
        // racing Stop() that nulls _singBox between this point and the lambda
        // body running can't NRE on `_singBox.Pid` at the OnStatus call. The
        // lambda is fire-and-forget and may execute after the user has already
        // initiated a quick disconnect.
        var pidSnapshot = _singBox.Pid;
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
                    OnStatus($"Connected (PID {pidSnapshot})");
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger?.Debug("[VpnEngine] Warm-up attempt {Attempt}: {Error}",
                        attempt, ex.GetType().Name);
                }
            }
            _logger?.Warning("[VpnEngine] TUN warm-up failed after {Ms}ms", sw.ElapsedMilliseconds);
            OnStatus($"Connected (PID {pidSnapshot})");
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

            // v2.31.6-r10 (Phase F): consolidated user-customization merge.
            // ApplyAsync historically called this without a SafeMode guard
            // (StartAsync skips when safe mode is on; ApplyAsync didn't —
            // preserving that asymmetry, see helper doc-comment).
            MergeUserCustomization(collection, settings);

            // Resolve active profile (tolerant — v2.22.0-r1)
            var profileName = settings.ActiveProfile;
            var isFullTunnel = (settings.App.RoutingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase);
            var isCustomConfig = settings.App.ConfigMode?.Equals("custom", StringComparison.OrdinalIgnoreCase) == true;

            // v2.22.3: same short-circuit as StartAsync — full tunnel ignores
            // profile and its process scan entirely. Avoids re-scanning on
            // every Apply when user toggles unrelated settings.
            if (isFullTunnel)
            {
                _activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
            }
            else if (!string.IsNullOrEmpty(profileName))
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

            // v2.31.8-r4: snapshot the previous process set before the
            // re-scan overwrites _scanResult. Used by the structural-change
            // detection below to escalate to forceRestart when the process
            // list changed — Clash API hot-reload only swaps the in-memory
            // config; established TCP connections from already-running apps
            // keep their original outbound (direct or proxy) and only NEW
            // sockets observe the new rules. User report: «работаю в split
            // tunnel, меняю список приложений, нажимаю применить — VPN
            // route не подхватывается, надо stop+start». Forcing a full
            // restart tears down the TUN adapter so existing TCP sockets
            // get cancelled, and apps reconnect under the new rules.
            var oldProcessSet = (_scanResult?.ProcessNames ?? new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                // v2.28.2: validate custom JSON before injecting. StartAsync did
                // this on initial start, but Apply never did. If a user edits
                // their custom config to something broken (no proxy outbound,
                // no outbounds array, malformed JSON), Apply would silently
                // produce an injected config with route rules pointing at a
                // missing "proxy" tag — same class of bug as the empty
                // Vless.Servers leak fixed in 14ec5da, just for custom mode.
                var (customValid, customErrs) = CustomConfigInjector.Validate(rawJson);
                if (!customValid)
                {
                    _logger?.Warning(
                        "[VpnEngine] Apply: custom config invalid, skipping reload — {Errors}",
                        string.Join("; ", customErrs));
                    return false;
                }

                configJson = CustomConfigInjector.Inject(rawJson, _scanResult.ProcessNames, settings);
            }
            else
            {
                // v2.32.x F-12 (parity audit P0, 2026-05-09): same pre-gen
                // invariant check as StartAsync. Catches a silent ConfigMode
                // flip that may have leaked in between StartAsync and Apply
                // — e.g. user toggling sub-tabs, hot-reload from
                // settings-page change, autostart-after-config-edit.
                var pregenValidation = LeakProtection.ValidateAppSettings(settings);
                if (!pregenValidation.IsValid)
                {
                    var msg = string.Join(" ", pregenValidation.Errors);
                    _logger?.Warning(
                        "[VpnEngine] Apply: AppSettings invariant violation, skipping reload — {Errors}",
                        msg);
                    return false;
                }

                // v2.28.2 critical fix: ROOT CAUSE of the v2.28.1 field-test
                // "flow mismatch: expected xtls-rprx-vision but got none"
                // server-log spam (249 errors/day from one machine).
                //
                // Apply was reading fresh settings.Vless.Servers straight
                // from disk (always [] in subscribe mode — subscription
                // servers live in App.Subscriptions[].Servers, not Vless.
                // Servers) and calling ConfigGenerator with an empty list.
                // That produced sing-box JSON with route rules pointing
                // at a "proxy" outbound that was never emitted, so all
                // traffic silently went out direct AND sing-box still
                // urltest-probed the upstream server with no VLESS
                // handshake — the source of the server-side errors.
                //
                // Resolve() mutates settings.Vless.Servers in place so the
                // downstream ConfigGenerator picks up the correct list.
                // Same call as StartAsync — single source of truth.
                var resolved = VlessServersResolver.Resolve(settings, _logger);
                if (resolved.Count == 0)
                {
                    var why = VlessServersResolver.DescribeEmptyReason(settings)
                              ?? "VLESS server not configured.";
                    _logger?.Warning("[VpnEngine] Apply: skipping config regen — {Reason}", why);
                    return false;
                }

                var sbConfig = ConfigGenerator.Generate(_activeProfile!, _scanResult.ProcessNames, settings);

                // v2.28.2: defense-in-depth — Apply now validates the regenerated
                // config the same way StartAsync does. Previously skipped here,
                // which is how the v2.28.1 broken-config-with-no-proxy-outbound
                // ever got past Apply at all (LeakProtection.ValidateConfig
                // catches missing proxy outbound on line 67-69, but it was only
                // being run in StartAsync, not Apply).
                var validation = LeakProtection.ValidateConfig(sbConfig);
                foreach (var warn in validation.Warnings)
                    _logger?.Warning("[VpnEngine] Apply: {Warn}", warn);

                if (!validation.IsValid)
                {
                    var errs = string.Join("; ", validation.Errors);
                    _logger?.Warning("[VpnEngine] Apply: validation failed, skipping reload — {Errors}", errs);
                    return false;
                }

                configJson = ConfigGenerator.Serialize(sbConfig);
            }

            // v2.27.1 — auto-detect structural changes that hot-reload CAN'T
            // pick up. Observed in the wild: a user flipped RoutingMode from
            // split → full via the UI, hot-reload accepted the new config
            // (reported "succeeded HTTP 204"), sing-box internally updated
            // route.final to "proxy"... but the TUN adapter kept the old
            // routes populated by kernel/Windows from the split-tunnel pass.
            // VM traffic on the host stayed direct until the user fully
            // stopped the VPN + uninstalled the service. Hot-reload is
            // config-layer only; any change that requires re-laying TUN
            // routes has to bounce the process.
            //
            // The comment at the old call-site warned about this but the
            // code didn't enforce it — escalating `forceRestart = true`
            // here makes the invariant self-healing: callers never need
            // to remember which setting changes are structural.
            var newRoutingMode = (settings.App.RoutingMode ?? "split").ToLowerInvariant();
            if (!string.Equals(newRoutingMode, ActiveRoutingMode, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Information(
                    "[VpnEngine] RoutingMode change detected ({Old} → {New}) — escalating to full restart so TUN routes are re-laid",
                    ActiveRoutingMode, newRoutingMode);
                forceRestart = true;
            }

            // v2.27.2: TUN-layer structural change detection. Same pattern as
            // RoutingMode above — Clash API hot-reload ACCEPTS the config
            // silently but the live TUN adapter keeps its old interface
            // name / IP / MTU / route-exclude list. Any mismatch here
            // means we need to tear the adapter down and rebuild.
            //
            // Covers:
            //   - Tun.InterfaceName    (rare, but user-visible in netsh)
            //   - Tun.Ipv4Address      (changing the TUN subnet re-lays routes)
            //   - Tun.Mtu              (kernel-level adapter property)
            //   - Tun.AutoRoute        (auto route table installation toggle)
            //   - Tun.StrictRoute      (block direct on non-TUN — critical for
            //                           leak protection; hot-reload ignores it)
            //   - Tun.Ipv6Enabled      (stack toggle, adapter-level)
            //   - Tun.RouteExcludeAddress (v2.20 AmneziaWG coexistence — the
            //                              exclude list is written into the
            //                              adapter's kernel route table at
            //                              creation; Clash API doesn't re-run
            //                              the installer)
            var newTunFingerprint = ComputeTunFingerprint(settings.Tun);
            if (!string.Equals(newTunFingerprint, TunFingerprint, StringComparison.Ordinal))
            {
                _logger?.Information(
                    "[VpnEngine] TUN settings change detected — escalating to full restart. Old fingerprint {Old}, new {New}",
                    TunFingerprint, newTunFingerprint);
                forceRestart = true;
            }

            // v2.31.8-r4: detect process list mutations that hot-reload can't
            // honour for ALREADY-OPEN sockets. Adding an app to the split-
            // tunnel list while that app is currently running with established
            // TCP connections to the internet — hot-reload accepts the new
            // route rule (HTTP 204), sing-box's matcher knows the new rule,
            // but the existing connections were routed at SYN-time according
            // to the old rules and stay on that path until the app closes
            // and re-opens them. End-user perception: «нажал применить, но
            // ничего не изменилось — пришлось stop+start». Forcing a full
            // restart cancels all in-flight TCP via the TUN tear-down so
            // apps reconnect under the new rules immediately.
            var newProcessSet = (_scanResult?.ProcessNames ?? new List<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!oldProcessSet.SetEquals(newProcessSet))
            {
                var added = newProcessSet.Except(oldProcessSet).ToList();
                var removed = oldProcessSet.Except(newProcessSet).ToList();
                _logger?.Information(
                    "[VpnEngine] Process list change detected (+{AddedCount}: {Added} / -{RemovedCount}: {Removed}) — escalating to full restart so existing TCP connections rejoin under new rules",
                    added.Count, string.Join(",", added),
                    removed.Count, string.Join(",", removed));
                forceRestart = true;
            }

            // Try hot-reload first, UNLESS the caller explicitly asked for
            // a full restart (v2.20.4 + v2.27.1 auto-detect above).
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

            // v2.31.7-r1: pass through forceRestart so the structural-change
            // intent actually reaches sing-box. Pre-r1 ReloadConfigJson always
            // ran TryHotReload() first regardless of caller intent — when
            // hot-reload happened to succeed (HTTP 204), the kill+restart
            // path never ran and TUN routes for the new RoutingMode were
            // never laid. Caught in brat-2026-05-04 16:17:32 logs (split →
            // full switch where PID stayed the same despite the «Forced full
            // restart» log line).
            _singBox.ReloadConfigJson(configJson, forceRestart);
            // Update cached trackers post-restart so subsequent Apply calls
            // see the new baseline for both RoutingMode and TUN fingerprint.
            ActiveRoutingMode = newRoutingMode;
            TunFingerprint = newTunFingerprint;
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

        // v2.27.2 — passive diagnostic: log TUN adapter state *after* a
        // graceful stop. If the adapter persists in netsh after
        // sing-box has exited cleanly, we've got a wintun/sing-box
        // driver-level leak. Useful for correlating with user reports
        // like "after heavy toggling, TUN adapter 'sticks' and a new
        // start either fails or reuses stale routes".
        try
        {
            if (OperatingSystem.IsWindows())
                TunAdapterDiagnostics.LogAdapterState(_logger, "VpnEngine.after-stop");
        }
        catch { /* diagnostics must never throw */ }

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

    /// <summary>
    /// Build a stable fingerprint of TUN-layer settings for change detection
    /// between StartAsync and ApplyAsync. Any field here that changes means
    /// the adapter needs to be rebuilt — Clash API hot-reload CANNOT re-lay
    /// kernel-level TUN properties.
    ///
    /// <para>Exposed as <c>internal</c> (not public) because tests live in
    /// the Tests project which has InternalsVisibleTo via friend assembly
    /// attribute. Production callers should compare
    /// <see cref="TunFingerprint"/> values instead of recomputing.</para>
    /// </summary>
    internal static string ComputeTunFingerprint(Models.TunSettings tun)
    {
        // Order-independent join of the exclude list — users might reorder
        // entries in the UI without any structural change intended.
        var excludes = tun.RouteExcludeAddress ?? new List<string>();
        var excludeKey = string.Join(",",
            excludes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .OrderBy(s => s, StringComparer.Ordinal));

        return string.Join("|",
            (tun.InterfaceName ?? "").Trim().ToLowerInvariant(),
            (tun.Ipv4Address ?? "").Trim().ToLowerInvariant(),
            tun.Ipv6Enabled ? "1" : "0",
            tun.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tun.AutoRoute ? "1" : "0",
            tun.StrictRoute ? "1" : "0",
            excludeKey);
    }

    /// <summary>
    /// v2.31.6-r10 (Phase F) — consolidated user-customization merge step.
    /// Mutates <paramref name="collection"/> in place by:
    ///
    /// <list type="bullet">
    ///   <item>Adding entries from <c>settings.CustomGroupApps</c> as
    ///   ProcessRule items on existing profiles whose name matches the
    ///   group key (case-insensitive). Skips dupes — a name already
    ///   present in the profile's Processes is left alone.</item>
    ///   <item>Injecting entries from <c>settings.CustomCategories</c>
    ///   as new <see cref="Profile"/> instances appended to the
    ///   collection. Skips categories whose Name collides with an
    ///   existing profile (the existing profile wins; user can rename
    ///   in CustomCategories to disambiguate).</item>
    /// </list>
    ///
    /// <para>App names are normalised to <c>foo.exe</c> form (extension
    /// added if missing) — sing-box's <c>process_name</c> matcher is
    /// case-sensitive but extension-aware, and this is the canonical
    /// shape the rest of the pipeline uses.</para>
    ///
    /// <para><b>SafeMode handling</b>: this helper does NOT itself
    /// check <see cref="SafeMode.Enabled"/>. The caller decides:
    /// <see cref="StartAsync"/> wraps the call in
    /// <c>if (!SafeMode.Enabled)</c> (so safe mode bypasses customization
    /// at boot — the entire point of safe mode). <see cref="ApplyAsync"/>
    /// calls unconditionally — preserving the pre-r10 asymmetry where
    /// a hot-reload Apply still merges customization even when a
    /// previous safe-mode boot would have skipped it. If we ever decide
    /// Apply should also respect safe mode, that's a separate
    /// behavioural change.</para>
    ///
    /// <para>Pre-r10 this body was duplicated ~50 LOC verbatim across
    /// StartAsync and ApplyAsync — a silent-leak class of bug if the
    /// two ever drifted (and they did briefly drift in v2.28.2's
    /// initial fix attempt, caught by the regression suite). The
    /// extraction makes the merge behaviour single-sourced.</para>
    /// </summary>
    internal static void MergeUserCustomization(
        ProfileCollection collection,
        AppSettings settings)
    {
        // Merge user-added apps into existing default groups.
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

        // Inject user-created categories as new profiles.
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

    /// <summary>
    /// Safe-mode variant: only the bundled catalogue next to the exe,
    /// then hard-coded BuiltInProfiles. No user-level file, no yaml
    /// ProfileSources. Guarantees we never touch a potentially broken
    /// user override when the user has chosen --safe.
    /// </summary>
    private static List<IProfileSource> BuildBundledOnlyProfileSources()
    {
        var sources = new List<IProfileSource>();
        var appDir = AppContext.BaseDirectory;
        var platformDefaultName = OperatingSystem.IsMacOS() ? "default-macos.json"
                                : OperatingSystem.IsLinux() ? "default-linux.json"
                                : "default.json";

        var platformBundled = Path.Combine(appDir, "profiles", platformDefaultName);
        if (File.Exists(platformBundled))
            sources.Add(new LocalProfileSource(platformBundled, 80));
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 78));
        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    // ─── v2.22.4 self-healing: stale user-catalogue quarantine ──────────

    private static readonly string[] ExpectedV222Groups =
    {
        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
    };

    /// <summary>
    /// Rename a user-level <c>profiles/default.json</c> that doesn't match
    /// the v2.22 group schema. Triggered at start of every StartAsync so
    /// stale catalogues from older installs can't deadlock ProcessScanner.
    /// Errors are swallowed — never fatal.
    /// </summary>
    private static void QuarantineStaleUserCatalogue(ILogger? logger)
    {
        try
        {
            var userPath = Path.Combine(AppPaths.ProfilesDir, "default.json");
            if (!File.Exists(userPath)) return;

            bool shouldQuarantine = false;
            string reason = "";

            try
            {
                var json = File.ReadAllText(userPath);
                var collection = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileCollection>(json);
                if (collection == null || collection.Profiles == null || collection.Profiles.Count == 0)
                {
                    shouldQuarantine = true;
                    reason = "empty or unparseable";
                }
                else
                {
                    var present = new HashSet<string>(
                        collection.Profiles.Select(p => p.Name),
                        StringComparer.OrdinalIgnoreCase);
                    var missing = ExpectedV222Groups.Count(g => !present.Contains(g));
                    // Heuristic: if ≥3 of 8 standard v2.22 groups absent,
                    // this is an older-schema catalogue. Just one or two
                    // missing — could be legitimate user customization.
                    if (missing >= 3)
                    {
                        shouldQuarantine = true;
                        reason = $"{missing} of {ExpectedV222Groups.Length} standard groups missing";
                    }
                }
            }
            catch (Exception ex)
            {
                shouldQuarantine = true;
                reason = $"parse error: {ex.Message}";
            }

            if (!shouldQuarantine) return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{userPath}.migrated-{stamp}";
            File.Move(userPath, backup);
            logger?.Warning(
                "[VpnEngine] Quarantined stale user catalogue {Path} ({Reason}) → {Backup}. Using bundled defaults.",
                userPath, reason, backup);
        }
        catch (Exception ex)
        {
            // Quarantine is best-effort. If we can't even rename it,
            // let it through — the catalogue load will either succeed or
            // skip to fallback sources naturally.
            logger?.Warning(ex, "[VpnEngine] Could not quarantine stale user catalogue");
        }
    }
}
