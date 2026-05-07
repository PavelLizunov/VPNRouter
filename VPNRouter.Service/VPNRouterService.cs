using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.NetworkInformation;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.Service;

/// <summary>
/// Windows Service implementation.
/// On cold start: waits for network, then auto-starts VPN/Zapret/TgProxy
/// based on config flags. Defers to desktop UI if it's running.
///
/// <para><b>v2.27 §4.6 C2 — config.yaml write invariant:</b> the Service
/// MUST NOT write to <c>config.yaml</c>. The desktop App is the single
/// authoritative writer; the Service is a pure reader + <c>FileSystem
/// Watcher</c>-driven reconciler (see <see cref="SettingsLoader.Load"/>
/// callers below). Enforced by convention, verified by a grep audit on
/// every v2.27.x release — breaking this would reintroduce the race
/// between App's <c>SaveSettings()</c> and a Service write that was
/// called out in the plan. If a future feature needs Service-side
/// persistence, add a separate <c>service-state.json</c> file instead
/// of touching <c>config.yaml</c>.</para>
/// </summary>
public class VPNRouterService : BackgroundService
{
    private readonly ILogger<VPNRouterService> _logger;
    private VpnEngine? _engine;
    private ZapretManager? _zapret;
    private TgProxyManager? _tgProxy;

    // v2.26.0 — current in-memory settings snapshot. Was a local in
    // ExecuteAsync; promoted to a field so the SettingsLoader watcher
    // callback can mutate it and any post-startup flow (crash-restart,
    // hot-reload) uses up-to-date values instead of the stale copy we
    // read once at service boot.
    private AppSettings? _currentSettings;

    private readonly TaskCompletionSource _startupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private const string EventSourceName = "VPNRouter";
    private const string EventLogName = "Application";

    public VPNRouterService(ILogger<VPNRouterService> logger)
    {
        _logger = logger;
        EnsureEventSource();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Service] ExecuteAsync started");

        try
        {
            _currentSettings = SettingsLoader.Load();
            var settings = _currentSettings;
            _logger.LogInformation("[Service] Config loaded, mode: {Mode}", settings.App.ConfigMode);

            // v2.26.0 — watch config.yaml for changes made by the desktop
            // UI (or anyone else) and reconcile into in-memory state + a
            // running sing-box if we own one. Closes the gap where a user
            // changed routing_mode / subscription / apps in the UI but
            // the service's cached settings stayed stale, so any
            // subsequent crash-restart used outdated values.
            SettingsLoader.StartWatching(onReload: OnConfigChanged);

            // ── Step 0: Self-migrate pre-v2.14.12 installs (add boot dependencies) ──
            TryMigrateDependencies();

            // ── Step 1: Wait for network (cold boot — NIC may not be up yet) ──
            await WaitForNetworkAsync(stoppingToken, TimeSpan.FromSeconds(30));

            // ── Step 2: Auto-start VPN ──
            if (settings.App.AutostartVpn)
            {
                await AutostartVpnAsync(settings, stoppingToken);
            }
            else
            {
                _logger.LogInformation("[Service] AutostartVpn=false, skipping VPN");
                _startupComplete.TrySetResult();
            }

            // ── Step 3: Auto-start Zapret (independent, parallel) ──
            if (settings.App.AutostartZapret)
            {
                _ = AutostartZapretAsync(settings, stoppingToken);
            }

            // ── Step 4: Auto-start TgProxy (independent, parallel) ──
            if (settings.App.AutostartTgProxy)
            {
                _ = AutostartTgProxyAsync(settings, stoppingToken);
            }

            // Keep service alive until stop is requested
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Service] Stop requested");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[Service] Fatal error");
            WriteEventLog($"Fatal error: {ex.Message}", EventLogEntryType.Error);
            throw;
        }
        finally
        {
            _startupComplete.TrySetResult();
        }
    }

    /// <summary>
    /// Upgrade path: if service was installed by a pre-v2.14.12 binary, it
    /// lacks Tcpip/Dnscache/Dhcp dependencies. Add them now so next reboot
    /// uses the proper start order. Running as LocalSystem, so sc config
    /// succeeds without UAC prompt.
    /// </summary>
    private void TryMigrateDependencies()
    {
        try
        {
            var current = ServiceInstaller.GetDependencies();
            if (current != null &&
                current.Any(d => string.Equals(d, "Tcpip", StringComparison.OrdinalIgnoreCase)))
            {
                return;  // Already migrated
            }

            _logger.LogInformation("[Service] Migrating: adding Tcpip/Dnscache/Dhcp dependencies");
            var result = ServiceInstaller.UpdateDependencies();
            _logger.LogInformation("[Service] Migration result: {Message}", result.Message);
            if (result.Success)
            {
                WriteEventLog(
                    "Added boot dependencies (Tcpip/Dnscache/Dhcp). Takes effect on next reboot.",
                    EventLogEntryType.Information);
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: continue boot even if self-migration fails
            _logger.LogWarning(ex, "[Service] Dependency migration failed (non-fatal)");
        }
    }

    /// <summary>Wait for network adapter to become available (max timeout).</summary>
    private async Task WaitForNetworkAsync(CancellationToken ct, TimeSpan timeout)
    {
        if (NetworkInterface.GetIsNetworkAvailable())
        {
            _logger.LogInformation("[Service] Network available");
            return;
        }

        _logger.LogInformation("[Service] Waiting for network (max {Sec}s)...", timeout.TotalSeconds);
        var deadline = DateTime.UtcNow + timeout;

        while (!NetworkInterface.GetIsNetworkAvailable() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct);
        }

        if (NetworkInterface.GetIsNetworkAvailable())
            _logger.LogInformation("[Service] Network became available");
        else
            _logger.LogWarning("[Service] Network still unavailable after timeout, proceeding anyway");
    }

    /// <summary>Start VPN connection with subscription refresh + UI deference.</summary>
    private async Task AutostartVpnAsync(AppSettings settings, CancellationToken ct)
    {
        _engine = new VpnEngine(
            new ProcessScanner(Serilog.Log.Logger),
            () => new FirewallManager(Serilog.Log.Logger),
            () => new EtwProcessMonitor(Serilog.Log.Logger),
            Serilog.Log.Logger);

        _engine.StatusChanged += msg =>
            _logger.LogInformation("[Service] {Status}", msg);
        _engine.RestartAttempted += (attempt, max) =>
            WriteEventLog($"sing-box restart attempt {attempt}/{max}", EventLogEntryType.Warning);
        _engine.Warning += msg =>
            _logger.LogWarning("[Service] {Warn}", msg);

        // Subscription mode: refresh + aggregate via shared resolver so Service,
        // CLI and GUI use the same bootstrap path. Mutates settings in place
        // (flips ConfigMode → "generated" when at least one server is resolved).
        await SubscriptionResolver.ResolveAsync(
            settings,
            refreshFromNetwork: true,
            Serilog.Log.Logger,
            ct);

        // v2.26.1 — Pre-flight: check the TUN ownership lock BEFORE trying
        // to start sing-box. If some other VPNRouter process (desktop App,
        // CLI session, leftover from a previous run) already owns it,
        // transition to watcher mode immediately instead of entering a
        // 30-second retry loop that burns CPU on every tick.
        //
        // Why TunLock beats the old `Process.GetProcessesByName("VPNRouter.App")`
        // check:
        //   1. Catches ALL sing-box owners — CLI and external instances
        //      too, not just the GUI process name.
        //   2. Atomic: the kernel releases the semaphore the instant the
        //      holder dies, so we can't race a ghost process.
        //   3. Free to poll — no Process enumeration overhead.
        //
        // Watcher mode: release startup completion, then park. The file-
        // watcher on config.yaml is still active and can hot-reload, so
        // the user's settings changes still land on the service even
        // though the service isn't the one running sing-box.
        if (TunOwnershipLock.IsOwnedByAnyone())
        {
            _logger.LogInformation(
                "[Service] TUN already owned by another VPNRouter process — " +
                "entering watcher mode, will not contend for sing-box.");
            _startupComplete.TrySetResult();
            return;
        }

        // ResilientStarter handles transient failures (5/10/20/40s backoff).
        // If TunOwnershipException bubbles up (someone grabbed the lock
        // between our peek above and our start), we still catch it and
        // transition to watcher mode rather than looping.
        var vpnStarted = false;
        try
        {
            vpnStarted = await ResilientStarter.StartWithBackoffAsync(
                componentName: "VPN",
                startFn: innerCt => _engine.StartAsync(settings, innerCt),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (!vpnStarted)
            {
                _logger.LogError(
                    "[Service] VPN autostart failed after retries. Not retrying further — " +
                    "Zapret/TgProxy will still attempt to start.");
                WriteEventLog(
                    "VPN autostart failed after retries",
                    EventLogEntryType.Error);
            }
        }
        catch (TunOwnershipException)
        {
            // Race with another VPNRouter process between our IsOwnedByAnyone
            // pre-check and sing-box startup. Accept the loss silently —
            // they're serving the user just fine, we'll watch.
            _logger.LogInformation(
                "[Service] TUN adapter acquired by another process mid-start — " +
                "entering watcher mode.");
        }

        _startupComplete.TrySetResult();

        if (vpnStarted)
        {
            WriteEventLog(
                $"VPN started — profile: {_engine.ActiveProfileName}, PID: {_engine.SingBoxPid}",
                EventLogEntryType.Information);
        }
    }

    /// <summary>Start Zapret DPI bypass (independent of VPN).</summary>
    private async Task AutostartZapretAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            if (!ZapretUpdater.IsInstalled())
            {
                _logger.LogWarning("[Service] Zapret not installed, skipping autostart");
                return;
            }

            var strategyName = settings.App.ZapretStrategy ?? "multisplit";
            string args;

            if (strategyName == "custom")
            {
                args = settings.App.ZapretCustomArgs;
            }
            else if (strategyName == "multisplit" || strategyName == "fake+multisplit")
            {
                args = ZapretManager.BuildLegacyArgs(strategyName);
            }
            else
            {
                var strategies = ZapretUpdater.ParseStrategies();
                var parsed = strategies.FirstOrDefault(s => s.Name == strategyName);
                args = parsed?.Arguments ?? ZapretManager.BuildLegacyArgs("multisplit");
            }

            _zapret = new ZapretManager(Serilog.Log.Logger);

            var started = await ResilientStarter.StartWithBackoffAsync(
                componentName: "Zapret",
                startFn: () => _zapret.Start(args),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (started)
            {
                _logger.LogInformation("[Service] Zapret started [{Strategy}] (PID {Pid})",
                    strategyName, _zapret.Pid);
                WriteEventLog($"Zapret started: {strategyName}", EventLogEntryType.Information);
            }
            else
            {
                _logger.LogError("[Service] Zapret autostart failed after retries");
                WriteEventLog(
                    $"Zapret autostart failed after retries ({strategyName})",
                    EventLogEntryType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping — swallow
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] Zapret autostart failed");
        }
    }

    /// <summary>Start TgProxy Telegram proxy (independent of VPN).</summary>
    private async Task AutostartTgProxyAsync(AppSettings settings, CancellationToken ct)
    {
        // v2.31.10 — explicit entry breadcrumb. Pre-fix the autostart could
        // exit silently down any of three early-return branches (not
        // installed, no secret, exception) and the only Service log we got
        // was "TgProxy not installed" with no path / no scope, leaving us
        // unable to tell whether the method even fired.
        _logger.LogInformation("[Service] AutostartTgProxyAsync: entered");
        try
        {
            // v2.31.10-r5 (DBG-1 + DBG-4) — combined fix:
            // (a) Probe with structured logging via the new IsInstalled(logger)
            //     overload — emits paths + per-component existence so a missing
            //     dir (proxy/ removed by user, Python never finished install) is
            //     visible immediately in Service log.
            // (b) ALSO surface the skip reason to Windows Event Log (Source:
            //     VPNRouter). Pre-r5 these warnings only landed in the file log,
            //     so users reporting "autostart doesn't work" had no signal in
            //     Event Viewer pointing at the actual cause. App-side fix in r5
            //     generates the secret on toggle, but legacy installs may still
            //     hit IsInstalled=false.
            if (!TgProxyUpdater.IsInstalled(Serilog.Log.Logger))
            {
                _logger.LogWarning("[Service] TgProxy not installed, skipping autostart");
                WriteEventLog(
                    "TgProxy autostart skipped: tg-ws-proxy is not installed. " +
                    "Open the Telegram tab in the desktop app and click Install.",
                    EventLogEntryType.Warning);
                return;
            }

            var port = settings.App.TgProxyPort > 0 ? settings.App.TgProxyPort : 1443;
            var secret = settings.App.TgProxySecret;

            if (string.IsNullOrWhiteSpace(secret))
            {
                _logger.LogWarning("[Service] TgProxy secret not configured, skipping");
                WriteEventLog(
                    "TgProxy autostart skipped: tg_proxy_secret is empty in config.yaml. " +
                    "Toggle the autostart checkbox once in v2.31.10-r5+ to auto-generate, " +
                    "or click Start in the Telegram tab to generate via the older path.",
                    EventLogEntryType.Warning);
                return;
            }

            _logger.LogInformation(
                "[Service] AutostartTgProxyAsync: secret configured (len {SecretLen}), port {Port} chosen, handing to ResilientStarter",
                secret.Length, port);

            _tgProxy = new TgProxyManager(Serilog.Log.Logger);

            var started = await ResilientStarter.StartWithBackoffAsync(
                componentName: "TgProxy",
                startFn: () => _tgProxy.Start(port, secret),
                logger: Serilog.Log.Logger,
                ct: ct);

            if (started)
            {
                _logger.LogInformation("[Service] TgProxy started on port {Port} (PID {Pid})",
                    port, _tgProxy.Pid);
                WriteEventLog($"TgProxy started on port {port}", EventLogEntryType.Information);
            }
            else
            {
                _logger.LogError("[Service] TgProxy autostart failed after retries");
                WriteEventLog(
                    $"TgProxy autostart failed after retries (port {port})",
                    EventLogEntryType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Service stopping — swallow
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Service] TgProxy autostart failed");
        }
    }

    /// <summary>
    /// v2.26.0 — FileSystemWatcher callback triggered on every external
    /// config.yaml write (with 2 s debounce built into SettingsLoader).
    /// Desktop UI is the primary writer; this method delivers the new
    /// settings to the live service without a service restart.
    /// </summary>
    private void OnConfigChanged(AppSettings newSettings)
    {
        try
        {
            _currentSettings = newSettings;
            _logger.LogInformation("[Service] config.yaml changed → settings reconciled");

            // If we're currently holding sing-box, hot-reload with the
            // fresh settings. VpnEngine.ApplyAsync tries Clash API first
            // (TUN stays up) and falls back to full restart only when the
            // change requires it (routing_mode flip, bypass-ru toggle).
            if (_engine != null && _engine.IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _engine.ApplyAsync(newSettings);
                        _logger.LogInformation(
                            "[Service] Hot-reload {Result} after config change",
                            ok ? "succeeded" : "failed (kept previous config)");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Service] ApplyAsync raised");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Service] OnConfigChanged error (non-fatal)");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Service] Stopping...");
        WriteEventLog("VPN Router service stopping", EventLogEntryType.Information);

        // v2.26.0 — release the FileSystemWatcher before tearing everything
        // else down, so a last-second config.yaml write during shutdown
        // doesn't queue an ApplyAsync against a disposed _engine.
        try { SettingsLoader.StopWatching(); } catch { }

        try
        {
            await Task.WhenAny(_startupComplete.Task, Task.Delay(15000, cancellationToken));
        }
        catch (OperationCanceledException) { }

        // v2.31.6-r9 — preserve the "all components must be stopped even if
        // one throws" invariant but log the swallowed exception instead of
        // discarding silently. Pre-r9 a sing-box stuck on Kill() during
        // shutdown produced an empty-catch swallow + zero diagnostics; now
        // operators can see what failed in Event Log.
        try { _engine?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _engine.Stop failed (non-fatal)"); }
        try { _engine?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _engine.Dispose failed (non-fatal)"); }
        try { _zapret?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _zapret.Stop failed (non-fatal)"); }
        try { _zapret?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _zapret.Dispose failed (non-fatal)"); }
        try { _tgProxy?.Stop(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _tgProxy.Stop failed (non-fatal)"); }
        try { _tgProxy?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Service] _tgProxy.Dispose failed (non-fatal)"); }

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("[Service] Stopped");
        WriteEventLog("VPN Router service stopped", EventLogEntryType.Information);
    }

    private static void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
                EventLog.CreateEventSource(EventSourceName, EventLogName);
        }
        catch { }
    }

    private static void WriteEventLog(string message, EventLogEntryType type)
    {
        try { EventLog.WriteEntry(EventSourceName, message, type); }
        catch { }
    }
}
