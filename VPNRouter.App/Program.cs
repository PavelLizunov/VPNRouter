using Avalonia;
using Serilog;
using System;
using VPNRouter.Core.Platform;
using VPNRouter.Core.Services;
#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Security.Principal;
#endif

namespace VPNRouter.App;

sealed class Program
{
    /// <summary>True when launched with --minimized (autostart, starts hidden in tray).</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>v2.29.0-r7+ Layer 7 — receipt-derived "previous update
    /// didn't take effect" warning, picked up by MainWindowViewModel
    /// constructor and bound to a dismissible banner. Empty / null when
    /// the previous update applied correctly.</summary>
    public static string? PendingUpdateWarning { get; set; }

    /// <summary>
    /// True when launched with --safe. Bypasses user overrides entirely:
    /// yaml ProfileSources, CustomCategories, CustomGroupApps, CustomApps,
    /// ActiveProfile are all ignored. VPN starts in Full tunnel mode with
    /// bundled-only catalogue. Last-resort recovery path when a corrupt
    /// user config is preventing the UI from starting normally.
    /// </summary>
    public static bool SafeMode { get; private set; }

    /// <summary>
    /// v2.38.0 — when this (first) instance was launched by the Explorer
    /// "route through VPN" context-menu verb (<c>--route-app "&lt;path&gt;"</c>)
    /// and no other instance was running to hand it to, the path is stashed
    /// here for the App to process once the ViewModel is up. Cleared after.
    /// </summary>
    internal static string? PendingRouteAppPath { get; set; }

    /// <summary>v2.38.0-r4 — optional target category for the pending route-app
    /// request (the cascading "VPNRouter ▸" submenu picks a category). Null =
    /// default "Custom Apps" group.</summary>
    internal static string? PendingRouteAppCategory { get; set; }

    /// <summary>v2.38.0-r5 — when this (first) instance was launched by the
    /// Explorer "remove from VPN" context-menu verb (<c>--unroute-app
    /// "&lt;path&gt;"</c>) with no other instance to hand it to. Cleared after.</summary>
    internal static string? PendingUnrouteAppPath { get; set; }

    /// <summary>Extract the value following <paramref name="flag"/>, if present.</summary>
    private static string? TryGetArgValue(string[] args, string flag)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // v2.24.0 self-healing: install crash reporter before anything
        // else. Writes crash-<stamp>.txt into %DataDir%/crashes/ on any
        // unhandled exception so the user has something to attach to a
        // bug report without scouring the logs themselves.
        VPNRouter.Core.Services.CrashReporter.Install();

        StartMinimized = args.Contains("--minimized");
        SafeMode = args.Contains("--safe");

        // Flip the Core-level flag so services below the App layer
        // (SettingsLoader, VpnEngine) see it without having to thread
        // parameters through every call site.
        VPNRouter.Core.Services.SafeMode.Enabled = SafeMode;

        // v2.24.2 defensive backup: entering Safe Mode, snapshot the
        // current config.yaml as config.yaml.backup-before-safemode-<stamp>
        // BEFORE anything could touch it. The Save() no-op from the
        // SafeMode.Enabled check should prevent overwrites, but a
        // second layer of defence doesn't hurt. Skipped in normal mode.
        if (SafeMode)
        {
            try
            {
                var cfg = VPNRouter.Core.AppPaths.ConfigYamlPath;
                if (System.IO.File.Exists(cfg))
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var backup = $"{cfg}.backup-before-safemode-{stamp}";
                    if (!System.IO.File.Exists(backup))
                        System.IO.File.Copy(cfg, backup);
                }
            }
            catch { /* non-fatal */ }
        }

        // v2.23.0: --reset wipes user config to factory defaults and
        // exits BEFORE any Avalonia startup. The next normal launch
        // will hit the "no config file" path and create a fresh one.
        // A timestamped backup is dropped next to the original. This
        // is the last-resort recovery path when even --safe can't get
        // the app running (e.g. config triggered a crash before UI).
        if (args.Contains("--reset"))
        {
            try
            {
                var backup = VPNRouter.Core.Services.SettingsLoader.ResetToDefaults();
                var msg = backup == null
                    ? "VPNRouter config reset: no prior config existed, defaults written."
                    : $"VPNRouter config reset complete.\r\nPrevious config backed up to: {backup}";
                Console.WriteLine(msg);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"VPNRouter --reset failed: {ex.Message}");
                Environment.Exit(1);
            }
            Environment.Exit(0);
        }

#if PLATFORM_WINDOWS
        // Auto-elevate to admin (required for TUN + ETW + Firewall).
        // If elevation fails (UAC declined, policy-blocked, etc.) write a
        // crash-file and emit to stderr so the user can see WHY nothing
        // happened — silent exit was the hardest v2.15.5 bug to diagnose.
        if (OperatingSystem.IsWindows() && !IsAdmin())
        {
            Exception? elevationError = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = string.Join(" ", args.Select(a => $"\"{a}\"")),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                elevationError = ex;
            }

            if (elevationError != null)
            {
                var msg =
                    "VPNRouter failed to elevate to administrator.\r\n" +
                    $"Reason: {elevationError.GetType().Name}: {elevationError.Message}\r\n" +
                    "Try: right-click VPNRouter.App.exe → Run as administrator.";
                try
                {
                    var crashPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "VPNRouter", "logs", "vpnrouter-launch-error.log");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashPath)!);
                    System.IO.File.AppendAllText(crashPath, $"[{DateTime.Now:O}] {msg}\r\n");
                }
                catch { }
                try { Console.Error.WriteLine(msg); } catch { }
            }
            return;
        }
#endif

        // BR-6b (audit 2026-05-20) — initialise the static Serilog
        // `Log.Logger` early so every downstream caller that does
        // `Serilog.Log.Logger?.Information(...)` actually writes to
        // vpnrouter*.log. Pre-r10 the static was never assigned: the
        // MainWindowViewModel ctor created an instance `_logger` for
        // its own use but Log.Logger stayed at the SilentLogger
        // default. That silently no-op'd:
        //
        //   * BR-3 (r7) SettingsLoader.LoadCore diagnostic mirror —
        //     the whole point of r7 was to surface the post-load
        //     {schema, subs, vless.servers} snapshot in user-shared
        //     logs. With Log.Logger silent, the mirror wrote to
        //     /dev/null on App.exe; brat's 23:29-23:33 logs confirmed
        //     zero `[SettingsLoader] Loaded …` lines.
        //
        //   * SettingsMigrator.Migrate diagnostic lines (called via
        //     SettingsLoader with a null logger that defaulted to
        //     Log.Logger inside the migrator) — same silent-default
        //     fate.
        //
        // Initialise it here, AFTER admin elevation but BEFORE
        // SettingsLoader.Load can be triggered by any sub-system
        // (LaunchFailureCounter doesn't load settings; service
        // binPath heal doesn't load settings; the first
        // SettingsLoader.Load is in MainWindowViewModel ctor far
        // below). Writes to the same vpnrouter.log file that the
        // VM-instance _logger writes to — Serilog's File sink handles
        // concurrent writers from one process correctly.
        try
        {
            VPNRouter.Core.AppPaths.EnsureDirectories();
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .WriteTo.File(
                    System.IO.Path.Combine(VPNRouter.Core.AppPaths.LogsDir, "vpnrouter.log"),
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .WriteTo.Console()
                .CreateLogger();
        }
        catch (Exception ex)
        {
            // Logger init failure must NOT block app startup. Fall
            // through with the SilentLogger default (same as pre-r10
            // behaviour). Surface to stderr so a CLI run shows it.
            try { Console.Error.WriteLine($"[serilog] init failed: {ex.Message}"); } catch { }
        }

        // v2.32.0 — launch-failure counter. Cross-platform: persists
        // a strike count in launch-counter.json next to config.yaml,
        // resets on MainWindow.Opened (see MainWindow.axaml.cs), and
        // graduates recovery (SelfRepair → config reset → Safe Mode
        // prompt) when chronic startup loops are detected. Wired here
        // — after Windows admin elevation, before any heavy work
        // (binPath self-heal, InstallHealthCheck, DLL-loading paths)
        // — so a crash from any of those still gets counted as a
        // strike. Wrapped in try/catch — a JSON I/O hiccup on the
        // counter must never block app startup.
        try
        {
            var recoveryAction = VPNRouter.Core.Services.LaunchFailureCounter.RecommendAction();
            if (recoveryAction != "none")
            {
                DispatchLaunchRecovery(recoveryAction);
            }
            VPNRouter.Core.Services.LaunchFailureCounter.IncrementOnStartup();
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[launch-counter] {ex.Message}"); } catch { }
        }

#if PLATFORM_WINDOWS
        // v2.26.0 — service binPath self-heal (Windows only). Analog of the
        // Run-key fix above but for `sc config VPNRouter binPath=`. Non-
        // disruptive: just reconfigures the service, change takes effect on
        // next service start. No-op when service isn't installed.
        try
        {
            var healResult = VPNRouter.App.Services.WindowsServiceHelper.EnsureCurrentBinPath();
            if (healResult.Success && healResult.Message.StartsWith("binPath updated", StringComparison.OrdinalIgnoreCase))
            {
                try { Console.Error.WriteLine($"[service-heal] {healResult.Message}"); }
                catch { }
            }
        }
        catch { /* never block app startup over a cosmetic sc.exe fix */ }

        // v2.31.8-r1 — install health check (mixed-version DLL detection).
        // BEFORE single-instance / heavy DLL loading, verify all
        // VPNRouter.*.dll on disk share the same source-commit hash. If
        // they don't, the user landed in the auto-update-with-Service-
        // running trap from pre-v2.31.7 (Bug 2): old broken updater
        // kept Service running, xcopy /R skipped Service-locked files,
        // result was mixed-version DLLs that crash or silently report
        // the old AppVersion.
        //
        // v2.31.10-r2 Task E: rollback FIRST, SelfRepair second. Local
        // app.bak/ snapshot (taken pre-update by ApplyUpdateWindows) is
        // a faster, network-free, AMSI-free recovery path. SelfRepair
        // remains the second-line fallback when the snapshot is absent
        // or itself damaged. See plans/v2.31.10-update-rollback.md.
        try
        {
            var health = VPNRouter.App.Services.InstallHealthCheck.Check();
            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var installDir = System.IO.Path.GetDirectoryName(appDir) ?? string.Empty;
            var failureMarkerPresent =
                !string.IsNullOrEmpty(installDir) &&
                VPNRouter.Core.Services.UpdateBackup.HasFailureMarker(installDir);

            if (!health.IsHealthy || failureMarkerPresent)
            {
                var trigger = failureMarkerPresent
                    ? $"helper.cmd .update-failed marker present ({VPNRouter.Core.Services.UpdateBackup.ReadFailureMarker(installDir)})"
                    : health.Diagnostic;
                Console.Error.WriteLine($"[health] {trigger} — attempting local rollback first");

                var rollback = !string.IsNullOrEmpty(installDir)
                    ? VPNRouter.Core.Services.UpdateBackup.RestoreSnapshot(installDir)
                    : new VPNRouter.Core.Services.UpdateBackup.RestoreResult(false, "no installDir");

                if (rollback.Restored)
                {
                    Console.Error.WriteLine($"[health] rollback ok: {rollback.Reason} — relaunching with restored binaries");
                    VPNRouter.Core.Services.UpdateBackup.ClearFailureMarker(installDir);
                    try
                    {
                        // Relaunch self so the freshly-restored DLLs are
                        // loaded fresh. Detached so our exit doesn't kill
                        // the new instance.
                        var guiExe = System.IO.Path.Combine(appDir, "VPNRouter.GUI.exe");
                        var exeToLaunch = System.IO.File.Exists(guiExe)
                            ? guiExe
                            : System.IO.Path.Combine(appDir, "VPNRouter.App.exe");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exeToLaunch,
                            UseShellExecute = true,
                            WorkingDirectory = appDir,
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[health] post-rollback relaunch failed: {ex.Message}");
                    }
                    return;
                }

                Console.Error.WriteLine($"[health] rollback declined: {rollback.Reason} — falling back to SelfRepair");
                var plan = VPNRouter.App.Services.SelfRepair.Plan();
                if (plan.ShouldRun)
                {
                    VPNRouter.App.Services.SelfRepair.Run();
                    return;
                }
                Console.Error.WriteLine($"[health] self-repair declined: {plan.Reason}");
                // Fall through — let the user see the broken state
                // instead of looping. CrashReporter will catch any DLL
                // mismatch crash.
            }
            else
            {
                // Healthy launch — schedule snapshot cleanup. The pre-
                // update snapshot served its purpose; deleting it
                // reclaims ~50–60 MB. Done in background so a slow
                // recursive delete (large DLL set, AV scanning) doesn't
                // delay window-up. We DO clear the failure marker even
                // here so a stale marker from a manual recovery doesn't
                // trigger rollback on the next launch.
                if (!string.IsNullOrEmpty(installDir))
                {
                    VPNRouter.Core.Services.UpdateBackup.ClearFailureMarker(installDir);
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // 30 s grace period — if the user crashes in
                            // that window we still have a usable snapshot
                            // for the next launch's rollback path.
                            System.Threading.Thread.Sleep(TimeSpan.FromSeconds(30));
                            VPNRouter.Core.Services.UpdateBackup.DeleteSnapshot(installDir);
                        }
                        catch { /* best-effort cleanup */ }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            // Health check / rollback / self-repair must NEVER block
            // app start outright. Worst case: log and continue.
            try { Console.Error.WriteLine($"[health] check failed: {ex.Message}"); } catch { }
        }

        // v2.31.7-r2 — single-instance enforcement. Replaces the brutal
        // OrphanCleanup-killing-VPNRouter.App approach. If a second
        // launch happens (user clicks taskbar / Start Menu shortcut /
        // autostart fired again / explorer relaunched), signal the
        // existing instance to surface and exit silently. spark-wraith
        // 2026-05-04: «не открывается, не показывается нигде» traced to
        // the kill-and-restart cycle interacting badly with Windows
        // ForegroundLockTimeout — the fresh window often didn't reach
        // foreground.
        // v2.38.0 — Explorer "route through VPN" context-menu verb. If invoked
        // with --route-app "<path>", hand it to an already-running instance and
        // exit; otherwise stash it so this (first) instance processes it once
        // the ViewModel is up (App.OnFrameworkInitializationCompleted).
        var routeAppPath = TryGetArgValue(args, "--route-app");
        if (routeAppPath != null)
        {
            var routeAppCategory = TryGetArgValue(args, "--category"); // r4: optional target category
            if (VPNRouter.App.Services.SingleInstance.TrySendRouteAppToRunningInstance(routeAppPath, routeAppCategory, Serilog.Log.Logger))
                return; // running instance received it — nothing more to do
            PendingRouteAppPath = routeAppPath; // we'll be the first instance
            PendingRouteAppCategory = routeAppCategory;
        }

        // v2.38.0-r5 — Explorer "remove from VPN" verb (--unroute-app "<path>").
        var unrouteAppPath = TryGetArgValue(args, "--unroute-app");
        if (unrouteAppPath != null)
        {
            if (VPNRouter.App.Services.SingleInstance.TrySendUnrouteAppToRunningInstance(unrouteAppPath, Serilog.Log.Logger))
                return; // running instance received it
            PendingUnrouteAppPath = unrouteAppPath; // we'll be the first instance
        }

        if (!VPNRouter.App.Services.SingleInstance.TryAcquireOrSignal(Serilog.Log.Logger))
            return;

        // Defensive cleanup: kill orphan sing-box left behind by failed
        // updates or hard crashes. After r2 the Mutex prevents twin
        // VPNRouter.App instances, but a stale sing-box (started by a
        // previous Service or crashed parent) can still linger.
        try { OrphanCleanup.KillOrphans(); } catch { }

        // Clean leftover firewall kill-switch rules that may block internet
        // after improper shutdown (ERR_NETWORK_ACCESS_DENIED symptom).
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var fw = new FirewallManager(Serilog.Log.Logger ?? new Serilog.LoggerConfiguration().CreateLogger());
                fw.CleanupOrphanedRules();
            }
        }
        catch { }

        // v2.40.0-r10 #2 (core-audit): also sweep on process exit, so an
        // abnormal teardown that skips the engine's clean DeleteAllRules
        // doesn't strand the user with kill-switch block rules still blocking
        // the internet until the NEXT launch. Gated on !IsOwnedByAnyone() so
        // that in background-service mode — where the Windows Service owns the
        // TUN and the block rules while this GUI is just a control panel —
        // closing the GUI window never nukes the Service's live rules. The
        // startup sweep above remains the fail-closed backstop for the case
        // where this process exits abnormally while still holding the lock.
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (OperatingSystem.IsWindows()
                        && !VPNRouter.Core.Services.TunOwnershipLock.IsOwnedByAnyone())
                    {
                        FirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);
                    }
                }
                catch { }
            };
        }
        catch { }

        // v2.38.0 — the Explorer "route through VPN" context-menu verb is
        // registered in App.axaml.cs AFTER the ViewModel loads settings, so
        // Strings.Lang is already "ru"/"en" and the menu label is localized
        // correctly. (r1 registered it here, before settings load, which
        // pinned the English label even for RU users — user report 2026-05-28.)
#endif

#if PLATFORM_WINDOWS
        // v2.31.9-r1 — Start Menu shortcut self-heal. install.ps1 pre-r1
        // wrote shortcuts targeting VPNRouter.App.exe directly, bypassing
        // the trampoline integrity check on every daily launch. Existing
        // users upgrading via in-app Update don't get a fresh install.ps1
        // pass (helper.cmd doesn't touch the shortcut), so we patch on
        // their first v2.31.9+ launch. Idempotent + try/catch — never
        // blocks startup.
        try
        {
            if (VPNRouter.App.Services.ShortcutSelfHeal.EnsureTrampolineTarget())
            {
                try { Console.Error.WriteLine("[shortcut] Start Menu shortcut migrated to VPNRouter.GUI.exe (trampoline)"); }
                catch { }
            }
        }
        catch { /* never block app startup over a cosmetic shortcut fix */ }
#endif

        // v2.25.13 — autostart path self-heal. v2.29.0 extended to Mac+Linux.
        // If user enabled "Start with system" at an earlier install location
        // and later reinstalled / moved the binary, the autostart entry
        // (HKCU\Run on Win, ~/Library/LaunchAgents/*.plist on Mac,
        // ~/.config/autostart/*.desktop on Linux) still holds the stale
        // ghost path — silent fail at next login. Every startup we verify
        // the stored path matches the currently-running exe and rewrite if
        // it doesn't. No-op when autostart is disabled.
        // (Moved out of #if PLATFORM_WINDOWS in v2.29.0-r2 — AutostartHelper
        // now dispatches Win/Mac/Linux internally.)
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && AutostartHelper.EnsureCurrentPath(exe))
            {
                try { Console.Error.WriteLine($"[autostart] entry rewritten -> {exe}"); }
                catch { }
            }
        }
        catch { /* never block app startup over a cosmetic autostart fix */ }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

#if PLATFORM_WINDOWS
    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
#endif

    /// <summary>
    /// v2.32.0 — graduated startup-loop recovery. Called by
    /// <see cref="Main"/> when <see cref="VPNRouter.Core.Services.LaunchFailureCounter.RecommendAction"/>
    /// surfaces a non-"none" action. Each branch is best-effort and
    /// must NEVER throw out — the counter has already stamped its
    /// cooldown so a relaunch within the next 10 min won't re-trigger
    /// the same tier; this method's job is only to attempt the
    /// recovery once.
    /// </summary>
    private static void DispatchLaunchRecovery(string action)
    {
        switch (action)
        {
            case "self-repair":
#if PLATFORM_WINDOWS
                try
                {
                    var plan = VPNRouter.App.Services.SelfRepair.Plan();
                    if (plan.ShouldRun)
                    {
                        try { Console.Error.WriteLine("[launch-counter] 3 strikes — triggering SelfRepair (web reinstall)"); } catch { }
                        VPNRouter.App.Services.SelfRepair.Run();
                        Environment.Exit(0);
                    }
                    try { Console.Error.WriteLine($"[launch-counter] self-repair declined: {plan.Reason}"); } catch { }
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine($"[launch-counter] self-repair dispatch failed: {ex.Message}"); } catch { }
                }
#else
                try { Console.Error.WriteLine("[launch-counter] self-repair tier reached but only implemented on Windows"); } catch { }
#endif
                break;

            case "config-reset":
                try
                {
                    var cfg = VPNRouter.Core.AppPaths.ConfigYamlPath;
                    if (System.IO.File.Exists(cfg))
                    {
                        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        var aside = $"{cfg}.crash-recovery-{stamp}";
                        System.IO.File.Move(cfg, aside);
                        try { Console.Error.WriteLine($"[launch-counter] 5 strikes — config moved aside to {aside}, fresh defaults will be created"); } catch { }
                    }
                    // SettingsLoader.Load() will see no config.yaml on
                    // disk and write fresh defaults — the same path the
                    // app takes on a clean install.
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine($"[launch-counter] config-reset failed: {ex.Message}"); } catch { }
                }
                break;

            case "safe-mode-prompt":
                try
                {
                    var msg =
                        "VPNRouter has failed to start 7 times in a row.\r\n" +
                        "Try the manual recovery script:\r\n" +
                        "  iwr -useb https://vpn.ninitux.com/repair.cmd | iex\r\n" +
                        "Or relaunch with --safe to bypass user config.";
                    try { Console.Error.WriteLine($"[launch-counter] {msg}"); } catch { }
                    var logDir = VPNRouter.Core.AppPaths.LogsDir;
                    System.IO.Directory.CreateDirectory(logDir);
                    var logPath = System.IO.Path.Combine(logDir, "vpnrouter-launch-error.log");
                    System.IO.File.AppendAllText(logPath,
                        $"[{DateTime.Now:O}] [safe-mode-prompt] {msg}\r\n");
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine($"[launch-counter] safe-mode-prompt failed: {ex.Message}"); } catch { }
                }
                break;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
