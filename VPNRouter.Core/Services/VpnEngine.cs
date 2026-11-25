using System.IO;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform.macOS;

namespace VPNRouter.Core.Services;

public partial class VpnEngine : IDisposable
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
    private readonly IWindowsDnsHardening _dnsHardening;
    private readonly IUnixDnsHardening _unixDns;
    private readonly ISplitTunnelDriver? _splitDriver;
    private readonly ILogger? _logger;

    private ConfigSanityCheck? _sanityCheck;
    private AutoFailoverEngine? _failover;
    private long _failoverGeneration;
    private AppSettings? _failoverSettingsContext;
    private bool _skipVpnConflictCheck;
    private CancellationTokenSource? _probeCts;
    private bool _startupIncomplete;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _sessionCts;

    private volatile bool _warmupConfirmed;

    private volatile bool _postStartPhase;

    private SlipstreamManager? _slipstream;

    private ConnectionHealthState? _connHealth;
    private ClashLogStream? _connHealthStream;

    private bool _disposed;
    private SingBoxRuntimePolicy? _policy;

    private SingBoxRuntimePolicy? EffectivePolicy => SingBoxRuntimePolicy.Capture(ref _policy);

    public bool IsRunning => _singBox?.IsRunning() ?? false;
    public string ActiveProfileName => _activeProfile?.Name ?? string.Empty;
    public int? SingBoxPid => _singBox?.Pid;
    // Unix readiness uses Clash, which can be unavailable while the owned process is alive.
    internal bool RequireCompleteRuleSetsOnStart { get; set; }
    internal Func<IProcessHandle, string, CancellationToken, Task<bool>>? ConfirmServiceReplacement { get; set; }
    public bool HasActiveTunnel => _singBox is { } manager && (manager.ExactStopUnconfirmed || manager.HasActiveTunnel);
    public List<string> MonitoredProcesses => _scanResult?.ProcessNames ?? new();

    public string ActiveConfigMode { get; private set; } = "generated";

    public string ActiveRoutingMode { get; private set; } = "split";

    public string ActiveServerAddress { get; private set; } = string.Empty;

    internal AcceptedTunnelInfo? AcceptedTunnel => Volatile.Read(ref _acceptedTunnel);
    private AcceptedTunnelInfo? _acceptedTunnel;

    internal void CommitTunnelInfo(AcceptedTunnelInfo tunnel) => Volatile.Write(ref _acceptedTunnel, tunnel);

    internal string TunFingerprint { get; private set; } = string.Empty;

    internal string ActiveAppRoutingFingerprint { get; private set; } = string.Empty;

    internal bool SkipVpnConflictCheckSnapshot => _skipVpnConflictCheck;

    internal void EnterPostStartPhase() => _postStartPhase = true;

    public event Action<string>? StatusChanged;

    public event Action<string, int>? ProcessDetected;

    public event Action<int, int>? RestartAttempted;

    public event Action<string>? Warning;

    public event Action<int>? SingBoxStarted;

    public event Action<int>? Connected;

    /// <summary>The current sing-box process exited unexpectedly.</summary>
    public event Action? SingBoxCrashed;

    // Names the first readiness condition that fails for pid, or null when ready.
    internal string? DescribeReadiness(int pid)
    {
        if (_disposed) return "disposed";
        if (!_warmupConfirmed) return "warmup not confirmed";
        if (_singBox == null) return "no sing-box manager";
        if (_sessionCts == null || _sessionCts.IsCancellationRequested) return "no live session";
        var handle = _singBox.OwnedProcessHandle;
        if (handle == null || handle.HasExited) return "no live process handle";
        if (_singBox.Pid != pid) return $"pid {_singBox.Pid} != {pid}";
        if (_singBox.State != SingBoxState.Running) return $"sing-box state {_singBox.State}";
        return null;
    }

    internal Func<bool> CaptureReadinessGuard(int pid)
    {
        if (_disposed) return () => false;
        var capturedSingBox = _singBox;
        var capturedSessionCts = _sessionCts;
        var capturedGeneration = _failoverGeneration;
        var capturedHandle = capturedSingBox?.OwnedProcessHandle;

        return () =>
        {
            try
            {
                if (_disposed) return false;
                if (!_warmupConfirmed) return false;
                if (capturedSingBox == null || !ReferenceEquals(_singBox, capturedSingBox)) return false;
                if (capturedSessionCts == null || !ReferenceEquals(_sessionCts, capturedSessionCts) || capturedSessionCts.IsCancellationRequested) return false;
                if (_failoverGeneration != capturedGeneration) return false;
                if (capturedHandle == null || !ReferenceEquals(_singBox.OwnedProcessHandle, capturedHandle)) return false;
                if (capturedSingBox.Pid != pid) return false;
                if (capturedHandle.HasExited) return false;
                if (capturedSingBox.State != SingBoxState.Running) return false;
                return true;
            }
            catch
            {
                return false;
            }
        };
    }

    public event Action<string>? AutoFailoverTriggered;

    public event Action<bool>? TrueSplitEngagedChanged;
    public event Action<TrueSplitState, string>? TrueSplitStateChanged;

    public TrueSplitState CurrentTrueSplitState { get; private set; } = TrueSplitState.NotApplicable;

    [Obsolete(
        "Use PlatformServices.CreateVpnEngine — direct construction bypasses " +
        "the platform-specific scanner / firewall / monitor wiring. This " +
        "warning is non-fatal during Phase 3; will become an error in " +
        "Phase 4 once all call sites are migrated.",
        error: false)]
    public VpnEngine(
        IProcessScanner scanner,
        Func<IFirewallManager> firewallFactory,
        Func<IProcessMonitor> monitorFactory,
        ILogger? logger = null,
        IWindowsDnsHardening? dnsHardening = null,
        IUnixDnsHardening? unixDnsHardening = null,
        ISplitTunnelDriver? splitDriver = null)
    {
        _scanner = scanner;
        _firewallFactory = firewallFactory;
        _monitorFactory = monitorFactory;
        _logger = logger;
        _splitDriver = splitDriver;
        if (_splitDriver is not null)
            _splitDriver.EngagedChanged += engaged => TrueSplitEngagedChanged?.Invoke(engaged);
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;
        _unixDns = unixDnsHardening ?? NullUnixDnsHardening.Default;
        _policy = SingBoxRuntimePolicy.Current;
        try { _unixDns.RestoreStrandedIfAny(_logger); } catch { }
    }

    internal static AppSettings CopySessionSettings(AppSettings settings)
    {
        var snapshot = SettingsLoader.Copy(settings);
        if (settings.Tun?.AutoDetectedExcludeAddress is { Count: > 0 } detected)
            snapshot.Tun.AutoDetectedExcludeAddress = [.. detected];
        return snapshot;
    }

    public Task StartAsync(AppSettings settings, CancellationToken ct = default, bool skipVpnConflictCheck = false) =>
        StartAsync(settings, ct, skipVpnConflictCheck, restoreAcceptedImport: false);

    internal async Task StartAsync(AppSettings settings, CancellationToken ct, bool skipVpnConflictCheck, bool restoreAcceptedImport)
    {
        var policy = EffectivePolicy;
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);
        settings = CopySessionSettings(settings);

        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartSessionGatedAsync(settings, ct, skipVpnConflictCheck, restoreAcceptedImport).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartSessionGatedAsync(AppSettings settings, CancellationToken ct, bool skipVpnConflictCheck,
        bool restoreAcceptedImport = false, string? sessionImportJson = null, bool deferActiveProfileSave = false)
    {
        if (_startupIncomplete || (ConfirmServiceReplacement != null && HasLiveOrStartingSingBox() &&
            _singBox != null && _healthMonitor?.IsMonitoring(_singBox) != true))
            RetireIncompleteStartup();

        if (HasLiveOrStartingSingBox())
        {
            _logger?.Warning(
                "[VpnEngine] StartAsync ignored - sing-box already {State} (PID {Pid}); use ApplyAsync/ReloadConfigJson for reconfigure",
                _singBox?.State,
                SingBoxPid);
            return;
        }

        _sessionCts?.Cancel();
        if (_singBox != null || _healthMonitor != null) TeardownInternal();
        _sessionCts?.Dispose();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ResetFailoverContext(settings);
        await StartAsyncInternal(settings, _sessionCts.Token, skipVpnConflictCheck,
            restoreAcceptedImport: restoreAcceptedImport, sessionImportJson: sessionImportJson,
            deferActiveProfileSave: deferActiveProfileSave).ConfigureAwait(false);
    }

    private void RetireIncompleteStartup()
    {
        // Keep this flag and exact ownership if the child cannot be stopped. A retry must finish
        // cleanup before treating any running child as a successful start.
        _startupIncomplete = true;
        _probeCts?.Cancel();
        _healthMonitor?.Stop();
        TeardownInternal();
        _startupIncomplete = false;
    }

    private bool HasLiveOrStartingSingBox()
    {
        var singBox = _singBox;
        if (singBox == null) return false;

        if (singBox.State is SingBoxState.Starting or SingBoxState.Restarting)
            return true;

        return singBox.ExactStopUnconfirmed ||
            (singBox.OwnedProcessHandle is { } child && child.QueryHasExited() != true) ||
            singBox.HasActiveTunnel;
    }

    internal async Task StartAsyncInternal(AppSettings settings, CancellationToken ct, bool skipVpnConflictCheck,
        Action? retireAfterValidation = null, bool restoreAcceptedImport = false, string? sessionImportJson = null,
        bool deferActiveProfileSave = false)
    {
        bool preparingNewRuntime = false;
        async Task PrepareRuntimeAsync()
        {
            ct.ThrowIfCancellationRequested();
            retireAfterValidation?.Invoke();
            preparingNewRuntime = _startupIncomplete = true;
            _skipVpnConflictCheck = skipVpnConflictCheck;
            _warmupConfirmed = false;
            _postStartPhase = false;
            if (_splitDriver is not null)
                await _splitDriver.SweepStaleStateAsync(ct).ConfigureAwait(false);
        }

        var host = new VpnEngineStartupHost(this);
        var pipeline = new StartupPipeline(host, dnsHardening: _dnsHardening);
        var mode = retireAfterValidation == null ? StartupMode.ColdStart : StartupMode.AutoFailover;
        try
        {
            if (mode == StartupMode.ColdStart)
                await PrepareRuntimeAsync().ConfigureAwait(false);

            var result = await pipeline.ExecuteAsync(
                new StartupContext(settings, mode, skipVpnConflictCheck)
                {
                    RequireCompleteRuleSets = RequireCompleteRuleSetsOnStart,
                    DeferActiveProfileSave = deferActiveProfileSave,
                    RestoreAcceptedImport = restoreAcceptedImport,
                    SessionImportJson = sessionImportJson
                }, ct,
                retireAfterValidation: mode == StartupMode.AutoFailover ? PrepareRuntimeAsync : null);

            if (result.EarlyReturn)
            {
                _logger?.Information(
                    "[VpnEngine] StartAsync: F-E re-entry handled by inner call (outer aborting)");
                return;
            }

            ActiveAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(
                _scanResult?.ProcessNames ?? [],
                settings);

            ApplyUnixDnsSettings(settings);

            await TryEngageSplitDriverAsync(settings, ct).ConfigureAwait(false);
            _startupIncomplete = false;
        }
        catch when (preparingNewRuntime)
        {
            RetireIncompleteStartup();
            throw;
        }
    }

    private void ApplyUnixDnsSettings(AppSettings settings)
    {
        var isLinux = OperatingSystem.IsLinux();
        var managesLinuxDns = isLinux && EffectivePolicy != null;
        if (settings.App.DnsLeakLockdown
            || (managesLinuxDns && Platform.Linux.LinuxDnsHardening.ShouldSteerResolver(settings)))
        {
            try
            {
                var gateway = isLinux
                    ? Platform.Linux.LinuxDnsHardening.DeriveDnsTarget(settings.Tun?.Ipv4Address)
                    : Platform.Unix.MacDnsParsers.DeriveDnsTarget(settings.Tun?.Ipv4Address);
                if (!string.IsNullOrEmpty(gateway))
                    _unixDns.Apply(gateway!, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[VpnEngine] Unix DNS hardening Apply failed (non-fatal)");
            }
        }
        else if (managesLinuxDns)
        {
            _unixDns.Restore(_logger);
        }
    }

    public async Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct = default, bool forceRestart = false)
    {
        settings = CopySessionSettings(settings);
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyGatedAsync(settings, ct, forceRestart).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> ApplyGatedAsync(AppSettings settings, CancellationToken ct, bool forceRestart, bool verifySelection = false)
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested)
        {
            _logger?.Warning("[VpnEngine] Apply skipped — no active session (stopped or never started)");
            return false;
        }

        if (_singBox == null || !HasActiveTunnel)
        {
            _logger?.Warning("[VpnEngine] Apply called but sing-box not running");
            return false;
        }

        if (_startupIncomplete || (ConfirmServiceReplacement != null && _healthMonitor?.IsMonitoring(_singBox) != true))
        {
            _logger?.Warning("[VpnEngine] Apply skipped while service recovery is unavailable");
            return false;
        }

        var oldConfigMode = ActiveConfigMode;
        var oldRoutingMode = ActiveRoutingMode;
        var oldTunFingerprint = TunFingerprint;
        var oldAppRoutingFingerprint = ActiveAppRoutingFingerprint;
        var oldServerAddress = ActiveServerAddress;
        var oldProfile = _activeProfile;
        var oldScanResult = _scanResult;
        var configCommitted = false;
        var previousSettings = _failoverSettingsContext;
        bool pausedHealth = false;
        bool pausedTelemetry = false;
        string? lastGoodJson = null;
        IProcessHandle? previousServiceHandle = null;
        bool rolledBack = false;
        IProcessHandle? confirmedServiceHandle = null;

        void RestoreActiveBaseline()
        {
            ActiveConfigMode = oldConfigMode;
            ActiveRoutingMode = oldRoutingMode;
            TunFingerprint = oldTunFingerprint;
            ActiveAppRoutingFingerprint = oldAppRoutingFingerprint;
            ActiveServerAddress = oldServerAddress;
            _activeProfile = oldProfile;
            _scanResult = oldScanResult;
            if (pausedTelemetry && previousSettings != null && !_sessionCts.IsCancellationRequested &&
                (rolledBack || (previousServiceHandle?.QueryHasExited() == false &&
                 ReferenceEquals(previousServiceHandle, _singBox.OwnedProcessHandle))))
            {
                pausedTelemetry = false;
                TryStartConnectionHealthStream(previousSettings);
            }
            if (pausedHealth && oldProfile != null && previousSettings != null && !_sessionCts.IsCancellationRequested)
            {
                pausedHealth = false;
                _healthMonitor?.ResumeAfterApply(oldProfile, previousSettings, oldScanResult);
            }
        }

        OnStatus("Applying config changes...");

        try
        {
            var host = new VpnEngineStartupHost(this);
            var pipeline = new StartupPipeline(host, dnsHardening: _dnsHardening);
            var result = await pipeline.ExecuteAsync(
                new StartupContext(settings, StartupMode.HotReload),
                ct);

            if (!result.Success || result.ConfigJson == null)
            {
                _logger?.Warning("[VpnEngine] Apply: pipeline returned no config JSON");
                RestoreActiveBaseline();
                return false;
            }

            var configJson = result.ConfigJson;
            if (verifySelection)
            {
                var sanity = _sanityCheck ??= new ConfigSanityCheck(_logger);
                if (sanity.CheckBeforeStart(configJson).IsDead || !await _singBox.CheckCandidateAsync(configJson, ct).ConfigureAwait(false))
                    throw new InvalidOperationException("Selected proxy configuration failed validation");
            }

            var newConfigMode = (settings.App.ConfigMode ?? "generated")
                .Equals("custom", StringComparison.OrdinalIgnoreCase)
                ? "custom"
                : "generated";
            var newRoutingMode = (settings.App.RoutingMode ?? "split")
                .Equals("full", StringComparison.OrdinalIgnoreCase)
                ? "full"
                : "split";
            var newTunFingerprint = ComputeTunFingerprint(settings.Tun);
            var newAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(
                _scanResult?.ProcessNames ?? [],
                settings);
            var (configModeChanged, routingModeChanged, tunChanged, appRoutingChanged) = DetectStructuralChanges(
                oldConfigMode,
                newConfigMode,
                oldRoutingMode,
                newRoutingMode,
                oldTunFingerprint,
                newTunFingerprint,
                oldAppRoutingFingerprint,
                newAppRoutingFingerprint);

            if (configModeChanged)
            {
                _logger?.Information(
                    "[VpnEngine] ConfigMode change detected ({Old} -> {New}) -- escalating to full restart",
                    oldConfigMode, newConfigMode);
            }

            if (routingModeChanged)
            {
                _logger?.Information(
                    "[VpnEngine] RoutingMode change detected ({Old} -> {New}) -- escalating to full restart so TUN routes are re-laid",
                    oldRoutingMode, newRoutingMode);
            }

            if (tunChanged)
            {
                _logger?.Information(
                    "[VpnEngine] TUN settings change detected -- escalating to full restart. Old fingerprint {Old}, new {New}",
                    oldTunFingerprint, newTunFingerprint);
            }

            if (appRoutingChanged)
            {
                _logger?.Information(
                    "[VpnEngine] Effective app routing change detected -- escalating to full restart so existing TCP connections rejoin under the new Include/Exclude policy");
            }

            forceRestart |= configModeChanged || routingModeChanged || tunChanged || appRoutingChanged;

            // sing-box's Clash API accepts PUT /configs but does not reload anything,
            // so a "hot reload" left the old server and rules running. Restart instead.
            _logger?.Information(forceRestart
                ? "[VpnEngine] Forced full restart (structural change)"
                : "[VpnEngine] Applying config via full restart");

            // Resolve the control endpoint before retiring a live tunnel.
            var controller = _healthMonitor != null || ConfirmServiceReplacement != null || verifySelection
                ? ClashSingBoxApi.ReadLocalController(configJson) : ((Uri Endpoint, string? Secret)?)null;
            var tunnelInfo = new AcceptedTunnelInfo(settings, configJson, ActiveServerAddress, controller, result.ProxySources);
            if (controller is { } target)
                await ClashSingBoxApi.ResolveControlTargetsAsync(target.Endpoint, ct).ConfigureAwait(false);
            if (_healthMonitor != null)
            {
                _healthMonitor.PauseForApply();
                pausedHealth = true;
            }
            previousServiceHandle = _singBox.OwnedProcessHandle;
            var confirm = ConfirmServiceReplacement;
            if (confirm != null || result.PublishAcceptedImport != null)
            {
                lastGoodJson = File.ReadAllText(AppPaths.CurrentConfigPath);
            }
            // Stop old control requests before replacing the listener or its credentials.
            _probeCts?.Cancel();
            if (_connHealthStream != null)
            {
                _connHealthStream.Dispose();
                _connHealthStream = null;
                pausedTelemetry = true;
            }
            if (!_singBox.ReloadConfigJsonWithResult(configJson, forceRestart: true))
            {
                if (lastGoodJson != null && !ReferenceEquals(previousServiceHandle, _singBox.OwnedProcessHandle))
                    throw new InvalidOperationException("Service replacement did not start.");
                RestoreActiveBaseline();
                _logger?.Error("[VpnEngine] Apply failed: sing-box reload or restart was not confirmed");
                OnStatus("Apply failed: sing-box reload or restart was not confirmed");
                return false;
            }

            if (confirm != null)
            {
                var replacement = _singBox.OwnedProcessHandle;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCts.Token);
                if (replacement == null || !await confirm(replacement, configJson, lifetime.Token).ConfigureAwait(false) ||
                    lifetime.IsCancellationRequested || replacement.QueryHasExited() != false ||
                    !ReferenceEquals(replacement, _singBox.OwnedProcessHandle))
                    throw new InvalidOperationException("The service tunnel did not confirm its replacement configuration.");
                confirmedServiceHandle = replacement;
            }

            ActiveConfigMode = newConfigMode;
            ActiveRoutingMode = newRoutingMode;
            TunFingerprint = newTunFingerprint;
            ActiveAppRoutingFingerprint = newAppRoutingFingerprint;
            void AcceptSettings()
            {
                if (confirm != null &&
                    (ct.IsCancellationRequested || _sessionCts.IsCancellationRequested ||
                     confirmedServiceHandle?.QueryHasExited() != false ||
                     !ReferenceEquals(confirmedServiceHandle, _singBox.OwnedProcessHandle)))
                    throw new InvalidOperationException("The replacement exited before accepting settings.");

                result.PublishAcceptedImport?.Invoke();
                if (_healthMonitor != null) _healthMonitor.AcceptedImportJson = result.ImportedJson;
                CommitTunnelInfo(tunnelInfo);
                ResetFailoverContext(settings);
                configCommitted = true;
            }

            if (pausedHealth && result.Profile != null)
                _healthMonitor!.ResumeAfterApply(result.Profile, settings, _scanResult, controller, AcceptSettings);
            else if (confirm == null)
                AcceptSettings();
            else
                throw new InvalidOperationException("Service recovery could not accept the replacement settings.");
            if (newConfigMode == "generated") TryStartConnectionHealthStream(settings);
            // Restart replaces the link and resolved drops its per-link settings.
            ApplyUnixDnsSettings(settings);
            UpdateFirewallCommittedConfig(configJson, newRoutingMode, result.Profile ?? _activeProfile);
            OnStatus($"Applied (restart, PID {_singBox.Pid})");

            await TryEngageSplitDriverAsync(settings, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (configCommitted)
            {
                _logger?.Warning("[VpnEngine] Post-apply step failed ({ExceptionType})", ex.GetType().Name);
                return true;
            }
            if (lastGoodJson != null && !_sessionCts.IsCancellationRequested &&
                !ReferenceEquals(previousServiceHandle, _singBox.OwnedProcessHandle))
            {
                try
                {
                    if (_singBox.HasActiveTunnel)
                    {
                        rolledBack = _singBox.ReloadConfigJsonWithResult(lastGoodJson, forceRestart: true);
                        if (!rolledBack) _logger?.Error("[VpnEngine] Service tunnel rollback was not confirmed");
                    }
                    else
                    {
                        _singBox.StartWithJson(lastGoodJson);
                        rolledBack = true;
                    }
                }
                catch (Exception) { _logger?.Error("[VpnEngine] Service tunnel rollback failed"); }
            }
            RestoreActiveBaseline();
            _logger?.Error("[VpnEngine] Apply failed ({ExceptionType})", ex.GetType().Name);
            OnStatus("Apply failed: check the selected configuration and required rule sets, then retry.");
            return false;
        }
    }

    private void UpdateFirewallCommittedConfig(string configJson, string routingMode, Profile? profile)
    {
        if (_firewall is ICommittedFirewallConfig committed)
        {
            var isFullTunnel = routingMode.Equals("full", StringComparison.OrdinalIgnoreCase);
            var enabledForFullTunnel = (profile?.BlockOnVpnFail == true) && isFullTunnel;
            committed.UpdateCommittedConfig(configJson, enabledForFullTunnel);
        }
    }

    internal bool TryPrepareColdReconnect()
    {
        // A concurrent start/restart may have acquired a process since the desktop checked.
        if (!_lifecycleGate.Wait(0)) return false;
        try
        {
            if (HasLiveOrStartingSingBox()) return false;
            _sessionCts?.Cancel();
            EndSession();
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Stop() => Stop(onlyOwnedSession: false);

    internal void StopOwnedSession() => Stop(onlyOwnedSession: true);

    private void Stop(bool onlyOwnedSession)
    {
        try { _sessionCts?.Cancel(); } catch { }
        try { _healthMonitor?.Stop(); } catch { }
        _lifecycleGate.Wait();
        try
        {
            // A child with unreadable exit status still belongs to its manager.
            if (!onlyOwnedSession || _singBox != null || _healthMonitor != null || _etw != null
                || _slipstream != null || _connHealthStream != null || _firewall != null)
                EndSession();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void EndSession()
    {
        _failoverGeneration++;
        _failover = null;
        TeardownInternal();
    }

    private void TeardownInternal()
    {
        OnStatus("Stopping...");

        if (_singBox is { } manager &&
            !(_healthMonitor?.TryRetireForFailover(manager.TryRetireForReplacement) ?? manager.TryRetireForReplacement()))
        {
            OnStatus("Tunnel stop was not confirmed. Retry disconnect.");
            throw new InvalidOperationException("Tunnel stop was not confirmed.");
        }

        try { _probeCts?.Cancel(); } catch { }
        _warmupConfirmed = false;
        // Dispose the health monitor before sing-box, or its poll sees sing-box exiting and restarts it mid-stop.
        try { _healthMonitor?.Dispose(); } catch { }
        try { _slipstream?.Dispose(); } catch { }

        try { _splitDriver?.DisengageAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); } catch { }

        try { _connHealthStream?.Stop(); } catch { }
        try { _connHealthStream?.Dispose(); } catch { }
        _connHealthStream = null;

        try { _dnsHardening.Restore(_logger); } catch { }
        try { _unixDns.Restore(_logger); } catch { }

        try { _etw?.Dispose(); } catch { }

        if (_activeProfile?.BlockOnVpnFail == true)
        {
            try { _firewall?.DisableBlockRules(); } catch { }
            try { _firewall?.DeleteAllRules(); } catch { }
        }

        try { _firewall?.Dispose(); } catch { }
        // Stop may follow a replacement before the health monitor can bind its
        // new identity. The helper can recover only this user's inactive policy.
        if (_firewall is MacFirewallManager macFirewall)
            macFirewall.RecoverInactivePolicy();

        Volatile.Write(ref _acceptedTunnel, null);
        _singBox = null;
        _slipstream = null;
        _healthMonitor = null;
        _etw = null;
        _firewall = null;

        try
        {
            if (OperatingSystem.IsWindows())
                TunAdapterDiagnostics.LogAdapterState(_logger, "VpnEngine.after-stop");
        }
        catch { }

        OnStatus("Stopped");
        _logger?.Information("[VpnEngine] Stopped");
    }

    public void Dispose() => Dispose(onlyOwnedSession: false);

    internal void DisposeOwnedSession() => Dispose(onlyOwnedSession: true);

    private void Dispose(bool onlyOwnedSession)
    {
        if (_disposed) return;

        if (onlyOwnedSession)
        {
            StopOwnedSession();
        }
        else if (_singBox != null)
        {
            Stop();
        }
        else
        {
            try { _healthMonitor?.Dispose(); } catch { }
            _healthMonitor = null;
            try { _etw?.Dispose(); } catch { }
            _etw = null;
            try { _connHealthStream?.Stop(); } catch { }
            try { _connHealthStream?.Dispose(); } catch { }
            _connHealthStream = null;
            try { _dnsHardening.Restore(_logger); } catch { }
            try { _unixDns.Restore(_logger); } catch { }
            try { _firewall?.Dispose(); } catch { }
            _firewall = null;
            try { _slipstream?.Dispose(); } catch { }
            _slipstream = null;
        }
        _disposed = true;
        try { _sanityCheck?.Dispose(); } catch { }
        _sanityCheck = null;
        try { _splitDriver?.Dispose(); } catch { }
        try { _probeCts?.Cancel(); } catch { }
        try { _probeCts?.Dispose(); } catch { }
        _probeCts = null;
        try { _sessionCts?.Cancel(); } catch { }
        try { _sessionCts?.Dispose(); } catch { }
        _sessionCts = null;
        try { _lifecycleGate.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }

    private void OnStatus(string message)
    {
        _logger?.Information("[VpnEngine] {Status}", message);
        StatusChanged?.Invoke(message);
    }

    internal static int ParseClashApiPort(string? hostPort)
    {
        const int Default = 9090;
        if (string.IsNullOrWhiteSpace(hostPort)) return Default;
        var colonIdx = hostPort.LastIndexOf(':');
        if (colonIdx < 0 || colonIdx == hostPort.Length - 1) return Default;
        var portStr = hostPort[(colonIdx + 1)..];
        return int.TryParse(portStr, out var port) && port > 0 && port <= 65535
            ? port
            : Default;
    }

    internal void TryStartConnectionHealthStream(AppSettings settings)
    {
        var flag = Environment.GetEnvironmentVariable("VPNROUTER_CONN_HEALTH");
        bool enabled = string.Equals(flag, "1", StringComparison.Ordinal) ||
                       string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
            return;

        try
        {
            _connHealthStream?.Dispose();
            _connHealth ??= new ConnectionHealthState();
            var clashPort = ParseClashApiPort(settings.SingBox.ClashApi);
            _connHealthStream = new ClashLogStream(
                $"http://127.0.0.1:{clashPort}",
                _connHealth,
                proxyEndpoints: null,
                logger: _logger,
                secret: settings.SingBox.ClashApiSecret);
            _connHealthStream.Start();
            _logger?.Information(
                "[VpnEngine] Connection-health telemetry started (observe-only, Clash port {Port})", clashPort);
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "[VpnEngine] Connection-health telemetry start failed (non-fatal)");
        }
    }

    internal static (bool ConfigModeChanged, bool RoutingModeChanged, bool TunChanged, bool AppRoutingChanged)
        DetectStructuralChanges(
            string activeConfigMode,
            string candidateConfigMode,
            string activeRoutingMode,
            string candidateRoutingMode,
            string activeTunFingerprint,
            string candidateTunFingerprint,
            string activeAppRoutingFingerprint,
            string candidateAppRoutingFingerprint)
        => (
            !string.Equals(
                activeConfigMode,
                candidateConfigMode,
                StringComparison.OrdinalIgnoreCase),
            !string.Equals(
                activeRoutingMode,
                candidateRoutingMode,
                StringComparison.OrdinalIgnoreCase),
            !string.Equals(
                activeTunFingerprint,
                candidateTunFingerprint,
                StringComparison.Ordinal),
            !string.Equals(
                activeAppRoutingFingerprint,
                candidateAppRoutingFingerprint,
                StringComparison.Ordinal));
}
