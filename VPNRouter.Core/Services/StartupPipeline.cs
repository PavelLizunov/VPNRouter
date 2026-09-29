#nullable enable
using System.IO;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed record StartupContext(
    AppSettings Settings,
    StartupMode Mode,
    bool SkipVpnConflictCheck = false);

public sealed record StartupResult(
    bool Success,
    bool EarlyReturn,
    int? ProcessId,
    TimeSpan Duration,
    string? ConfigJson = null,
    Profile? Profile = null);

public enum StartupMode
{
    ColdStart,

    HotReload,

    AutoFailover,
}

internal interface IStartupHost
{
    ILogger? Logger { get; }

    IProcessScanner Scanner { get; }

    Func<IFirewallManager> FirewallFactory { get; }

    Func<IProcessMonitor> MonitorFactory { get; }

    void OnStatus(string message);

    void OnWarning(string message);

    void OnSingBoxStarted(int pid);

    void OnConnected(int pid);

    bool IsCurrentStart(int pid);

    void OnRestartAttempted(int attempt, int max);

    void OnFailoverRequested(string reason);

    void OnAutoFailoverTriggered(string message);

    void OnProcessDetected(string name, int pid);

    void SetActiveServerAddress(string address);

    void SetActiveModes(string configMode, string routingMode, string tunFingerprint);

    void SetActiveProfile(Profile profile);

    void SetScanResult(ScanResult result);

    void SetSingBoxManager(SingBoxManager manager);

    void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings);

    void SetFirewallManager(IFirewallManager firewall);

    void SetProcessMonitor(IProcessMonitor etw);

    void SetHealthMonitor(HealthMonitor monitor);

    void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck);

    AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck);

    AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck);

    void SchedulePostStartProbe(
        AppSettings settings,
        ConfigSanityCheck sanityCheck,
        CancellationToken ct);
}

internal sealed class StartupPipeline
{
    private readonly IStartupHost _host;
    private readonly ISettingsStore _store;
    private readonly IWindowsDnsHardening _dnsHardening;

    public static IHttpClient? WarmupHttp;

    public StartupPipeline(
        IStartupHost host,
        ISettingsStore? store = null,
        IWindowsDnsHardening? dnsHardening = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _store = store ?? RealSettingsStore.Instance;
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;
    }

    public async Task<StartupResult> ExecuteAsync(
        StartupContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var settings = context.Settings;

        if (context.Mode != StartupMode.HotReload)
        {
            PreflightConflictAndDns(settings, context.SkipVpnConflictCheck);
            await PreflightGeoDataAsync(settings, ct);
        }

        var (profile, isCustomConfig, customConfigJson) =
            await ResolveProfileAndServersAsync(settings, context.Mode, ct);

        var scanResult = await ScanProcessesPhaseAsync(profile, settings, ct).ConfigureAwait(false);

        Func<VlessServerEntry, bool>? isServerAlive = context.Mode == StartupMode.HotReload
            ? null
            : await TryProbeUdpForNaivePairingAsync(settings, ct).ConfigureAwait(false);

        var configJson = GenerateConfigPhase(
            profile,
            scanResult,
            settings,
            isCustomConfig,
            customConfigJson,
            context.Mode,
            isServerAlive);

        ct.ThrowIfCancellationRequested();

        if (context.Mode == StartupMode.HotReload)
        {
            return new StartupResult(
                Success: true,
                EarlyReturn: false,
                ProcessId: null,
                Duration: sw.Elapsed,
                ConfigJson: configJson,
                Profile: profile);
        }

        if (context.Mode == StartupMode.ColdStart && !isCustomConfig)
        {
            var earlyReturn = await PreStartChecksPhaseAsync(
                settings, configJson, ct);
            if (earlyReturn)
            {
                return new StartupResult(
                    Success: true,
                    EarlyReturn: true,
                    ProcessId: null,
                    Duration: sw.Elapsed);
            }
        }

        await DeployAndSetupFirewallPhaseAsync(
            settings, profile, scanResult, ct);

        ct.ThrowIfCancellationRequested();

        var activeForTransport = settings.Vless?.GetActiveServers() ?? new List<VlessServerEntry>();
        if (activeForTransport.Count > 0 &&
            string.Equals(activeForTransport[0].Protocol, "dns-tunnel", StringComparison.OrdinalIgnoreCase))
        {
            _host.StartDnsTunnelTransport(activeForTransport[0], settings);
            ct.ThrowIfCancellationRequested();
        }

        var pid = await StartSingBoxPhaseAsync(
            settings, configJson, isCustomConfig, ct);

        ct.ThrowIfCancellationRequested();

        var firewall = (_host as StartupHostInternal)?.Firewall;
        if (firewall is ICommittedFirewallConfig committedFirewall)
        {
            var isFullTunnel = (settings.App.RoutingMode ?? "split")
                .Equals("full", StringComparison.OrdinalIgnoreCase);
            committedFirewall.UpdateCommittedConfig(configJson, profile.BlockOnVpnFail && isFullTunnel);
        }

        StartMonitorsPhase(settings, profile, scanResult);

        _host.OnStatus("VPN Router is running");

        return new StartupResult(
            Success: true,
            EarlyReturn: false,
            ProcessId: pid,
            Duration: sw.Elapsed);
    }

    private void PreflightConflictAndDns(AppSettings settings, bool skipVpnConflictCheck)
    {
        AppPaths.EnsureDirectories();

        if (!skipVpnConflictCheck)
        {
            var conflicts = ConflictingVpnDetector.DetectConflictingVpnProcesses(_host.Logger);
            if (conflicts.Count > 0)
            {
                var first = conflicts[0];
                throw new ConflictingVpnException(
                    conflicts,
                    $"Another VPN client is running: {first.ProcessName} (PID {first.Pid}). " +
                    $"Only one VPN can hold the TUN adapter at a time. " +
                    $"Stop {first.ProcessName} before launching VPNRouter.");
            }

            var coexisting = ConflictingVpnDetector.DetectCoexistingVpnProcesses(_host.Logger);
            if (coexisting.Count > 0)
            {
                var c = coexisting[0];
                _host.Logger?.Warning(
                    "[StartupPipeline] Coexisting VPN detected: {Name} (PID {Pid}) — VPNRouter " +
                    "excludes its subnet from TUN routing so they run side-by-side; proceeding. " +
                    "If routed apps lose internet, stop it and reconnect.",
                    c.ProcessName, c.Pid);
            }
        }
        else
        {
            _host.Logger?.Information(
                "[StartupPipeline] Skipping conflicting-VPN pre-flight check (user opt-in)");
        }

        if (settings.App.FlushDnsOnStart)
            DnsFlusher.Flush(_host.Logger);
    }

    private async Task PreflightGeoDataAsync(AppSettings settings, CancellationToken ct)
    {
        if (settings.App.BypassRussianTraffic && !GeoDataDownloader.AreGeoFilesAvailable())
        {
            _host.OnStatus("Downloading geo data...");
            try
            {
                var downloader = new GeoDataDownloader(_host.Logger);
                var ok = await downloader.EnsureGeoFilesAsync(ct);
                if (!ok)
                    _host.Logger?.Warning(
                        "[StartupPipeline] Geo data download failed — RU bypass will be disabled for this session");
            }
            catch (Exception ex)
            {
                _host.Logger?.Warning(ex,
                    "[StartupPipeline] Geo data download error — RU bypass will be disabled");
            }
        }
    }

    private async Task<(Profile profile, bool isCustom, string? rawCustomJson)>
        ResolveProfileAndServersAsync(
            AppSettings settings,
            StartupMode mode,
            CancellationToken ct)
    {
        var isCustomConfig = (settings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);
        var activeConfigMode = isCustomConfig ? "custom" : "generated";
        var activeRoutingMode = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase) ? "full" : "split";
        var tunFingerprint = VpnEngine.ComputeTunFingerprint(settings.Tun);

        _host.SetActiveModes(activeConfigMode, activeRoutingMode, tunFingerprint);

        string? rawCustomJson = null;

        if (isCustomConfig)
        {
            var customPath = VpnEngine.ResolveCustomConfigPath(settings);
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                throw new InvalidOperationException(
                    $"Custom config not found: {customPath}. Add a config in the Servers tab.");

            rawCustomJson = File.ReadAllText(customPath);
            var (isValid, errors) = CustomConfigInjector.Validate(rawCustomJson);
            if (!isValid)
                throw new InvalidOperationException(
                    $"Custom config validation failed: {string.Join("; ", errors)}");

            try
            {
                var (_, srv) = CustomConfigInjector.ParseConfigInfo(rawCustomJson);
                _host.SetActiveServerAddress(srv);
            }
            catch { _host.SetActiveServerAddress(""); }
        }
        else
        {
            var pregenValidation = LeakProtection.ValidateAppSettings(settings);
            if (!pregenValidation.IsValid)
            {
                var msg = string.Join(" ", pregenValidation.Errors);
                _host.Logger?.Error(
                    "[StartupPipeline] AppSettings invariant violation pre-generation: {Errors}",
                    msg);
                throw new InvalidOperationException(msg);
            }

            var allServers = VlessServersResolver.Resolve(settings, _host.Logger);
            if (allServers.Count == 0)
            {
                var why = VlessServersResolver.DescribeEmptyReason(settings)
                          ?? "VLESS server not configured.";
                throw new InvalidOperationException(why);
            }

            var activeServers = settings.Vless.GetActiveServers();
            _host.SetActiveServerAddress(
                activeServers.Count > 0
                    ? activeServers[0].Server
                    : allServers[0].Server);
        }

        ct.ThrowIfCancellationRequested();

        _host.OnStatus("Loading profiles...");
        VpnEngine.QuarantineStaleUserCatalogue(_host.Logger);

        var sources = SafeMode.Enabled
            ? VpnEngine.BuildBundledOnlyProfileSources()
            : VpnEngine.BuildProfileSources(settings);
        if (SafeMode.Enabled)
            _host.Logger?.Warning(
                "[StartupPipeline] Safe mode — using bundled profiles only, ignoring user overrides");

        var manager = new ProfileManager(sources, _host.Logger);
        var collection = await manager.LoadAsync(ct);

        if (SafeMode.Enabled)
            _host.Logger?.Warning(
                "[StartupPipeline] Safe mode — skipping custom apps / categories / group-apps merge");
        if (!SafeMode.Enabled)
            VpnEngine.MergeUserCustomization(collection, settings);

        _host.Logger?.Information(
            "[StartupPipeline] Loaded profile catalogue ({Count}): {Names}",
            collection.Profiles.Count,
            string.Join(", ", collection.Profiles.Select(p => p.Name)));

        ct.ThrowIfCancellationRequested();

        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        var profileName = settings.ActiveProfile;

        if (string.IsNullOrEmpty(profileName) && !isFullTunnel && !isCustomConfig)
            throw new InvalidOperationException("No active profile specified in config.");

        if (SafeMode.Enabled)
        {
            _host.Logger?.Warning("[StartupPipeline] Safe mode — forcing full-tunnel routing");
            isFullTunnel = true;
            settings.App.RoutingMode = "full";
        }

        Profile activeProfile;
        if (isFullTunnel)
        {
            _host.Logger?.Information(
                "[StartupPipeline] Full-tunnel mode — ignoring ActiveProfile '{Profile}' and skipping process scan",
                profileName ?? "(empty)");
            var blockOnVpnFail = !string.IsNullOrEmpty(profileName)
                && manager.MergeProfilesTolerant(
                    profileName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    out _)?.BlockOnVpnFail == true;
            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only", BlockOnVpnFail = blockOnVpnFail };
        }
        else if (!string.IsNullOrEmpty(profileName))
        {
            var names = profileName.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var merged = manager.MergeProfilesTolerant(names, out var missing);
            if (merged == null)
                throw new InvalidOperationException(
                    $"None of the requested profiles exist: {string.Join(", ", names)}. " +
                    $"Available: {string.Join(", ", collection.Profiles.Select(p => p.Name))}");

            activeProfile = merged;

            if (missing.Count > 0)
            {
                _host.OnWarning($"Skipped unknown profile(s): {string.Join(", ", missing)}");
                if (mode == StartupMode.ColdStart || mode == StartupMode.AutoFailover)
                    PersistSanitizedActiveProfile(settings, names, missing, profileName);
            }
        }
        else if (isCustomConfig)
        {
            activeProfile = new Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
        }
        else
        {
            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }

        if (settings.CustomApps?.Count > 0)
        {
            foreach (var app in settings.CustomApps)
            {
                if (!string.IsNullOrEmpty(app) &&
                    !activeProfile.Processes.Any(p =>
                        p.Name.Equals(app, StringComparison.OrdinalIgnoreCase)))
                {
                    activeProfile.Processes.Add(new ProcessRule
                    {
                        Name = app,
                        IncludeChildren = true,
                        ScanPatterns = new[] { app }
                    });
                }
            }
        }

        if (!SafeMode.Enabled)
            VpnEngine.RemoveExcludedApps(activeProfile, settings.ExcludedApps);

        _host.OnStatus($"Profile: {activeProfile.Name} ({activeProfile.Processes.Count} rules)");
        _host.SetActiveProfile(activeProfile);

        ct.ThrowIfCancellationRequested();

        return (activeProfile, isCustomConfig, rawCustomJson);
    }

    private void PersistSanitizedActiveProfile(
        AppSettings settings,
        string[] names,
        IReadOnlyCollection<string> missing,
        string originalProfileName)
    {
        var sanitized = string.Join(",",
            names.Where(n => !missing.Contains(n, StringComparer.OrdinalIgnoreCase)));
        if (string.Equals(sanitized, originalProfileName, StringComparison.Ordinal))
            return;

        settings.ActiveProfile = sanitized;
        try
        {
            var fresh = _store.Load(AppPaths.ConfigYamlPath);
            fresh.ActiveProfile = sanitized;
            _store.Save(fresh);
            _host.Logger?.Information(
                "[StartupPipeline] ActiveProfile migrated: '{Old}' → '{New}'",
                originalProfileName, sanitized);
        }
        catch (Exception saveEx)
        {
            _host.Logger?.Warning(saveEx,
                "[StartupPipeline] Failed to persist ActiveProfile migration");
        }
    }

    private async Task<ScanResult> ScanProcessesPhaseAsync(
        Profile profile,
        AppSettings settings,
        CancellationToken ct)
    {
        _host.OnStatus("Scanning processes...");
        ScanResult? scanResult = null;
        try
        {
            var scanTask = Task.Run(() => _host.Scanner.ScanForProfile(profile), ct);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var winner = await Task.WhenAny(scanTask, timeoutTask).ConfigureAwait(false);
            if (winner == scanTask)
            {
                scanResult = await scanTask.ConfigureAwait(false);
            }
            else
            {
                _host.Logger?.Warning(
                    "[StartupPipeline] Process scan timed out after 30s — continuing with empty list. " +
                    "Check %ProgramData%\\VPNRouter\\profiles\\ for corrupt entries, or switch to Full tunnel mode.");
                _host.OnWarning(
                    "Process scan timed out — split mode may not route correctly. " +
                    "Switch to Full mode or reset your catalogue.");
                _ = scanTask.ContinueWith(
                    t => { _ = t.Exception; },
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _host.Logger?.Error(ex,
                "[StartupPipeline] Process scan failed — continuing with empty list");
            _host.OnWarning($"Process scan error: {ex.Message}");
        }
        scanResult ??= new ScanResult
        {
            ProcessNames = new List<string>(),
            ScannedAt = DateTime.Now
        };
        _host.Logger?.Information(
            "[StartupPipeline] Resolved {Count} process names",
            scanResult.ProcessNames.Count);

        _host.SetScanResult(scanResult);

        ct.ThrowIfCancellationRequested();

        var detectedSubnets = NetworkInterfaceDetector.DetectWireGuardSubnets(
            settings.Tun.InterfaceName, _host.Logger);
        settings.Tun.AutoDetectedExcludeAddress = detectedSubnets;
        if (detectedSubnets.Count > 0)
        {
            _host.Logger?.Information(
                "[StartupPipeline] Auto-excluded WG/AWG subnets (runtime-only, not persisted): {Subnets}",
                string.Join(", ", detectedSubnets));
        }

        return scanResult;
    }

    private async Task<Func<VlessServerEntry, bool>?> TryProbeUdpForNaivePairingAsync(
        AppSettings settings, CancellationToken ct)
    {
        try
        {
            var active = settings.Vless.GetActiveServers();
            if (active == null || !active.Any(NaivePairing.IsNaive))
                return null;

            var udpCandidates = (settings.Vless.Servers ?? new List<VlessServerEntry>())
                .Where(NaivePairing.IsUdpCapable)
                .ToList();
            if (udpCandidates.Count == 0)
                return null;

            _host.OnStatus("Проверяем UDP-серверы для игр…");
            var probe = new ServerHealthProbe(_host.Logger);
            var results = await probe.ProbeAllAsync(udpCandidates, TimeSpan.FromSeconds(3), ct)
                .ConfigureAwait(false);
            var dead = new HashSet<string>(
                results.Where(r => !r.Alive).Select(r => r.Server.Name ?? string.Empty),
                StringComparer.Ordinal);
            if (dead.Count == 0)
                return null;

            _host.Logger?.Information(
                "[StartupPipeline] RB1: {Dead}/{Total} UDP candidate(s) dead — excluding from UDP pairing",
                dead.Count, udpCandidates.Count);
            return s => !dead.Contains(s.Name ?? string.Empty);
        }
        catch (Exception ex)
        {
            _host.Logger?.Debug("[StartupPipeline] RB1 UDP probe failed (non-fatal): {Err}", ex.Message);
            return null;
        }
    }

    private string GenerateConfigPhase(
        Profile profile,
        ScanResult scanResult,
        AppSettings settings,
        bool isCustomConfig,
        string? rawCustomJson,
        StartupMode mode,
        Func<VlessServerEntry, bool>? isServerAlive = null)
    {
        if (isCustomConfig)
        {
            var customPath = VpnEngine.ResolveCustomConfigPath(settings);
            var activeEntry = settings.App.CustomConfigs
                .FirstOrDefault(c => c.Name == settings.App.ActiveCustomConfig);
            var configName = activeEntry?.Name ?? "custom";
            var localCopy = CustomConfigInjector.GetProgramDataPath(configName);

            if (!File.Exists(localCopy) && File.Exists(customPath))
            {
                localCopy = CustomConfigInjector.CopyToProgramData(customPath, configName);
                _host.Logger?.Information(
                    "[StartupPipeline] Custom config copied to {Path}", localCopy);
            }

            var injectSource = (rawCustomJson != null && File.Exists(customPath))
                ? rawCustomJson
                : File.ReadAllText(localCopy);
            var configJson = CustomConfigInjector.Inject(
                injectSource, scanResult.ProcessNames, settings);
            _host.OnStatus($"Custom config '{configName}' injected with process routing");
            return configJson;
        }

        var json = ConfigPipeline.Generate(
            profile,
            scanResult.ProcessNames,
            settings,
            ConfigPipeline.ValidationMode.Strict,
            warningSink: msg => _host.OnWarning(msg),
            logger: _host.Logger,
            isServerAlive: isServerAlive);
        return json;
    }

    private async Task<bool> PreStartChecksPhaseAsync(
        AppSettings settings,
        string configJson,
        CancellationToken ct)
    {
        _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);

        var preCheck = sanityCheck.CheckBeforeStart(configJson);
        if (!preCheck.IsDead) return false;

        _host.Logger?.Warning(
            "[StartupPipeline] F-E pre-start dead config: {Reason} (field: {Field})",
            preCheck.Reason, preCheck.OffendingField);

        var failover = _host.WireFailover(sanityCheck);
        var outcome = await failover.HandleDeadConfigAsync(
            preCheck.Reason ?? "dead config", ct);

        if (outcome.UserFacingMessage != null)
            _host.OnAutoFailoverTriggered(outcome.UserFacingMessage);

        if (outcome.Switched)
        {
            _host.Logger?.Information(
                "[StartupPipeline] F-E switched to {New} — abort outer StartAsync flow",
                outcome.NewActiveServer);
            return true;
        }

        _host.OnWarning(outcome.UserFacingMessage ?? preCheck.Reason ?? "Dead config");
        throw new InvalidOperationException(
            outcome.UserFacingMessage ?? preCheck.Reason ?? "Dead VPN config");
    }

    private Task DeployAndSetupFirewallPhaseAsync(
        AppSettings settings,
        Profile profile,
        ScanResult scanResult,
        CancellationToken ct)
    {
        DeploySingBoxBinary(settings);

        ct.ThrowIfCancellationRequested();

        var firewall = _host.FirewallFactory();
        _host.SetFirewallManager(firewall);
        if (profile.BlockOnVpnFail && firewall is not ICommittedFirewallConfig)
        {
            var isFullTunnel = (settings.App.RoutingMode ?? "split")
                .Equals("full", StringComparison.OrdinalIgnoreCase);
            firewall.CreateBlockRules(scanResult.ProcessNames, isFullTunnel);
            _host.OnStatus("Firewall block rules created (disabled)");
        }

        ct.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }

    private void DeploySingBoxBinary(AppSettings settings)
    {
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
                var installedSize = new FileInfo(exePath).Length;
                var bundledSize = new FileInfo(bundledPath).Length;
                needDeploy = installedSize != bundledSize;
            }
            if (needDeploy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
                File.Copy(bundledPath, exePath, overwrite: true);
                _host.Logger?.Information(
                    "[StartupPipeline] Deployed sing-box from bundle to {Path}", exePath);
            }
        }
        else if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }
    }

    private async Task<int> StartSingBoxPhaseAsync(
        AppSettings settings,
        string configJson,
        bool isCustomConfig,
        CancellationToken ct)
    {
        _host.OnStatus("Starting sing-box...");

        var singBox = new SingBoxManager(settings.SingBox, _host.Logger);
        singBox.Started += pid => _host.OnSingBoxStarted(pid);
        _host.SetSingBoxManager(singBox);
        singBox.StartWithJson(configJson);

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (singBox.IsRunning()) break;
        }

        if (!singBox.IsRunning())
        {
            throw new Exception("sing-box failed to start within 5 seconds. Check logs.");
        }

        var pid = singBox.Pid ?? -1;
        _host.Logger?.Information("[StartupPipeline] sing-box started (PID {Pid})", pid);
        _host.OnStatus($"sing-box started (PID {pid})");

        ScheduleWarmupProbe(pid, settings, ct);

        if (!isCustomConfig)
        {
            _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);
            _host.SchedulePostStartProbe(settings, sanityCheck, ct);
        }

        return pid;
    }

    private void ScheduleWarmupProbe(int pidSnapshot, AppSettings settings, CancellationToken ct)
    {
        _host.OnStatus("Warming up network...");
        var seamHttp = WarmupHttp;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var inlineHttp = seamHttp == null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(3) }
                : null;
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    if (seamHttp != null)
                    {
                        var resp = await seamHttp.SendAsync(
                            new HttpRequest(
                                HttpMethod.Get,
                                new Uri("https://www.gstatic.com/generate_204"),
                                Timeout: TimeSpan.FromSeconds(3)),
                            ct);
                        if (!resp.IsSuccess())
                            throw new HttpRequestException(
                                $"warmup probe HTTP {resp.StatusCode}");
                    }
                    else
                    {
                        await inlineHttp!.GetStringAsync(
                            "https://www.gstatic.com/generate_204", ct);
                    }
                    _host.Logger?.Information(
                        "[StartupPipeline] TUN ready after {Ms}ms (attempt {Attempt})",
                        sw.ElapsedMilliseconds, attempt);
                    if (!_host.IsCurrentStart(pidSnapshot))
                    {
                        _host.Logger?.Debug(
                            "[StartupPipeline] Warm-up of PID {Pid} finished after a restart; ignoring stale result",
                            pidSnapshot);
                        return;
                    }
                    _host.OnStatus($"Connected (PID {pidSnapshot})");

                    try { _host.OnConnected(pidSnapshot); }
                    catch (Exception ex)
                    {
                        _host.Logger?.Warning(ex,
                            "[StartupPipeline] OnConnected callback threw (non-fatal)");
                    }

                    try { _dnsHardening.EnableLockdownIfConfigured(settings, _host.Logger); }
                    catch (Exception ex)
                    {
                        _host.Logger?.Warning(ex,
                            "[StartupPipeline] BR-7 deferred lockdown arm threw (non-fatal)");
                    }
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _host.Logger?.Debug(
                        "[StartupPipeline] Warm-up attempt {Attempt}: {Error}",
                        attempt, ex.GetType().Name);
                }
            }
            _host.Logger?.Warning(
                "[StartupPipeline] TUN warm-up failed after {Ms}ms — " +
                "Wave 39 firewall DNS lockdown NOT installed (BR-7: prefer " +
                "internet-up + DNS-leak-risk over internet-down + lockdown-on)",
                sw.ElapsedMilliseconds);
            if (_host.IsCurrentStart(pidSnapshot))
                _host.OnStatus($"Connected (PID {pidSnapshot})");
        }, ct);
    }

    private void StartMonitorsPhase(
        AppSettings settings,
        Profile activeProfile,
        ScanResult scanResult)
    {
        var profile = activeProfile;
        var etw = _host.MonitorFactory();
        _host.SetProcessMonitor(etw);

        var singBox = ((StartupHostInternal)_host).SingBox
            ?? throw new InvalidOperationException(
                "StartupPipeline phase 8: SingBoxManager missing (phase 7 didn't set it).");
        var firewall = ((StartupHostInternal)_host).Firewall
            ?? throw new InvalidOperationException(
                "StartupPipeline phase 8: IFirewallManager missing (phase 6 didn't set it).");

        var healthMonitor = new HealthMonitor(
            singBox, _host.Scanner, firewall,
            settings.Monitoring, _host.Logger,
            clashApiBase: settings.SingBox?.ClashApi,
            clashApiSecret: settings.SingBox?.ClashApiSecret);

        etw.ProcessStarted += (_, e) =>
        {
            var isTargeted = profile.Processes
                .Any(r => r.ScanPatterns
                    .Any(p => ProcessScanner.MatchesPattern(e.ProcessName + ".exe", p)));

            if (isTargeted)
            {
                _host.OnProcessDetected(e.ProcessName, e.ProcessId);
                healthMonitor.OnNewProcessDetected(e.ProcessName);
            }
        };

        healthMonitor.RestartAttempted += (_, attempt) =>
            _host.OnRestartAttempted(attempt, settings.Monitoring.MaxRestartAttempts);

        healthMonitor.FailoverRequested += (_, reason) => _host.OnFailoverRequested(reason);

        etw.Start();
        healthMonitor.Start(profile, settings, scanResult);

        _host.SetHealthMonitor(healthMonitor);

        if (profile.BlockOnVpnFail)
            _host.OnStatus("Firewall leak protection ready (armed for VPN failure)");

        _dnsHardening.Apply(settings, _host.Logger);
    }
}

internal interface StartupHostInternal : IStartupHost
{
    SingBoxManager? SingBox { get; }
    IFirewallManager? Firewall { get; }
}
