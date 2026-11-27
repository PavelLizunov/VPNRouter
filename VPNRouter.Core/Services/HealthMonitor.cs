using System.Text.Json.Nodes;
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

    internal Action<AppSettings>? ReapplyUnixDnsOnRestart { get; set; }

    // The live session controller is authoritative across regenerated recovery
    // configs. On macOS the privileged contract mints a secret for a blank
    // document, so regenerating must reuse the accepted bearer, not a new one.
    private string? _activeControllerSecret;

    // A started replacement whose bounded tag check did not finish stays armed
    // until a later reconcile request confirms the controller serves the document.
    private string? _pendingConfigConfirmation;

    // A strict-DNS replacement that started unconfirmed must still update the
    // in-memory failover state once that same document is later accepted.
    private bool? _pendingStrictDnsFailOpen;

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
        _activeControllerSecret = clashApiSecret;

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
        _pendingConfigConfirmation = null;
        _pendingStrictDnsFailOpen = null;
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
        (Uri Endpoint, string? Secret)? controller = null, Action? commit = null,
        bool strictDnsFailedOver = false)
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

            if (controller is { } accepted)
            {
                _activeControllerSecret = accepted.Secret;
                _singBox.SetClashController(accepted);
            }
            if (candidateApi != null)
            {
                var oldApi = _ownedApi;
                _api = candidateApi;
                _ownedApi = candidateApi;
                oldApi?.Dispose();
            }
            _strictDnsFailedOver = strictDnsFailedOver;
            _strictDnsUnhealthyStreak = _strictDnsHealthyStreak = 0;
        }
    }

    internal bool IsMonitoring(SingBoxManager manager)
    {
        lock (_settingsWork)
            return ReferenceEquals(manager, _singBox) && _healthTimer != null && !_disposed &&
                !_isStopping && _shouldBeRunning;
    }

    internal bool PauseForApply()
    {
        lock (_settingsWork)
        {
            var strictDnsFailedOver = _pendingStrictDnsFailOpen ?? _strictDnsFailedOver;
            StopCore(preserveFirewallRelease: true);
            return strictDnsFailedOver;
        }
    }

    internal void ScheduleBlockRuleReleaseRetry()
    {
        if (_firewall is not MacFirewallManager) return;
        _lastFullRestart = DateTime.UtcNow;
        Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
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
        _pendingConfigConfirmation = null;
        _pendingStrictDnsFailOpen = null;

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
        if (_isStopping || _disposed || !UsesDynamicProfileScan(_appSettings)) return;
        if (IsProcessAlreadyTracked(_lastScan, processName))
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

                // A restart that started but could not finish its bounded tag
                // check is reconciled here: the controller is asked again for
                // the applied outbound tags before the block rules are lifted.
                if (_pendingConfigConfirmation is { } pending && clashServing &&
                    _singBox.VerifyAppliedConfigTags(pending, logFailure: false))
                {
                    CommitPendingConfigConfirmation();
                }

                var confirmationComplete = _pendingConfigConfirmation == null;
                var releaseReady = confirmationComplete ? clashServing || fallbackElapsed : clashServing && fallbackElapsed;
                if (releaseReady
                    && System.Threading.Interlocked.Exchange(ref _deferredBlockRuleDisable, 0) == 1)
                {
                    try
                    {
                        ReapplyUnixDnsOnRestart?.Invoke(_appSettings);
                        var released = TryReleaseBlockRules();
                        if (released)
                        {
                            // The fallback window elapsed with the started runtime
                            // still in place: accept its pending strict-DNS state too.
                            CommitPendingConfigConfirmation();
                            _logger.Information(
                                "[HealthMonitor] Kill-switch block rules lifted after restart (clashApi={Clash}, fallback={Fallback}, +{Secs:F0}s)",
                                clashServing, fallbackElapsed, sinceRestart.TotalSeconds);
                        }
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

        // Wait for the deferred controller confirmation before replacing the
        // runtime again; the health tick reconciles pending tags.
        if (_pendingConfigConfirmation != null)
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
            var state = ApplyConfigWithRestart(configJson);

            if (state == ConfigApplyState.StartedUnconfirmed)
            {
                // The new document is live even though the bounded tag check
                // timed out. Keep the target so confirmation commits it.
                _pendingStrictDnsFailOpen = failOpen;
                _logger.Warning(
                    "[HealthMonitor] StrictDns failover started but is not yet confirmed (target fail-open={FailOpen}) — retaining the previous in-memory state until the health reconcile accepts the document",
                    failOpen);
                return;
            }

            if (state == ConfigApplyState.NotStarted)
            {
                _logger.Warning(
                    "[HealthMonitor] StrictDns failover restart did not start (target fail-open={FailOpen}) — leaving state unchanged; will retry next tick",
                    failOpen);
                return;
            }

            _pendingStrictDnsFailOpen = null;
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
        _pendingConfigConfirmation = null;
        _pendingStrictDnsFailOpen = null;

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

        Task.Delay(delayMs, ct).ContinueWith(_ =>
            RunForActiveSettings(() => RunScheduledRestart(ct)), CancellationToken.None);
    }

    private void RunScheduledRestart(CancellationToken ct)
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

            // Never trust the legacy 204 reload: arm the kill switch, restart
            // with the regenerated document, and keep the block rules deferred
            // once the new runtime has started. A start which could not finish
            // its bounded tag check is still a started runtime, not a failure.
            try
            {
                var freshScan = _scanner.ScanForProfile(_activeProfile);
                configJson = GenerateConfigJson(freshScan.ProcessNames);
                scan = freshScan;
                _pendingStrictDnsFailOpen = null;
                var state = ApplyConfigWithProtection(configJson, releaseWhenNotStarted: false);
                if (state == ConfigApplyState.NotStarted)
                {
                    _logger.Warning(
                        "[HealthMonitor] Restart attempt {N} did not start a replacement — keeping the previous scan and kill-switch block rules",
                        _restartAttempts);
                    return;
                }
            }
            catch (Exception) when (!_singBox.HasActiveTunnel)
            {
                _logger.Warning(
                    "[HealthMonitor] Config regen before full restart failed — " +
                    "falling back to relaunch of last-good on-disk config");
                _singBox.Restart();
                _lastFullRestart = DateTime.UtcNow;
                Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                _logger.Information("[HealthMonitor] Fallback restart done — deferring kill-switch lift until health confirmed");
            }

            _lastScan = scan;
            _logger.Information("[HealthMonitor] Restart attempt {N} advanced the active scan", _restartAttempts);
        }
        catch (Exception ex)
        {
            _logger.Error("[HealthMonitor] Restart attempt {N} failed ({ExceptionType})", _restartAttempts, ex.GetType().Name);
        }
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

    internal Func<string, bool>? ReloadHookForTests { get; set; }

    internal Func<string, ConfigApplyState>? ReloadOutcomeHookForTests { get; set; }

    /// <summary>
    /// Applies a config with a real restart and the manager's bounded controller
    /// tag check. There is no hot-reload fallback: a legacy 204 response is not
    /// evidence that the document was applied.
    /// </summary>
    private ConfigApplyState ApplyConfigWithRestart(string configJson) =>
        ApplyConfigWithProtection(configJson, releaseWhenNotStarted: true);

    private ConfigApplyState ApplyConfigWithProtection(string configJson, bool releaseWhenNotStarted)
    {
        var previousRuntime = _singBox.CaptureRuntimeIdentity();

        // A replacement briefly stops the only runtime. Arm the configured
        // kill switch first so the gap cannot carry direct traffic.
        try { _firewall.EnableBlockRules(); }
        catch (Exception ex) { _logger.Error(ex, "[HealthMonitor] Failed to arm firewall block rules before a restart"); }

        var state = ApplyConfigWithRestartCore(configJson);
        if (state == ConfigApplyState.NotStarted)
        {
            // A failed replacement can happen after the old runtime was already
            // stopped. Lift only when the exact prior runtime is demonstrably
            // alive; otherwise keep the kill switch armed.
            if (releaseWhenNotStarted && Volatile.Read(ref _deferredBlockRuleDisable) == 0)
            {
                if (_singBox.IsRuntimeIdentityLive(previousRuntime))
                {
                    try
                    {
                        if (!TryReleaseBlockRules())
                        {
                            Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                            _logger.Warning("[HealthMonitor] Previous runtime survived the unstarted replacement but block-rule release was not confirmed — will retry on the health tick");
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                        _logger.Error(ex, "[HealthMonitor] Failed to release firewall block rules after an unstarted restart");
                    }
                }
                else
                {
                    _logger.Warning(
                        "[HealthMonitor] Replacement did not start and the previous runtime is not confirmed live — keeping the kill switch armed");
                }
            }
            return state;
        }

        _lastFullRestart = DateTime.UtcNow;
        if (state == ConfigApplyState.Confirmed)
        {
            _pendingConfigConfirmation = null;
            try
            {
                ReapplyUnixDnsOnRestart?.Invoke(_appSettings);
                if (TryReleaseBlockRules())
                {
                    Interlocked.Exchange(ref _deferredBlockRuleDisable, 0);
                    _logger.Information("[HealthMonitor] Config replacement confirmed — kill-switch block rules lifted");
                }
                else
                {
                    Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                    _logger.Warning("[HealthMonitor] Config replacement confirmed but block-rule release was not confirmed — deferring lift to next health tick");
                }
            }
            catch (Exception fwEx)
            {
                Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
                _logger.Error(fwEx, "[HealthMonitor] Config replacement confirmed but lifting kill-switch block rules threw — deferring lift to next health tick");
            }
        }
        else
        {
            Interlocked.Exchange(ref _deferredBlockRuleDisable, 1);
            _pendingConfigConfirmation = configJson;
            _logger.Warning(
                "[HealthMonitor] Restart started but controller tag confirmation did not finish — keeping the kill switch armed and deferring its lift to the next health reconcile");
        }
        return state;
    }

    private ConfigApplyState ApplyConfigWithRestartCore(string configJson)
    {
        if (ReloadOutcomeHookForTests is { } outcomeHook)
            return outcomeHook(configJson);
        if (ReloadHookForTests is { } hook)
            return hook(configJson) ? ConfigApplyState.Confirmed : ConfigApplyState.NotStarted;

        try
        {
            return _singBox.ReloadConfigJsonWithOutcome(configJson, forceRestart: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[HealthMonitor] Controlled restart threw ({ExceptionType})", ex.GetType().Name);
            return ConfigApplyState.NotStarted;
        }
    }

    private bool TryReleaseBlockRules()
    {
        if (_firewall is MacFirewallManager macFirewall)
            return macFirewall.TryDisableBlockRules();

        _firewall.DisableBlockRules();
        return true;
    }

    private void CommitPendingConfigConfirmation()
    {
        _pendingConfigConfirmation = null;
        if (_pendingStrictDnsFailOpen is not { } failOpen)
            return;

        _pendingStrictDnsFailOpen = null;
        if (_strictDnsFailedOver == failOpen)
            return;

        _strictDnsFailedOver = failOpen;
        if (failOpen)
        {
            _logger.Warning(
                "[HealthMonitor] Deferred StrictDns failover accepted (fail-open) — DNS stays on the direct resolver");
        }
        else
        {
            _logger.Information(
                "[HealthMonitor] Deferred StrictDns re-arm accepted — all DNS is back through the tunnel");
        }
        StrictDnsFailoverChanged?.Invoke(this, failOpen);
    }

    private void OnDebounceElapsed(object? state) => RunForActiveSettings(RescanProcesses);

    private void RescanProcesses()
    {
        if (_isStopping) return;

        if (!UsesDynamicProfileScan(_appSettings))
        {
            _logger.Debug("[HealthMonitor] Skipping process rescan — active routing mode does not use dynamic profile scanning");
            return;
        }

        try
        {
            _logger.Debug("[HealthMonitor] Debounce elapsed — rescanning processes");

            var newScan = _scanner.ScanForProfile(_activeProfile);
            var mergedNames = MergeTrackedProcessNames(_lastScan?.ProcessNames, newScan.ProcessNames);
            var mergedScan = new ScanResult
            {
                ProcessNames = mergedNames,
                ScannedAt = newScan.ScannedAt,
            };

            var newFiltered  = FilterForSingBox(mergedScan.ProcessNames);
            var prevFiltered = _lastScan == null ? null : FilterForSingBox(_lastScan.ProcessNames);

            _lastScan = mergedScan;

            if (prevFiltered != null &&
                new HashSet<string>(newFiltered, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(prevFiltered))
            {
                _logger.Debug("[HealthMonitor] No new processes discovered during rescan");
                return;
            }

            // sing-box has no non-disruptive config hot-reload (PUT /configs is a no-op).
            // Restarting the runtime on background process churn tears down VPNRouter-TUN
            // and arms the kill switch, dropping active games, voice calls, and TCP/UDP
            // sessions. Keep the live tunnel running and retain the expanded scan for
            // the next explicit Apply, reconnect, or crash recovery.
            _logger.Information(
                "[HealthMonitor] Process scan updated ({Prev} → {New} processes) — keeping live tunnel running without restart",
                prevFiltered?.Count ?? 0, newFiltered.Count);
        }
        catch (Exception ex)
        {
            _logger.Error("[HealthMonitor] Error in debounced rescan ({ExceptionType})", ex.GetType().Name);
        }
    }

    private static bool UsesDynamicProfileScan(AppSettings? settings)
    {
        var app = settings?.App;
        if (app is null) return false;
        if (string.Equals(app.RoutingMode, "full", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(app.RoutingAppsMode, "exclude", StringComparison.OrdinalIgnoreCase))
            return false;
        if (app.RoutingAppsIncludeInitialized || app.RoutingAppsInclude?.Count > 0)
            return false;
        return true;
    }

    private static bool IsProcessAlreadyTracked(ScanResult? scan, string processName)
    {
        if (scan?.ProcessNames == null || string.IsNullOrWhiteSpace(processName))
            return false;

        if (scan.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            return true;

        if (!processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            scan.ProcessNames.Contains(processName + ".exe", StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static List<string> MergeTrackedProcessNames(
        IEnumerable<string>? previous,
        IEnumerable<string> current)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (previous != null)
        {
            foreach (var name in previous)
            {
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    merged.Add(name);
            }
        }

        foreach (var name in current)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                merged.Add(name);
        }

        return merged;
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
                return PinActiveControllerSecret(json);
            }

            if (AcceptedImportJson is { } accepted) return CheckImport(accepted);
            var (selectedPath, _) = VpnEngine.ResolveCustomConfigFiles(_appSettings, preferAccepted: true);
            if (File.Exists(selectedPath)) return CheckImport(CustomConfigInjector.ReadImport(selectedPath));
            throw new InvalidOperationException("The selected custom VPN configuration is missing.");
        }

        var generated = ConfigPipeline.Generate(
            _activeProfile,
            processNames,
            _appSettings,
            ConfigPipeline.ValidationMode.Advisory,
            warningSink: null,
            logger: _logger,
            strictDnsOverride: (strictDnsFailedOverOverride ?? _strictDnsFailedOver) ? false : (bool?)null,
            onMissingRuleSet: ConfigGenerator.RejectMissingRuleSet,
            checkRuleSet: _singBox.CheckRuleSetAsync);
        return PinActiveControllerSecret(generated);
    }

    private string PinActiveControllerSecret(string configJson)
    {
        // A recovery document must keep the bearer the live controller accepted.
        // This is session memory only: the secret is never logged here.
        if (string.IsNullOrEmpty(_activeControllerSecret))
            return configJson;

        if (JsonNode.Parse(configJson) is not JsonObject root ||
            root["experimental"] is not JsonObject experimental ||
            experimental["clash_api"] is not JsonObject clashApi)
            throw new InvalidOperationException("The recovery configuration is missing the active controller.");

        clashApi["secret"] = _activeControllerSecret;
        return root.ToJsonString();
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
