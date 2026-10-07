using System.IO;
using System.Text.Json;
using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;

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
        _policy = SingBoxRuntimePolicy.Current;
        try { _unixDns.RestoreStrandedIfAny(_logger); } catch { }
    }

    public async Task StartAsync(AppSettings settings, CancellationToken ct = default, bool skipVpnConflictCheck = false)
    {
        var policy = EffectivePolicy;
        using var _ = SingBoxRuntimePolicy.EnterScope(policy);
        policy?.Authorize(SingBoxRuntimeOperation.Start);

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
