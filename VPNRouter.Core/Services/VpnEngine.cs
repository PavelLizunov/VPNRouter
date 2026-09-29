using System.IO;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

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

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _sessionCts;

    private volatile bool _warmupConfirmed;

    private volatile bool _postStartPhase;

    private SlipstreamManager? _slipstream;

    private ConnectionHealthState? _connHealth;
    private ClashLogStream? _connHealthStream;

    private bool _disposed;

    public bool IsRunning => _singBox?.IsRunning() ?? false;
    public string ActiveProfileName => _activeProfile?.Name ?? string.Empty;
    public int? SingBoxPid => _singBox?.Pid;
    public List<string> MonitoredProcesses => _scanResult?.ProcessNames ?? new();

    public string ActiveConfigMode { get; private set; } = "generated";

    public string ActiveRoutingMode { get; private set; } = "split";

    public string ActiveServerAddress { get; private set; } = string.Empty;

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

    public bool IsTrueSplitEngaged => _splitDriver?.IsEngaged ?? false;
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
        try { _unixDns.RestoreStrandedIfAny(_logger); } catch { }
    }

    internal static bool ShouldAutoFailoverAfterProbe(
        bool probeIsDead, bool probeCancelled, bool warmupConfirmed)
        => probeIsDead && !probeCancelled && !warmupConfirmed;

    public async Task StartAsync(AppSettings settings, CancellationToken ct = default, bool skipVpnConflictCheck = false)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasLiveOrStartingSingBox())
            {
                _logger?.Warning(
                    "[VpnEngine] StartAsync ignored - sing-box already {State} (PID {Pid}); use ApplyAsync/ReloadConfigJson for reconfigure",
                    _singBox?.State,
                    SingBoxPid);
                return;
            }

            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ResetFailoverContext(settings);
            try
            {
                await StartAsyncInternal(settings, _sessionCts.Token, skipVpnConflictCheck).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!IsRunning)
                {
                    _logger?.Warning(
                        ex,
                        "[VpnEngine] StartAsync failed to bring up VPN — tearing down partial state");
                    try { TeardownInternal(); } catch { }
                }
                else
                {
                    _logger?.Warning(
                        ex,
                        "[VpnEngine] StartAsync bring-up threw after sing-box came up — leaving live tunnel for Stop/Dispose");
                }
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal void ResetFailoverContext(AppSettings settings)
    {
        _failoverGeneration++;
        _failoverSettingsContext = settings;
        _failover = null;
    }

    private bool HasLiveOrStartingSingBox()
    {
        var singBox = _singBox;
        if (singBox == null) return false;

        if (singBox.State is SingBoxState.Starting or SingBoxState.Restarting)
            return true;

        return singBox.IsRunning();
    }

    internal async Task StartAsyncInternal(AppSettings settings, CancellationToken ct, bool skipVpnConflictCheck)
    {
        _skipVpnConflictCheck = skipVpnConflictCheck;
        _warmupConfirmed = false;
        _postStartPhase = false;
        var host = new VpnEngineStartupHost(this);
        var pipeline = new StartupPipeline(host, dnsHardening: _dnsHardening);
        if (_splitDriver is not null)
            await _splitDriver.SweepStaleStateAsync(ct).ConfigureAwait(false);

        var result = await pipeline.ExecuteAsync(
            new StartupContext(settings, StartupMode.ColdStart, skipVpnConflictCheck),
            ct);

        if (result.EarlyReturn)
        {
            _logger?.Information(
                "[VpnEngine] StartAsync: F-E re-entry handled by inner call (outer aborting)");
            return;
        }

        ActiveAppRoutingFingerprint = ConfigGenerator.ComputeAppRoutingFingerprint(
            _scanResult?.ProcessNames ?? [],
            settings);

        if (settings.App.DnsLeakLockdown)
        {
            try
            {
                var gateway = VPNRouter.Core.Platform.Unix.MacDnsParsers
                    .DeriveDnsTarget(settings.Tun?.Ipv4Address);
                if (!string.IsNullOrEmpty(gateway))
                    _unixDns.Apply(gateway!, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "[VpnEngine] Unix DNS hardening Apply failed (non-fatal)");
            }
        }

        await TryEngageSplitDriverAsync(settings, ct).ConfigureAwait(false);
    }

    internal async Task TryEngageSplitDriverAsync(
        AppSettings settings,
        CancellationToken ct)
    {
        if (_splitDriver is null)
        {
            SetTrueSplitState(TrueSplitState.NotApplicable, "No split-tunnel driver on this platform.");
            return;
        }

        var app = settings.App;
        bool hasExcluded = app.RoutingAppsExclude is { Count: > 0 };
        if (!SplitTunnelPolicy.ShouldEngage(OperatingSystem.IsWindows(), app.RoutingMode, app.RoutingAppsMode, hasExcluded, "auto"))
        {
            if (_splitDriver.IsEngaged) await _splitDriver.DisengageAsync(ct).ConfigureAwait(false);
            SetTrueSplitState(TrueSplitState.NotApplicable, "True split applies only to Windows split/exclude mode with excluded apps.");
            return;
        }

        if (!_splitDriver.IsAvailable)
        {
            SetTrueSplitState(TrueSplitState.DriverMissing, "True-split driver is not bundled in this build.");
            return;
        }

        SetTrueSplitState(TrueSplitState.Starting, "Starting true split...");
        var dosPaths = new List<string>();
        foreach (var name in app.RoutingAppsExclude)
        {
            var p = ProcessImagePath.ResolveRunningPath(name) ?? ProcessImagePath.ResolveNameToPath(name);
            if (!string.IsNullOrEmpty(p)) dosPaths.Add(p!);
            else _logger?.Information("[VpnEngine] Split-tunnel: '{Name}' not running/unresolved — post-capture rule covers it", name);
        }

        if (dosPaths.Count == 0)
        {
            if (_splitDriver.IsEngaged) await _splitDriver.DisengageAsync(ct).ConfigureAwait(false);
            _logger?.Information("[VpnEngine] True-split driver: 0 excluded path(s) resolved — not engaging (post-capture covers them)");
            SetTrueSplitState(TrueSplitState.Fallback, "True split needs a resolvable app path; ordinary split is active.");
            return;
        }

        var req = new SplitTunnelEngageRequest(
            dosPaths,
            settings.Tun?.Ipv4Address,
            TunnelIpv6: null);
        bool ok = await _splitDriver.EngageAsync(req, ct).ConfigureAwait(false);
        _logger?.Information("[VpnEngine] True-split driver engage={Ok} ({N} excluded path(s) resolved)", ok, dosPaths.Count);
        var failReason = _splitDriver.LastFailureReason;
        SetTrueSplitState(
            ok ? TrueSplitState.Active : TrueSplitState.Fallback,
            ok ? "True split active." : failReason ?? "True split did not start; ordinary split is active.");
    }

    public Task RestartTrueSplitAsync(AppSettings settings, CancellationToken ct = default) =>
        TryEngageSplitDriverAsync(settings, ct);

    private void SetTrueSplitState(TrueSplitState state, string reason)
    {
        CurrentTrueSplitState = state;
        TrueSplitStateChanged?.Invoke(state, reason);
    }

    internal async Task<bool> ExecuteFailoverRestartAsync(
        AppSettings captured,
        CancellationToken ct,
        long? expectedGeneration = null)
    {
        if (_postStartPhase)
            return await ExecuteProbeFailoverRestartAsync(captured, ct, expectedGeneration).ConfigureAwait(false);

        if ((expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) ||
            (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured)))
        {
            _logger?.Information(
                "[VpnEngine] Pre-start failover restart aborted — captured settings or generation do not match active failover context (stale failover intent)");
            return false;
        }

        try
        {
            await StartAsyncInternal(captured, ct, _skipVpnConflictCheck).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex,
                "[VpnEngine] Pre-start failover restart threw inside StartAsyncInternal");
            if (!IsRunning)
            {
                try { TeardownInternal(); } catch { }
            }
            return false;
        }
    }

    internal async Task<bool> ExecuteProbeFailoverRestartAsync(
        AppSettings captured,
        CancellationToken probeCt,
        long? expectedGeneration = null)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if ((expectedGeneration.HasValue && expectedGeneration.Value != _failoverGeneration) ||
                (_failoverSettingsContext is not null && !ReferenceEquals(_failoverSettingsContext, captured)))
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — captured settings or generation do not match active failover context (stale failover intent)");
                return false;
            }

            TeardownInternal();
            var session = _sessionCts;
            if (session == null || session.IsCancellationRequested)
            {
                _logger?.Information(
                    "[VpnEngine] Failover restart aborted — session cancelled (user disconnect)");
                return false;
            }
            await StartAsyncInternal(captured, session.Token, _skipVpnConflictCheck).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.Information(
                "[VpnEngine] Failover restart cancelled mid-flight by user disconnect — not resurrecting");
            return false;
        }
        catch (ObjectDisposedException)
        {
            _logger?.Information(
                "[VpnEngine] Failover restart aborted — engine disposed during shutdown");
            return false;
        }
        catch (Exception ex)
        {
            if (!IsRunning)
            {
                _logger?.Warning(ex,
                    "[VpnEngine] Failover restart failed to bring up replacement — tearing down partial state");
                try { TeardownInternal(); } catch { }
            }
            else
            {
                _logger?.Warning(ex,
                    "[VpnEngine] Failover restart bring-up threw after sing-box came up — leaving live tunnel for Stop/Dispose");
            }
            throw;
        }
        finally
        {
            try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task<bool> ApplyAsync(AppSettings settings, CancellationToken ct = default, bool forceRestart = false)
    {
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

    private async Task<bool> ApplyGatedAsync(AppSettings settings, CancellationToken ct, bool forceRestart)
    {
        if (_sessionCts is null || _sessionCts.IsCancellationRequested)
        {
            _logger?.Warning("[VpnEngine] Apply skipped — no active session (stopped or never started)");
            return false;
        }

        if (_singBox == null || !_singBox.IsRunning())
        {
            _logger?.Warning("[VpnEngine] Apply called but sing-box not running");
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

        void RestoreActiveBaseline()
        {
            ActiveConfigMode = oldConfigMode;
            ActiveRoutingMode = oldRoutingMode;
            TunFingerprint = oldTunFingerprint;
            ActiveAppRoutingFingerprint = oldAppRoutingFingerprint;
            ActiveServerAddress = oldServerAddress;
            _activeProfile = oldProfile;
            _scanResult = oldScanResult;
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

            if (!forceRestart && _singBox.TryReloadConfigJson(configJson))
            {
                ActiveConfigMode = newConfigMode;
                ActiveRoutingMode = newRoutingMode;
                TunFingerprint = newTunFingerprint;
                ActiveAppRoutingFingerprint = newAppRoutingFingerprint;
                configCommitted = true;
                ResetFailoverContext(settings);
                UpdateFirewallCommittedConfig(configJson, newRoutingMode, result.Profile ?? _activeProfile);
                OnStatus($"Applied (hot-reload, PID {_singBox.Pid})");
                _logger?.Information("[VpnEngine] Applied via hot-reload");
                await TryEngageSplitDriverAsync(settings, CancellationToken.None).ConfigureAwait(false);
                return true;
            }

            if (forceRestart)
                _logger?.Information("[VpnEngine] Forced full restart (structural change)");
            else
                _logger?.Warning("[VpnEngine] Hot-reload failed, falling back to full restart");

            if (!_singBox.ReloadConfigJsonWithResult(configJson, forceRestart))
            {
                RestoreActiveBaseline();
                _logger?.Error("[VpnEngine] Apply failed: sing-box reload or restart was not confirmed");
                OnStatus("Apply failed: sing-box reload or restart was not confirmed");
                return false;
            }

            ActiveConfigMode = newConfigMode;
            ActiveRoutingMode = newRoutingMode;
            TunFingerprint = newTunFingerprint;
            ActiveAppRoutingFingerprint = newAppRoutingFingerprint;
            configCommitted = true;
            ResetFailoverContext(settings);
            UpdateFirewallCommittedConfig(configJson, newRoutingMode, result.Profile ?? _activeProfile);
            OnStatus($"Applied (restart, PID {_singBox.Pid})");

            await TryEngageSplitDriverAsync(settings, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            if (!configCommitted)
                RestoreActiveBaseline();
            _logger?.Error(ex, "[VpnEngine] Apply failed");
            OnStatus($"Apply failed: {ex.Message}");
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

    public void Stop()
    {
        try { _sessionCts?.Cancel(); } catch { }
        try { _healthMonitor?.Stop(); } catch { }
        _lifecycleGate.Wait();
        try
        {
            _failoverGeneration++;
            _failover = null;
            TeardownInternal();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void TeardownInternal()
    {
        OnStatus("Stopping...");

        try { _probeCts?.Cancel(); } catch { }
        _warmupConfirmed = false;
        // Dispose the health monitor before sing-box, or its poll sees sing-box exiting and restarts it mid-stop.
        try { _healthMonitor?.Dispose(); } catch { }
        // Dispose, not Stop: unhooks the ProcessExit handler so each connect cycle does not leak a manager.
        try { _singBox?.Dispose(); } catch { }
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

    internal static string ResolveCustomConfigPath(AppSettings settings)
    {
        if (settings.App.CustomConfigs?.Count > 0)
        {
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

        return Environment.ExpandEnvironmentVariables(settings.App.CustomConfig ?? "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRunning) Stop();
        else
        {
            try { _dnsHardening.Restore(_logger); } catch { }
            try { _unixDns.Restore(_logger); } catch { }
            try { _firewall?.Dispose(); } catch { }
            _firewall = null;
            try { _singBox?.Dispose(); } catch { }
            _singBox = null;
            try { _slipstream?.Dispose(); } catch { }
            _slipstream = null;
        }
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

    internal static string ComputeTunFingerprint(Models.TunSettings tun)
    {
        var excludes = tun.GetEffectiveRouteExcludeAddress();
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

    internal static void MergeUserCustomization(
        ProfileCollection collection,
        AppSettings settings)
    {
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

    internal static void RemoveExcludedApps(Profile? profile, IReadOnlyList<string>? excludedApps)
    {
        if (profile == null) return;
        if (excludedApps == null || excludedApps.Count == 0) return;

        var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in excludedApps)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            excludeSet.Add(StripExeSuffix(raw));
        }
        if (excludeSet.Count == 0) return;

        profile.Processes.RemoveAll(p =>
            p != null && !string.IsNullOrEmpty(p.Name)
            && excludeSet.Contains(StripExeSuffix(p.Name)));
    }

    private static string StripExeSuffix(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    internal static List<IProfileSource> BuildProfileSources(AppSettings settings)
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

        var platformProfiles = Path.Combine(AppPaths.ProfilesDir, platformDefaultName);
        if (File.Exists(platformProfiles))
            sources.Add(new LocalProfileSource(platformProfiles, 85));
        var userDefault = Path.Combine(AppPaths.ProfilesDir, "default.json");
        if (File.Exists(userDefault) && !userDefault.Equals(platformProfiles, StringComparison.Ordinal))
            sources.Add(new LocalProfileSource(userDefault, 83));

        sources.Add(new BuiltInProfileSource());
        return sources;
    }

    internal static List<IProfileSource> BuildBundledOnlyProfileSources()
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

    private static readonly string[] ExpectedV222Groups =
    {
        "Discord_Privacy", "Messengers", "AI_Tools", "Browsers",
        "Work_Suite", "Streaming", "Gaming", "Privacy_Shell"
    };

    internal static void QuarantineStaleUserCatalogue(ILogger? logger)
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
                var collection = JsonSerializer.Deserialize(
                    json, Json.AppJsonContext.Default.ProfileCollection);
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
            logger?.Warning(ex, "[VpnEngine] Could not quarantine stale user catalogue");
        }
    }

    private sealed class VpnEngineStartupHost : StartupHostInternal
    {
        private readonly VpnEngine _engine;
        private readonly long _generation;
        private readonly CancellationTokenSource? _sessionCts;
        private SingBoxManager? _singBoxManager;
        private IProcessHandle? _startedHandle;

        public VpnEngineStartupHost(VpnEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _generation = engine._failoverGeneration;
            _sessionCts = engine._sessionCts;
        }

        public ILogger? Logger => _engine._logger;
        public IProcessScanner Scanner => _engine._scanner;
        public Func<IFirewallManager> FirewallFactory => _engine._firewallFactory;
        public Func<IProcessMonitor> MonitorFactory => _engine._monitorFactory;

        public SingBoxManager? SingBox => _engine._singBox;
        public IFirewallManager? Firewall => _engine._firewall;

        public void OnStatus(string message) => _engine.OnStatus(message);

        public void OnWarning(string message) => _engine.Warning?.Invoke(message);

        public void OnSingBoxStarted(int pid)
        {
            _startedHandle = _singBoxManager?.OwnedProcessHandle;
            _engine.EnterPostStartPhase();
            _engine.SingBoxStarted?.Invoke(pid);
        }

        public bool IsCurrentStart(int pid)
        {
            try
            {
                if (_engine._disposed) return false;
                if (_generation != _engine._failoverGeneration) return false;
                if (_sessionCts == null || _sessionCts.IsCancellationRequested || !ReferenceEquals(_engine._sessionCts, _sessionCts))
                    return false;

                if (_singBoxManager == null || !ReferenceEquals(_engine._singBox, _singBoxManager) || _singBoxManager.Pid != pid || _singBoxManager.State != SingBoxState.Running)
                    return false;

                return _startedHandle != null && !_startedHandle.HasExited
                    && ReferenceEquals(_singBoxManager.OwnedProcessHandle, _startedHandle);
            }
            catch
            {
                return false;
            }
        }

        public void OnConnected(int pid)
        {
            if (!IsCurrentStart(pid)) return;

            _engine._warmupConfirmed = true;
            _engine._failover?.ResetCycle();
            _engine.Connected?.Invoke(pid);
        }

        public void OnRestartAttempted(int attempt, int max) =>
            _engine.RestartAttempted?.Invoke(attempt, max);

        public void OnAutoFailoverTriggered(string message) =>
            _engine.AutoFailoverTriggered?.Invoke(message);

        public void OnFailoverRequested(string reason)
        {
            _ = Task.Run(async () =>
            {
                var token = _engine._sessionCts?.Token ?? CancellationToken.None;
                try
                {
                    if (token.IsCancellationRequested) return;
                    var sanity = new ConfigSanityCheck(_engine._logger);
                    var failover = WireFailoverWithStop(sanity);
                    var outcome = await failover.HandleDeadConfigAsync(reason, token);
                    if (outcome.UserFacingMessage != null
                        && _engine._sessionCts?.IsCancellationRequested != true)
                        _engine.AutoFailoverTriggered?.Invoke(outcome.UserFacingMessage);
                }
                catch (Exception ex)
                {
                    _engine._logger?.Warning(ex,
                        "[VpnEngine] HealthMonitor-requested failover failed — leaving VPN stopped");
                }
            });
        }

        public void OnProcessDetected(string name, int pid) =>
            _engine.ProcessDetected?.Invoke(name, pid);

        public void SetActiveServerAddress(string address) =>
            _engine.ActiveServerAddress = address;

        public void SetActiveModes(string configMode, string routingMode, string tunFingerprint)
        {
            _engine.ActiveConfigMode = configMode;
            _engine.ActiveRoutingMode = routingMode;
            _engine.TunFingerprint = tunFingerprint;
        }

        public void SetActiveProfile(Profile profile) => _engine._activeProfile = profile;

        public void SetScanResult(ScanResult result) => _engine._scanResult = result;

        public void SetSingBoxManager(SingBoxManager manager)
        {
            _singBoxManager = manager;
            _engine._singBox = manager;
        }

        public void StartDnsTunnelTransport(VlessServerEntry activeServer, AppSettings settings)
        {
            if (settings.App?.DnsLeakLockdown == true)
                _engine._logger?.Warning(
                    "[VpnEngine] dns-tunnel active WITH DnsLeakLockdown — the lockdown may block " +
                    "slipstream-client's DNS to the resolvers (the tunnel's own transport). " +
                    "If the tunnel won't connect, disable DnsLeakLockdown.");

            var slip = _engine._slipstream ??= new SlipstreamManager(_engine._logger);
            _engine.OnStatus("Starting DNS-tunnel transport...");
            slip.Start(activeServer, SlipstreamManager.DefaultLocalPort);

            if (!slip.WaitForPortListening(5000))
            {
                slip.Stop();
                throw new SlipstreamException(
                    "slipstream-client did not start listening on 127.0.0.1:" +
                    SlipstreamManager.DefaultLocalPort + " within 5s");
            }
            _engine.OnStatus("DNS-tunnel transport up (127.0.0.1:" + SlipstreamManager.DefaultLocalPort + ")");
        }

        public void SetFirewallManager(IFirewallManager firewall) => _engine._firewall = firewall;

        public void SetProcessMonitor(IProcessMonitor etw) => _engine._etw = etw;

        public void SetHealthMonitor(HealthMonitor monitor) => _engine._healthMonitor = monitor;

        public void EnsureSanityCheckScaffolding(AppSettings settings, out ConfigSanityCheck sanityCheck)
        {
            CaptureSettings(settings);
            _engine._sanityCheck ??= new ConfigSanityCheck(_engine._logger);
            sanityCheck = _engine._sanityCheck;
        }

        public AutoFailoverEngine WireFailover(ConfigSanityCheck sanityCheck)
            => WireFailoverCore(sanityCheck);

        public AutoFailoverEngine WireFailoverWithStop(ConfigSanityCheck sanityCheck)
            => WireFailoverCore(sanityCheck);

        private AutoFailoverEngine WireFailoverCore(ConfigSanityCheck sanityCheck)
        {
            var settings = _engine._failoverSettingsContext ?? CapturedSettings();
            var generation = _engine._failoverGeneration;
            _engine._failover ??= new AutoFailoverEngine(
                settings,
                sanityCheck,
                restart: (innerCt) =>
                    _engine.ExecuteFailoverRestartAsync(settings, innerCt, generation),
                logger: _engine._logger)
            {
                IsCurrentIntent = () =>
                {
                    var sessionCts = _engine._sessionCts;
                    return !_engine._disposed
                        && _engine._failoverGeneration == generation
                        && (sessionCts == null || !sessionCts.IsCancellationRequested);
                }
            };
            return _engine._failover;
        }

        private AppSettings _capturedSettings = null!;
        public void CaptureSettings(AppSettings settings) => _capturedSettings = settings;
        private AppSettings CapturedSettings() =>
            _capturedSettings ?? throw new InvalidOperationException(
                "StartupHost: CaptureSettings was not called before F-E wire-up.");

        public void SchedulePostStartProbe(
            AppSettings settings,
            ConfigSanityCheck sanityCheck,
            CancellationToken ct)
        {
            CaptureSettings(settings);

            _engine.TryStartConnectionHealthStream(settings);

            _engine._probeCts?.Cancel();
            _engine._probeCts?.Dispose();
            _engine._probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var probeCt = _engine._probeCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), probeCt);
                    var clashPort = ParseClashApiPort(settings.SingBox.ClashApi);
                    var probe = await sanityCheck.ProbeAsync(
                        clashPort, settings.SingBox.ClashApiSecret, probeCt);
                    if (ShouldAutoFailoverAfterProbe(
                            probe.IsDead, probeCt.IsCancellationRequested, _engine._warmupConfirmed))
                    {
                        _engine._logger?.Warning(
                            "[VpnEngine] F-E post-start probe failed: {Reason}",
                            probe.Reason);

                        var failover = WireFailoverWithStop(sanityCheck);
                        var outcome = await failover.HandleDeadConfigAsync(
                            probe.Reason ?? "probe failed", probeCt);
                        if (outcome.UserFacingMessage != null
                            && _engine._sessionCts?.IsCancellationRequested != true)
                            _engine.AutoFailoverTriggered?.Invoke(outcome.UserFacingMessage);
                    }
                    else if (probe.IsDead && !probeCt.IsCancellationRequested && _engine._warmupConfirmed)
                    {
                        _engine._logger?.Warning(
                            "[VpnEngine] F-E post-start probe reported dead ({Reason}) but TUN " +
                            "warmup already confirmed connectivity — treating as false positive, " +
                            "NOT failing over.",
                            probe.Reason);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _engine._logger?.Debug(ex,
                        "[VpnEngine] F-E probe task threw (non-fatal)");
                }
            }, probeCt);
        }
    }
}
