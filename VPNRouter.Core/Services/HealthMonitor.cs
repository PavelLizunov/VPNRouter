using Serilog;
using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Platform.macOS;

namespace VPNRouter.Core.Services;

public class HealthMonitor : IDisposable
{
    // A removed registration must not change recovery for a tunnel which is still running.
    internal string? AcceptedImportJson { get; set; }
    private readonly SingBoxManager _singBox;
    private readonly IProcessScanner _scanner;
    private readonly IFirewallManager _firewall;
    private MonitoringSettings _settings;
    // Drain old timer, process and crash work before swapping the settings used for recovery.
    private readonly object _settingsWork = new();
    private long _settingsGeneration;
    private readonly ILogger _logger;

    private ISingBoxApi _api;

    private IDisposable? _ownedApi;

    private readonly IWindowsDnsHardening _dnsHardening;

    private volatile bool _strictDnsFailedOver;
    private int _strictDnsUnhealthyStreak;
    private int _strictDnsHealthyStreak;
    private const string StrictDnsProbeUrl = "http://www.gstatic.com/generate_204";
    private const int StrictDnsProbeTimeoutMs = 3000;
    private const int HealthProbeRestartFailThreshold = 2;
    private int _unhealthyHealthProbeStreak;
    private const int StrictDnsFailThreshold = 2;
    private const int StrictDnsRecoverThreshold = 2;

    private System.Threading.Timer? _healthTimer;
    private System.Threading.Timer? _debounceTimer;

    private PowerEventListener? _powerListener;

    private Profile _activeProfile = null!;
    private AppSettings _appSettings = null!;
    private ScanResult? _lastScan;
    private int _restartAttempts;
    private bool _vpnWasRunning;
    private bool _disposed;
    private volatile bool _isStopping;

    private bool _shouldBeRunning;

    // Wedge detection arms only after the Clash API has served once this lifecycle, never during TUN warm-up.
    private bool _servingConfirmed;
    private int _wedgeStreak;
    private const int WedgeKillThreshold = 4;

    private volatile bool _failoverRequested;

    private CancellationTokenSource? _restartCts;

    // Serialises AttemptRestart, which is invoked from both the health tick and the Crashed callback on different threads.
    private readonly object _attemptRestartLock = new();

    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(60);
    private DateTime _lastFullRestart = DateTime.MinValue;

    // After a full restart the new TUN is not routing yet: lift the kill switch only once the tunnel is confirmed healthy.
    private int _deferredBlockRuleDisable;

    private static readonly TimeSpan DeferredDisableMaxWait = TimeSpan.FromSeconds(45);

    public event EventHandler<int>? RestartAttempted;

    public HealthMonitor(
        SingBoxManager singBox,
        IProcessScanner scanner,
        IFirewallManager firewall,
        MonitoringSettings settings,
        ILogger? logger = null,
        ISingBoxApi? api = null,
        IWindowsDnsHardening? dnsHardening = null,
        string? clashApiBase = null,
        string? clashApiSecret = null)
    {
        _singBox = singBox;
        _scanner = scanner;
        _firewall = firewall;
        _settings = settings;
        _logger = logger ?? Log.Logger;
        _dnsHardening = dnsHardening ?? WindowsDnsHardeningImpl.Default;

        if (api is not null)
        {
            _api = api;
            _ownedApi = null;
        }
        else
        {
            var concrete = ClashSingBoxApi.ForController(ClashSingBoxApi.LocalController(clashApiBase), clashApiSecret, _logger);
            _api = concrete;
            _ownedApi = concrete;
        }

        _singBox.Crashed += QueueCrashedProcess;
    }

    public void Start(Profile profile, AppSettings appSettings, ScanResult? initialScan = null)
    {
        if (_healthTimer != null || _powerListener != null)
        {
            _logger.Warning("[HealthMonitor] Start() called while already running — " +
                "restarting cleanly to avoid orphaning the prior timer + power listener");
            Stop();
        }

        _activeProfile = profile;
        _appSettings = appSettings;
        _restartAttempts = 0;
        _unhealthyHealthProbeStreak = 0;
        _failoverRequested = false;
        _isStopping = false;
        _shouldBeRunning = true;
        _servingConfirmed = false; _wedgeStreak = 0;
        _lastScan = initialScan;

        var intervalSeconds = appSettings.App.StrictMode ? 5 : _settings.HealthCheckInterval;
        var intervalMs = intervalSeconds * 1000;

        _healthTimer = new System.Threading.Timer(
            OnHealthTick, null, intervalMs, intervalMs);

        _powerListener = new PowerEventListener(ProbeNow, _logger);
        _powerListener.Start();

        _logger.Information("[HealthMonitor] Started — check every {Sec}s, max {Max} restarts (strict mode: {Strict})",
            intervalSeconds, _settings.MaxRestartAttempts, appSettings.App.StrictMode);
    }

    internal void ResumeAfterApply(Profile profile, AppSettings settings, ScanResult? scan,
        (Uri Endpoint, string? Secret)? controller = null, Action? commit = null)
    {
        lock (_settingsWork)
        {
            // Rollback reuses the previously accepted API client, including an import's control address.
            var candidateApi = _ownedApi != null && controller is { } target
                ? ClashSingBoxApi.ForController(target.Endpoint, target.Secret, _logger)
                : null;
            try
            {
                _settings = settings.Monitoring;
                Start(profile, settings, scan);
                commit?.Invoke();
            }
            catch
            {
                candidateApi?.Dispose();
                StopCore(preserveFirewallRelease: true);
                throw;
            }

            if (controller is { } accepted) _singBox.SetClashController(accepted);
            if (candidateApi != null)
            {
                var oldApi = _ownedApi;
                _api = candidateApi;
                _ownedApi = candidateApi;
                oldApi?.Dispose();
            }
            _strictDnsFailedOver = false;
            _strictDnsUnhealthyStreak = _strictDnsHealthyStreak = 0;
        }
    }

    internal bool IsMonitoring(SingBoxManager manager)
    {
        lock (_settingsWork)
            return ReferenceEquals(manager, _singBox) && _healthTimer != null && !_disposed &&
                !_isStopping && _shouldBeRunning;
    }

    internal void PauseForApply()
    {
        lock (_settingsWork) StopCore(preserveFirewallRelease: true);
    }

    internal bool TryRetireForFailover(Func<bool> retire)
    {
        lock (_settingsWork)
        {
            if (!retire()) return false;
            Stop();
            return true;
        }
    }

    private void RunForActiveSettings(Action work)
    {
        lock (_settingsWork)
            if (!_isStopping && !_disposed) work();
    }

    public void Stop()
    {
        lock (_settingsWork) StopCore(preserveFirewallRelease: false);
    }

    private void StopCore(bool preserveFirewallRelease)
    {
        Interlocked.Increment(ref _settingsGeneration);
        _isStopping = true;
        _shouldBeRunning = false;
        _unhealthyHealthProbeStreak = 0;

        if (!preserveFirewallRelease)
        {
            Interlocked.Exchange(ref _deferredBlockRuleDisable, 0);
            _lastFullRestart = DateTime.MinValue;
        }
        var cts = _restartCts;
        _restartCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
        var ht = System.Threading.Interlocked.Exchange(ref _healthTimer, null);
        var dt = System.Threading.Interlocked.Exchange(ref _debounceTimer, null);
        ht?.Dispose();
        dt?.Dispose();

        var pl = System.Threading.Interlocked.Exchange(ref _powerListener, null);
        pl?.Dispose();

        _logger.Information("[HealthMonitor] Stopped");
    }

    public void ProbeNow()
    {
        if (_isStopping || _disposed) return;
        OnHealthTick(state: null);
    }

    public void OnNewProcessDetected(string processName)
    {
        if (_isStopping || _disposed) return;
        if (_lastScan?.ProcessNames != null &&
            _lastScan.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Verbose("[HealthMonitor] Process {Name} already monitored — skipping debounce", processName);
            return;
        }

        _logger.Debug("[HealthMonitor] New process detected: {Name} — debouncing", processName);

        // Swap the debounce timer atomically: ETW callbacks arrive on multiple threads.
        var newTimer = new System.Threading.Timer(
            OnDebounceElapsed, null,
            (int)DebounceWindow.TotalMilliseconds,
            Timeout.Infinite);
        var oldTimer = System.Threading.Interlocked.Exchange(ref _debounceTimer, newTimer);
        oldTimer?.Dispose();
    }

    private void OnHealthTick(object? state)
    {
        if (!Monitor.TryEnter(_settingsWork))
        {
            _logger.Debug("[HealthMonitor] OnHealthTick skipped — previous tick still in progress");
            return;
        }

        try
        {
            if (_isStopping || _disposed) return;
            var isHealthy = _singBox.IsHealthy();

            bool? _servingThisTick = null;
            bool Serving() { _servingThisTick ??= ClashApiResponds(); return _servingThisTick.Value; }

            if (isHealthy && _vpnWasRunning && !_isStopping && _singBox.IsRunning()
                && WedgeKillPolicy.ShouldKill(Serving(), ref _servingConfirmed, ref _wedgeStreak, WedgeKillThreshold))
            {
                _logger.Warning("[HealthMonitor] sing-box WEDGED (alive, Clash API not serving {N} ticks) — killing to free the TUN adapter so excluded apps recover", WedgeKillThreshold);
                try { _singBox.KillWedgedForRecovery(); }
                catch (Exception kex) { _logger.Error(kex, "[HealthMonitor] wedge kill failed"); }
                OnSingBoxCrashed(this, EventArgs.Empty);
                return;
            }

            if (isHealthy && System.Threading.Volatile.Read(ref _deferredBlockRuleDisable) == 1)
            {
                var sinceRestart = DateTime.UtcNow - _lastFullRestart;
                var clashServing = Serving();
                var fallbackElapsed = sinceRestart >= DeferredDisableMaxWait;
                if ((clashServing || fallbackElapsed)
                    && System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0) == 1)
                {
                    try
                    {
                        var released = true;
                        if (_firewall is MacFirewallManager macFirewall)
                            released = macFirewall.TryDisableBlockRules();
                        else
                            _firewall.DisableBlockRules();

                        if (released)
                            _logger.Information(
                                "[HealthMonitor] Kill-switch block rules lifted after restart — TUN serving confirmed (clashApi={Clash}, fallback={Fallback}, +{Secs:F0}s)",
                                clashServing, fallbackElapsed, sinceRestart.TotalSeconds);
                        else
                            Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    }
                    catch (Exception fwEx)
                    {
                        if (_firewall is MacFirewallManager)
                            Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                        _logger.Error(fwEx, "[HealthMonitor] Failed to lift firewall block rules on deferred-disable");
                    }
                }
            }

            if (!isHealthy && _vpnWasRunning && !_isStopping &&
                !_singBox.LastCrashWasLinuxTunPermissionFailure)
            {
                _logger.Warning("[HealthMonitor] Health check failed — sing-box is not healthy");
                if (!ShouldRestartAfterHealthProbeFailure())
                    return;
                AttemptRestart();
            }
            else if (!isHealthy && _shouldBeRunning && !_isStopping &&
                     !_singBox.LastCrashWasLinuxTunPermissionFailure)
            {
                _logger.Warning("[HealthMonitor] sing-box dead while user wants VPN up — initiating recovery (intended-running path)");
                AttemptRestart();
            }
            else if (isHealthy && !_vpnWasRunning)
            {
                _unhealthyHealthProbeStreak = 0;
                _vpnWasRunning = true;
                _restartAttempts = 0;
                _failoverRequested = false;
                _logger.Information("[HealthMonitor] VPN is up");
            }
            else if (isHealthy)
            {
                _unhealthyHealthProbeStreak = 0;
            }

            if (_appSettings?.App?.DnsLeakLockdown == true)
            {
                bool serving = isHealthy && Serving();
                _dnsHardening.ReconcileLockdownForHealth(serving, _appSettings, _logger);
            }

            if (isHealthy)
                ReconcileStrictDnsFailover();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[HealthMonitor] Exception in health tick");
        }
        finally
        {
            Monitor.Exit(_settingsWork);
        }
    }

    private bool ClashApiResponds()
    {
        try { return _api.GetVersionAsync().GetAwaiter().GetResult() != null; }
        catch { return false; }
    }

    public event EventHandler<bool>? StrictDnsFailoverChanged;

    public event EventHandler<string>? FailoverRequested;

    private bool StrictDnsIsSoleDriver()
    {
        var app = _appSettings?.App;
        if (app is null || !app.StrictDns) return false;
        if ((app.ConfigMode ?? "generated").Equals("custom", StringComparison.OrdinalIgnoreCase)) return false;
        if ((app.RoutingMode ?? "split").Equals("full", StringComparison.OrdinalIgnoreCase)) return false;
        if ((app.RoutingAppsMode ?? "include").Equals("exclude", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private bool ProxyReachable()
    {
        try
        {
            return _api.GetProxyDelayAsync("proxy", StrictDnsProbeUrl, StrictDnsProbeTimeoutMs)
                       .GetAwaiter().GetResult() != null;
        }
        catch { return false; }
    }

    private void ReconcileStrictDnsFailover()
    {
        bool soleDriver = StrictDnsIsSoleDriver();
        var scan = _lastScan;
        if ((!soleDriver && !_strictDnsFailedOver) || scan is null)
            return;

        bool proxyOk = ProxyReachable();
        if (proxyOk) { _strictDnsHealthyStreak++; _strictDnsUnhealthyStreak = 0; }
        else { _strictDnsUnhealthyStreak++; _strictDnsHealthyStreak = 0; }

        bool effectiveHealthy = _strictDnsFailedOver
            ? _strictDnsHealthyStreak >= StrictDnsRecoverThreshold
            : _strictDnsUnhealthyStreak < StrictDnsFailThreshold;

        var action = StrictDnsFailoverPolicy.Decide(soleDriver, effectiveHealthy, _strictDnsFailedOver);
        if (action == StrictDnsAction.None)
            return;

        bool failOpen = action == StrictDnsAction.FailOpen;

        try
        {
            var configJson = GenerateConfigJson(scan.ProcessNames.ToList(),
                strictDnsFailedOverOverride: failOpen);
            var reloaded = TryHotReloadViaApi(configJson);

            if (!reloaded)
            {
                _logger.Warning(
                    "[HealthMonitor] StrictDns failover hot-reload FAILED (target fail-open={FailOpen}) — leaving state unchanged; will retry next tick",
                    failOpen);
                return;
            }

            _strictDnsFailedOver = failOpen;

            if (failOpen)
                _logger.Warning(
                    "[HealthMonitor] StrictDns auto-disabled (fail-open) — proxy unreachable after {N} probes; DNS failed over to direct resolver (local-dns) so the machine keeps internet",
                    _strictDnsUnhealthyStreak);
            else
                _logger.Information(
                    "[HealthMonitor] StrictDns re-armed — proxy reachable again after {N} probes; all DNS back through the tunnel",
                    _strictDnsHealthyStreak);

            StrictDnsFailoverChanged?.Invoke(this, failOpen);
        }
        catch (Exception)
        {
            _logger.Error("[HealthMonitor] StrictDns failover reload failed");
        }
    }

    private void QueueCrashedProcess(object? sender, EventArgs e)
    {
        if (e is SingBoxManager.ChildExit exit)
            _ = QueueCrashRecoveryAsync(exit.Child);
    }

    internal Task QueueCrashRecoveryAsync(IProcessHandle child)
    {
        var generation = Volatile.Read(ref _settingsGeneration);
        // Process disposal may wait for Exited subscribers. Never take the monitor lock on that callback.
        return Task.Run(() =>
        {
            lock (_settingsWork)
            {
                if (_disposed || _isStopping || _healthTimer == null || generation != _settingsGeneration ||
                    !ReferenceEquals(child, _singBox.OwnedProcessHandle)) return;
                RecoverCrashedProcess();
            }
        });
    }

    private void OnSingBoxCrashed(object? sender, EventArgs e) => RunForActiveSettings(RecoverCrashedProcess);

    private void RecoverCrashedProcess()
    {
        if (_isStopping)
            return;

        _unhealthyHealthProbeStreak = 0;
        _servingConfirmed = false; _wedgeStreak = 0;
        _vpnWasRunning = false;

        try { _firewall.EnableBlockRules(); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to enable firewall block rules on crash"); }

        // sing-box is gone: lift the DNS lockdown now (fail open) so the user is not stranded offline.
        try { _dnsHardening.ReconcileLockdownForHealth(false, _appSettings, _logger); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to lift DNS lockdown on crash"); }

        if (_singBox.LastCrashWasLinuxTunPermissionFailure)
        {
            _shouldBeRunning = false;
            _logger.Error(
                "[HealthMonitor] Automatic restart disabled: Linux denied TUNSETIFF. Reconnect after launching outside the restricting sandbox or granting host TUN privileges.");
            return;
        }

        if (_settings.RestartOnFailure)
            AttemptRestart();
    }

    private bool ShouldRestartAfterHealthProbeFailure()
    {
        _unhealthyHealthProbeStreak++;
        if (_unhealthyHealthProbeStreak < HealthProbeRestartFailThreshold)
        {
            _logger.Warning(
                "[HealthMonitor] Health failure {Count}/{Threshold} - delaying restart to avoid dropping active realtime UDP sessions on a transient probe blip",
                _unhealthyHealthProbeStreak,
                HealthProbeRestartFailThreshold);
            return false;
        }

        return true;
    }

    private void AttemptRestart()
    {
        int attempt;
        CancellationToken ct;
        int delayMs;
        bool requestFailover = false;
        lock (_attemptRestartLock)
        {
            if (_restartAttempts >= _settings.MaxRestartAttempts)
            {
                if (FailoverRequested != null && !_failoverRequested)
                {
                    _failoverRequested = true;
                    requestFailover = true;
                }
                else
                {
                    _logger.Error("[HealthMonitor] Max restart attempts ({Max}) reached — giving up",
                        _settings.MaxRestartAttempts);
                    return;
                }
            }

            if (requestFailover)
            {
                attempt = _restartAttempts;
                ct = CancellationToken.None;
                delayMs = 0;
            }
            else
            {
                _restartAttempts++;
                attempt = _restartAttempts;

                var oldCts = _restartCts;
                _restartCts = new CancellationTokenSource();
                ct = _restartCts.Token;
                if (oldCts != null)
                {
                    try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
                    oldCts.Dispose();
                }

                delayMs = (int)Math.Pow(2, attempt - 1) * 5000;
            }
        }

        if (requestFailover)
        {
            _logger.Error(
                "[HealthMonitor] Max restart attempts ({Max}) reached — requesting failover to a healthy server",
                _settings.MaxRestartAttempts);
            FailoverRequested?.Invoke(this, "max restart attempts reached");
            return;
        }

        RestartAttempted?.Invoke(this, attempt);

        _logger.Warning("[HealthMonitor] Restarting sing-box (attempt {N}/{Max}) in {Delay}ms",
            attempt, _settings.MaxRestartAttempts, delayMs);

        Task.Delay(delayMs, ct).ContinueWith(_ => RunForActiveSettings(() =>
        {
            if (ct.IsCancellationRequested || _isStopping) return;

            if (_singBox.IsRunning())
            {
                _logger.Debug("[HealthMonitor] sing-box already running — skipping scheduled restart");
                lock (_attemptRestartLock) { _restartAttempts = 0; }
                return;
            }

            try
            {
                var scan = _scanner.ScanForProfile(_activeProfile);
                var configJson = GenerateConfigJson(scan.ProcessNames);

                if (!RunTunOrphanRecoveryCleanup(ct)) return;

                if (ct.IsCancellationRequested || _isStopping)
                {
                    _logger.Information("[HealthMonitor] Stop requested during restart prep — aborting sing-box revival");
                    return;
                }

                var managedSingBoxRunning = _singBox.IsRunning();
                if (!managedSingBoxRunning && OperatingSystem.IsWindows() && ProcessOwnership.AnySingBoxOwned())
                {
                    _logger.Warning(
                        "[HealthMonitor] Managed sing-box handle is gone but a VPNRouter-owned sing-box is still running — killing orphan before full restart");
                    try { OrphanCleanup.KillOrphans(_logger, respectTunLock: false); }
                    catch (Exception cleanupEx) { _logger.Warning(cleanupEx, "[HealthMonitor] Orphan sing-box cleanup failed before full restart"); }
                }

                var hotReloaded = managedSingBoxRunning && TryHotReloadViaApi(configJson);
                if (!managedSingBoxRunning)
                    _logger.Warning("[HealthMonitor] Hot-reload skipped on restart attempt — no managed sing-box process; performing full restart");
                if (!hotReloaded)
                {
                    _logger.Warning("[HealthMonitor] Hot-reload unavailable on restart attempt — performing full restart");
                    if (_firewall is MacFirewallManager macFirewall)
                        macFirewall.EnableBlockRules();
                    try
                    {
                        var freshScan = _scanner.ScanForProfile(_activeProfile);
                        configJson = GenerateConfigJson(freshScan.ProcessNames);
                        scan = freshScan;
                        _singBox.ReloadConfigJson(configJson, forceRestart: true);
                    }
                    catch (Exception) when (!_singBox.HasActiveTunnel)
                    {
                        _logger.Warning(
                            "[HealthMonitor] Config regen before full restart failed — " +
                            "falling back to relaunch of last-good on-disk config");
                        _singBox.Restart();
                    }
                }
                _lastScan = scan;
                _lastFullRestart = DateTime.UtcNow;

                if (hotReloaded)
                {
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0);
                    try { _firewall.DisableBlockRules(); }
                    catch (Exception fwEx) { _logger.Error(fwEx, "[HealthMonitor] Failed to disable firewall block rules after hot-reload"); }
                }
                else
                {
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    _logger.Information("[HealthMonitor] Full restart done — deferring kill-switch lift until health confirmed");
                }

                _logger.Information("[HealthMonitor] sing-box restarted successfully");
            }
            catch (Exception ex)
            {
                _logger.Error("[HealthMonitor] Restart attempt {N} failed ({ExceptionType})", _restartAttempts, ex.GetType().Name);
            }
        }), CancellationToken.None);
    }

    internal bool RunTunOrphanRecoveryCleanup(CancellationToken ct)
    {
        if (!_singBox.LastCrashWasTunOrphan) return true;
        if (!OperatingSystem.IsWindows()) return true;

        _logger.Information(
            "[HealthMonitor] Previous crash was TUN orphan ('Cannot create a file when that file already exists'). " +
            "Force-disabling VPNRouter-TUN via netsh before retry.");

        try
        {
            var disabled = TunAdapterDiagnostics
                .TryDisableAdapterViaNetshAsync(
                    _logger, "VPNRouter-TUN",
                    "HealthMonitor.AttemptRestart.TunOrphan")
                .GetAwaiter().GetResult();

            if (!disabled)
            {
                _logger.Warning(
                    "[HealthMonitor] netsh disable failed — retry may also fail. " +
                    "User may need to manually disable VPNRouter-TUN in Network Connections " +
                    "OR install RSAT NetAdapter PowerShell module for reliable cleanup.");
            }

            Task.Delay(500, ct).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception netshEx)
        {
            _logger.Warning(netshEx,
                "[HealthMonitor] netsh disable for VPNRouter-TUN threw (non-fatal) — continuing with restart");
            return true;
        }
    }

    internal Func<string, bool>? HotReloadHookForTests { get; set; }

    private bool TryHotReloadViaApi(string configJson)
    {
        if (HotReloadHookForTests is { } hook)
            return hook(configJson);

        var path = _singBox.WriteConfigToDisk(configJson);
        try
        {
            return _api.ReloadConfigAsync(path).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            _logger.Debug("[HealthMonitor] ISingBoxApi.ReloadConfigAsync threw — returning false");
            return false;
        }
    }

    private void OnDebounceElapsed(object? state) => RunForActiveSettings(RescanProcesses);

    private void RescanProcesses()
    {
        if (_isStopping) return;

        try
        {
            _logger.Information("[HealthMonitor] Debounce elapsed — rescanning processes");

            var newScan = _scanner.ScanForProfile(_activeProfile);

            var newFiltered  = FilterForSingBox(newScan.ProcessNames);
            var prevFiltered = _lastScan == null ? null : FilterForSingBox(_lastScan.ProcessNames);

            if (prevFiltered != null &&
                new HashSet<string>(newFiltered, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(prevFiltered))
            {
                _logger.Debug("[HealthMonitor] No effective process changes — skipping reload");
                _lastScan = newScan;
                return;
            }

            _logger.Information("[HealthMonitor] Process list changed ({Prev} → {New} processes) — reloading sing-box config",
                prevFiltered?.Count ?? 0, newFiltered.Count);

            _restartCts?.Cancel();

            var configJson = GenerateConfigJson(newScan.ProcessNames);

            if (TryHotReloadViaApi(configJson))
            {
                _lastScan = newScan;
                _restartAttempts = 0;
                _logger.Information("[HealthMonitor] Hot-reload succeeded with {Count} processes", newFiltered.Count);
            }
            else
            {
                var sinceLastRestart = DateTime.UtcNow - _lastFullRestart;
                if (sinceLastRestart < RestartCooldown)
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed, but cooldown active ({Remaining}s left) — deferring full restart",
                        (int)(RestartCooldown - sinceLastRestart).TotalSeconds);
                    _lastScan = newScan;
                }
                else
                {
                    _logger.Warning("[HealthMonitor] Hot-reload failed — performing full restart");
                    _lastFullRestart = DateTime.UtcNow;
                    try { _firewall.EnableBlockRules(); }
                    catch (Exception fwEx) { _logger.Error(fwEx, "[HealthMonitor] Failed to enable block rules before debounce restart"); }
                    _singBox.Restart();
                    System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    _lastScan = newScan;
                    _restartAttempts = 0;
                    _logger.Information("[HealthMonitor] Full restart completed with {Count} processes", newFiltered.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[HealthMonitor] Error in debounced rescan ({ExceptionType})", ex.GetType().Name);
        }
    }

    private string GenerateConfigJson(List<string> processNames, bool? strictDnsFailedOverOverride = null)
    {
        var isCustom = (_appSettings.App.ConfigMode ?? "generated")
            .Equals("custom", StringComparison.OrdinalIgnoreCase);

        if (AcceptedImportJson != null || isCustom)
        {
            string CheckImport(string raw)
            {
                var json = CustomConfigInjector.Inject(raw, processNames, _appSettings,
                    ConfigGenerator.RejectMissingRuleSet, _singBox.CheckRuleSetAsync);
                if (!_singBox.CheckCandidateAsync(json, CancellationToken.None).GetAwaiter().GetResult())
                    throw new InvalidOperationException("The imported VPN configuration failed native validation.");
                return json;
            }

            if (AcceptedImportJson is { } accepted) return CheckImport(accepted);
            var (selectedPath, _) = VpnEngine.ResolveCustomConfigFiles(_appSettings, preferAccepted: true);
            if (File.Exists(selectedPath)) return CheckImport(CustomConfigInjector.ReadImport(selectedPath));
            throw new InvalidOperationException("The selected custom VPN configuration is missing.");
        }

        return ConfigPipeline.Generate(
            _activeProfile,
            processNames,
            _appSettings,
            ConfigPipeline.ValidationMode.Advisory,
            warningSink: null,
            logger: _logger,
            strictDnsOverride: (strictDnsFailedOverOverride ?? _strictDnsFailedOver) ? false : (bool?)null,
            onMissingRuleSet: ConfigGenerator.RejectMissingRuleSet,
            checkRuleSet: _singBox.CheckRuleSetAsync);
    }

    private static List<string> FilterForSingBox(IEnumerable<string> names) =>
        names.Where(p => !p.Contains('*') && !p.Contains('?'))
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        // Unsubscribe in Dispose, not Stop: the subscription is made once in the constructor.
        try { _singBox.Crashed -= QueueCrashedProcess; } catch { }

        _ownedApi?.Dispose();
    }
}
