#nullable enable
using System.IO;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public sealed record StartupContext(
    AppSettings Settings,
    StartupMode Mode,
    bool SkipVpnConflictCheck = false)
{
    internal bool RequireCompleteRuleSets { get; init; }
    internal bool RestoreAcceptedImport { get; init; }
    internal string? SessionImportJson { get; init; }
    internal bool ValidateRequiredRuleSets => Mode != StartupMode.ColdStart || RequireCompleteRuleSets;
}

public sealed record StartupResult(
    bool Success,
    bool EarlyReturn,
    int? ProcessId,
    TimeSpan Duration,
    string? ConfigJson = null,
    Profile? Profile = null)
{
    internal Action? PublishAcceptedImport { get; init; }
    internal string? ImportedJson { get; init; }
}

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

    bool CallerConfirmsProxy => false;

    CancellationToken BeginStartupProbes(CancellationToken ct) => ct;

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
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    public static IHttpClient? WarmupHttp;

    public StartupPipeline(
        IStartupHost host,
        ISettingsStore? store = null,
        IWindowsDnsHardening? dnsHardening = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _store = store ?? RealSettingsStore.Instance;
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;
        _policy = SingBoxRuntimePolicy.Current;
    }

    public async Task<StartupResult> ExecuteAsync(
        StartupContext context,
        CancellationToken ct,
        Func<Task>? retireAfterValidation = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var policy = EffectivePolicy;
        using var policyScope = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var settings = context.Settings;

        if (context.Mode != StartupMode.HotReload && retireAfterValidation == null)
        {
            PreflightConflictAndDns(settings, context.SkipVpnConflictCheck);
            await PreflightGeoDataAsync(settings, ct);
        }

        var routingMode = string.Equals(settings.App.RoutingMode, "full", StringComparison.OrdinalIgnoreCase)
            ? "full" : "split";
        var tunFingerprint = VpnEngine.ComputeTunFingerprint(settings.Tun);
        var (profile, customConfigJson, serverAddress, publishAcceptedImport) =
            await ResolveProfileAndServersAsync(context, ct);
        bool isCustomConfig = customConfigJson != null;

        var scanResult = await ScanProcessesPhaseAsync(profile, settings, ct).ConfigureAwait(false);

        Func<VlessServerEntry, bool>? isServerAlive = context.Mode == StartupMode.HotReload
            ? null
            : await TryProbeUdpForNaivePairingAsync(settings, ct).ConfigureAwait(false);

        var current = (_host as StartupHostInternal)?.SingBox;
        Func<string, CancellationToken, Task<bool>>? checkRuleSet =
            context.Mode == StartupMode.HotReload && current != null ? current.CheckRuleSetAsync : null;
        var configJson = GenerateConfigPhase(
            profile,
            scanResult,
            settings,
            customConfigJson,
            context.ValidateRequiredRuleSets,
            isServerAlive,
            checkRuleSet);

        ct.ThrowIfCancellationRequested();

        if (isCustomConfig && (context.Mode == StartupMode.HotReload || retireAfterValidation != null))
        {
            // Failover provisions a new manager after validation. Hot Apply uses the existing deployment.
            var nextDeployment = retireAfterValidation == null ? null : settings.SingBox;
            if (current == null || !await current.CheckCandidateAsync(configJson, ct, nextDeployment).ConfigureAwait(false))
                throw new InvalidOperationException("The imported VPN configuration failed native validation.");
            ct.ThrowIfCancellationRequested();
        }

        var monitorController = context.Mode == StartupMode.HotReload ? default :
            ClashSingBoxApi.ReadLocalController(configJson);
        if (context.Mode != StartupMode.HotReload)
            await ClashSingBoxApi.ResolveControlTargetsAsync(monitorController.Endpoint, ct).ConfigureAwait(false);

        if (retireAfterValidation != null)
        {
            // A rejected automatic replacement must leave the current process and its policy in place.
            _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);
            if (!isCustomConfig && sanityCheck.CheckBeforeStart(configJson).IsDead)
                throw new InvalidOperationException("The replacement server configuration is invalid.");
            await retireAfterValidation().ConfigureAwait(false);
            PreflightConflictAndDns(settings, context.SkipVpnConflictCheck);
        }

        _host.SetActiveModes(isCustomConfig ? "custom" : "generated", routingMode, tunFingerprint);
        _host.SetActiveServerAddress(serverAddress);
        _host.SetActiveProfile(profile);
        _host.SetScanResult(scanResult);

        if (context.Mode == StartupMode.HotReload)
        {
            return new StartupResult(
                Success: true,
                EarlyReturn: false,
                ProcessId: null,
                Duration: sw.Elapsed,
                ConfigJson: configJson,
                Profile: profile) { PublishAcceptedImport = publishAcceptedImport, ImportedJson = customConfigJson };
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
            settings, configJson, ct);

        ct.ThrowIfCancellationRequested();

        var firewall = (_host as StartupHostInternal)?.Firewall;
        if (firewall is ICommittedFirewallConfig committedFirewall)
        {
            var isFullTunnel = (settings.App.RoutingMode ?? "split")
                .Equals("full", StringComparison.OrdinalIgnoreCase);
            committedFirewall.UpdateCommittedConfig(configJson, profile.BlockOnVpnFail && isFullTunnel);
        }

        void CommitStart()
        {
            ct.ThrowIfCancellationRequested();
            if (!_host.IsCurrentStart(pid))
                throw new InvalidOperationException("The tunnel exited before its startup settings could be accepted.");
            publishAcceptedImport?.Invoke();
        }
        StartMonitorsPhase(settings, profile, scanResult, monitorController, customConfigJson, CommitStart);

        var probeToken = _host.BeginStartupProbes(ct);
        ScheduleWarmupProbe(pid, settings, probeToken);
        if (!isCustomConfig)
        {
            _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);
            _host.SchedulePostStartProbe(settings, sanityCheck, probeToken);
        }

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

            var foreignTun = ConflictingVpnDetector.DetectForeignFullTunnelTun(settings.Tun.InterfaceName, _host.Logger);
            if (foreignTun.Count > 0)
            {
                throw new ConflictingVpnException(
                    Array.Empty<ConflictingVpnDetector.ConflictingProcessInfo>(),
                    $"Another VPN already routes all traffic through {foreignTun[0]}. " +
                    "Turn it off before connecting VPNRouter.");
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
        var checkContents = RuleSetFile.ForRuntime(settings.SingBox);
        if (settings.App.BypassRussianTraffic && !GeoDataDownloader.AreGeoFilesAvailable(checkContents))
        {
            _host.OnStatus("Downloading geo data...");
            try
            {
                var downloader = new GeoDataDownloader(PolicyHttpClient.Shared, _host.Logger, checkContents);
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

    private async Task<(Profile profile, string? rawCustomJson, string serverAddress, Action? publishAcceptedImport)>
        ResolveProfileAndServersAsync(StartupContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        var isCustomConfig = context.SessionImportJson != null ||
            string.Equals(settings.App.ConfigMode, "custom", StringComparison.OrdinalIgnoreCase);
        var (rawCustomJson, serverAddress, publishAcceptedImport) = ResolveServerSource(context, isCustomConfig);

        ct.ThrowIfCancellationRequested();

        _host.OnStatus("Loading profiles...");
        var (manager, collection) = await LoadProfileCatalogueAsync(settings, ct);

        ct.ThrowIfCancellationRequested();

        var activeProfile = SelectActiveProfile(settings, context.Mode, manager, collection, isCustomConfig);

        AddCustomAppsToProfile(activeProfile, settings);

        if (!SafeMode.Enabled)
            VpnEngine.RemoveExcludedApps(activeProfile, settings.ExcludedApps);

        _host.OnStatus($"Profile loaded ({activeProfile.Processes.Count} rules)");

        ct.ThrowIfCancellationRequested();

        return (activeProfile, rawCustomJson, serverAddress, publishAcceptedImport);
    }

    private (string? rawCustomJson, string serverAddress, Action? publishAcceptedImport) ResolveServerSource(
        StartupContext context, bool isCustomConfig)
    {
        var settings = context.Settings;
        string? rawCustomJson = context.SessionImportJson;
        Action? publishAcceptedImport = null;
        string serverAddress;

        if (isCustomConfig)
        {
            if (rawCustomJson == null)
            {
                var (customPath, acceptedPath) = VpnEngine.ResolveCustomConfigFiles(settings, context.RestoreAcceptedImport);
                if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                    throw new InvalidOperationException(
                        "Custom config not found. Add a config in the Servers tab.");

                rawCustomJson = CustomConfigInjector.ReadImport(customPath);
                if (acceptedPath != null && customPath != acceptedPath)
                    publishAcceptedImport = () => CustomConfigInjector.WriteImportCopy(acceptedPath, rawCustomJson);
            }
            var (isValid, errors) = CustomConfigInjector.Validate(rawCustomJson);
            if (!isValid)
                throw new InvalidOperationException(
                    $"Custom config validation failed ({errors.Count} errors). Check the selected JSON configuration.");

            try
            {
                (_, serverAddress) = CustomConfigInjector.ParseConfigInfo(rawCustomJson);
            }
            catch { serverAddress = ""; }
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
            serverAddress = activeServers.Count > 0 ? activeServers[0].Server : allServers[0].Server;
        }

        return (rawCustomJson, serverAddress, publishAcceptedImport);
    }

    private async Task<(ProfileManager manager, ProfileCollection collection)> LoadProfileCatalogueAsync(
        AppSettings settings,
        CancellationToken ct)
    {
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
            "[StartupPipeline] Loaded {Count} profiles", collection.Profiles.Count);

        return (manager, collection);
    }

    private Profile SelectActiveProfile(
        AppSettings settings,
        StartupMode mode,
        ProfileManager manager,
        ProfileCollection collection,
        bool isCustomConfig)
    {
        var isFullTunnel = (settings.App.RoutingMode ?? "split")
            .Equals("full", StringComparison.OrdinalIgnoreCase);
        var profileName = settings.ActiveProfile;

        if (string.IsNullOrEmpty(profileName) && !isFullTunnel && !isCustomConfig
            && !HasExplicitAppSelection(settings.App))
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
                "[StartupPipeline] Full-tunnel mode, skipping process scan");
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
                    $"None of the requested profiles exist. Choose one of the {collection.Profiles.Count} available profiles.");

            activeProfile = merged;

            if (missing.Count > 0)
            {
                _host.OnWarning($"Skipped {missing.Count} unknown profile(s)");
                if (mode == StartupMode.ColdStart)
                    PersistSanitizedActiveProfile(settings, names, missing, profileName);
            }
        }
        else if (isCustomConfig)
        {
            activeProfile = new Profile { Name = "CustomConfig", DnsMode = "vpn_only" };
        }
        else if (!isFullTunnel)
        {
            activeProfile = new Profile { Name = "SelectedApps", DnsMode = "vpn_only" };
        }
        else
        {
            activeProfile = new Profile { Name = "FullTunnel", DnsMode = "vpn_only" };
        }

        return activeProfile;
    }

    // The Linux shell picks apps directly instead of a profile; the app list
    // alone then defines split routing.
    private static bool HasExplicitAppSelection(AppConfig app)
        => string.Equals(app.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase)
            || app.RoutingAppsIncludeInitialized
            || app.RoutingAppsInclude?.Count > 0;

    private static void AddCustomAppsToProfile(Profile activeProfile, AppSettings settings)
    {
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
                "[StartupPipeline] Removed {Count} unavailable profiles from selection", missing.Count);
        }
        catch (Exception)
        {
            _host.Logger?.Warning(
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

            _host.OnStatus(Localization.Strings.StartupCheckingUdpServers);
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
        catch (Exception)
        {
            _host.Logger?.Debug("[StartupPipeline] RB1 UDP probe failed (non-fatal)");
            return null;
        }
    }

    private string GenerateConfigPhase(
        Profile profile,
        ScanResult scanResult,
        AppSettings settings,
        string? rawCustomJson,
        bool validateRequiredRuleSets,
        Func<VlessServerEntry, bool>? isServerAlive,
        Func<string, CancellationToken, Task<bool>>? checkRuleSet)
    {
        Action<string>? onMissingRuleSet = validateRequiredRuleSets ? ConfigGenerator.RejectMissingRuleSet : null;
        if (rawCustomJson != null)
        {
            var configJson = CustomConfigInjector.Inject(
                rawCustomJson, scanResult.ProcessNames, settings, onMissingRuleSet, checkRuleSet);
            _host.OnStatus("Prepared process routing for the selected custom configuration");
            return configJson;
        }

        var json = ConfigPipeline.Generate(
            profile,
            scanResult.ProcessNames,
            settings,
            ConfigPipeline.ValidationMode.Strict,
            warningSink: msg => _host.OnWarning(msg),
            logger: _host.Logger,
            isServerAlive: isServerAlive,
            onMissingRuleSet: onMissingRuleSet,
            checkRuleSet: checkRuleSet);
        return json;
    }

    internal async Task<bool> PreStartChecksPhaseAsync(
        AppSettings settings,
        string configJson,
        CancellationToken ct)
    {
        _host.EnsureSanityCheckScaffolding(settings, out var sanityCheck);

        var preCheck = sanityCheck.CheckBeforeStart(configJson);
        if (!preCheck.IsDead) return false;

        _host.Logger?.Warning(
            "[StartupPipeline] F-E pre-start configuration check failed (field: {Field})",
            preCheck.OffendingField);

        var failover = _host.WireFailover(sanityCheck);
        var outcome = await failover.HandleDeadConfigAsync(
            preCheck.Reason ?? "dead config", ct);

        if (outcome.UserFacingMessage != null)
            _host.OnAutoFailoverTriggered(outcome.UserFacingMessage);

        if (outcome.Switched)
        {
            _host.Logger?.Information(
                "[StartupPipeline] F-E switched servers, ending the original start attempt");
            return true;
        }

        const string rejected = "VPN startup rejected: check the selected server or choose another (pre_start_check_failed).";
        _host.OnWarning(rejected);
        throw new InvalidOperationException(rejected);
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
        if (EffectivePolicy != null)
            return;

        var exePath = AppPaths.DeployedSingBoxExePath(settings.SingBox);
        var source = AppPaths.SingBoxDeploymentSource(AppPaths.BundledSingBoxExePath, exePath);
        if (source != exePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
            File.Copy(source, exePath, overwrite: true);
            _host.Logger?.Information(
                "[StartupPipeline] Deployed sing-box from bundle to {Path}", exePath);
        }
        else if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"sing-box not found at: {exePath}");
        }
    }

    private async Task<int> StartSingBoxPhaseAsync(
        AppSettings settings,
        string configJson,
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

        return pid;
    }

    private void ScheduleWarmupProbe(int pidSnapshot, AppSettings settings, CancellationToken ct)
    {
        if (_host.CallerConfirmsProxy) return;
        _host.OnStatus("Warming up network...");
        ConfigSanityCheck? exactProxy = null;
        if (settings.App.ConfigMode == "generated" && settings.Vless.GetActiveServers().FirstOrDefault()?.UseAsSingleOutbound == true)
            _host.EnsureSanityCheckScaffolding(settings, out exactProxy);
        var seamHttp = WarmupHttp;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var inlineHttp = seamHttp == null && exactProxy == null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(3) }
                : null;
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    if (exactProxy != null)
                    {
                        if (!_host.IsCurrentStart(pidSnapshot)) return;
                        var probe = await exactProxy.ProbeAsync(VpnEngine.ParseClashApiPort(settings.SingBox.ClashApi),
                            settings.SingBox.ClashApiSecret, ct);
                        if (probe.IsDead) continue;
                    }
                    else if (seamHttp != null)
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
            if (exactProxy == null && _host.IsCurrentStart(pidSnapshot))
                _host.OnStatus($"Connected (PID {pidSnapshot})");
        }, ct);
    }

    private void StartMonitorsPhase(
        AppSettings settings,
        Profile activeProfile,
        ScanResult scanResult,
        (Uri Endpoint, string? Secret) controller,
        string? importJson,
        Action commit)
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
            clashApiBase: controller.Endpoint.Authority,
            clashApiSecret: controller.Secret) { AcceptedImportJson = importJson };
        _host.SetHealthMonitor(healthMonitor);

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

        if (profile.BlockOnVpnFail)
            _host.OnStatus("Firewall leak protection ready (armed for VPN failure)");

        _dnsHardening.Apply(settings, _host.Logger);
        healthMonitor.ResumeAfterApply(profile, settings, scanResult, commit: commit);
    }
}

internal interface StartupHostInternal : IStartupHost
{
    SingBoxManager? SingBox { get; }
    IFirewallManager? Firewall { get; }
}
