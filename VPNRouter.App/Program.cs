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
    public static bool StartMinimized { get; private set; }

    public static string? PendingUpdateWarning { get; set; }

    public static bool SafeMode { get; private set; }

    internal static string? PendingRouteAppPath { get; set; }

    internal static string? PendingRouteAppCategory { get; set; }

    internal static string? PendingUnrouteAppPath { get; set; }

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
        if (UnixOwnedProcessSignal.TryHandleHelper(args, out var helperExitCode))
        {
            Environment.ExitCode = helperExitCode;
            return;
        }

        VPNRouter.Core.Services.CrashReporter.Install();

        StartMinimized = args.Contains("--minimized");
        SafeMode = args.Contains("--safe");
        VPNRouter.App.Services.AppAutomationDriver.ParseArgs(args);

        VPNRouter.Core.Services.SafeMode.Enabled = SafeMode;

        if (SafeMode)
            BackupConfigBeforeSafeMode();

        if (args.Contains("--reset"))
            ResetConfigAndExit();

#if PLATFORM_WINDOWS
        if (OperatingSystem.IsWindows() && !IsAdmin())
        {
            RelaunchElevated(args);
            return;
        }
#endif

        InitializeLogging();
        RecordLaunchAttempt();

#if PLATFORM_WINDOWS
        HealServiceBinPath();
        if (!RunInstallHealthCheck())
            return;

        if (!HandleRouteAppArguments(args))
            return;

        if (!VPNRouter.App.Services.SingleInstance.TryAcquireOrSignal(Serilog.Log.Logger))
            return;

        CleanUpOrphansAndHookProcessExit();
#endif

#if PLATFORM_WINDOWS
        try
        {
            if (VPNRouter.App.Services.ShortcutSelfHeal.EnsureTrampolineTarget())
            {
                try { Console.Error.WriteLine("[shortcut] Start Menu shortcut migrated to VPNRouter.GUI.exe (trampoline)"); }
                catch { }
            }
        }
        catch { }
#endif

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && AutostartHelper.EnsureCurrentPath(exe))
            {
                try { Console.Error.WriteLine($"[autostart] entry rewritten -> {exe}"); }
                catch { }
            }
        }
        catch { }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void BackupConfigBeforeSafeMode()
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
        catch { }
    }

    private static void ResetConfigAndExit()
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

    private static void InitializeLogging()
    {
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
            try { Console.Error.WriteLine($"[serilog] init failed: {ex.Message}"); } catch { }
        }
    }

    private static void RecordLaunchAttempt()
    {
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
    }

#if PLATFORM_WINDOWS
    private static void RelaunchElevated(string[] args)
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
    }

    private static void HealServiceBinPath()
    {
        try
        {
            var healResult = VPNRouter.App.Services.WindowsServiceHelper.EnsureCurrentBinPath();
            if (!healResult.Success
                || healResult.Message.StartsWith("binPath updated", StringComparison.OrdinalIgnoreCase))
            {
                try { Console.Error.WriteLine($"[service-heal] {healResult.Message}"); }
                catch { }
            }
        }
        catch { }
    }

    private static bool RunInstallHealthCheck()
    {
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
                    return false;
                }

                if (rollback.OperationInProgress)
                {
                    Console.Error.WriteLine($"[health] rollback deferred: {rollback.Reason} — not starting concurrent SelfRepair");
                    return false;
                }

                Console.Error.WriteLine($"[health] rollback declined: {rollback.Reason} — falling back to SelfRepair");
                var plan = VPNRouter.App.Services.SelfRepair.Plan();
                if (plan.ShouldRun)
                {
                    VPNRouter.App.Services.SelfRepair.Run();
                    return false;
                }
                Console.Error.WriteLine($"[health] self-repair declined: {plan.Reason}");
            }
            else
            {
                if (!string.IsNullOrEmpty(installDir))
                {
                    VPNRouter.Core.Services.UpdateBackup.ClearFailureMarker(installDir);
                    var cleanupGeneration =
                        VPNRouter.Core.Services.UpdateBackup.GetSnapshotGeneration(installDir);
                    if (cleanupGeneration is not null)
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(30));
                                VPNRouter.Core.Services.UpdateBackup.DeleteSnapshot(
                                    installDir,
                                    cleanupGeneration);
                            }
                            catch { }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine($"[health] check failed: {ex.Message}"); } catch { }
        }


        return true;
    }

    private static bool HandleRouteAppArguments(string[] args)
    {
        var routeAppPath = TryGetArgValue(args, "--route-app");
        if (routeAppPath != null)
        {
            var routeAppCategory = TryGetArgValue(args, "--category");
            if (VPNRouter.App.Services.SingleInstance.TrySendRouteAppToRunningInstance(routeAppPath, routeAppCategory, Serilog.Log.Logger))
                return false;
            PendingRouteAppPath = routeAppPath;
            PendingRouteAppCategory = routeAppCategory;
        }

        var unrouteAppPath = TryGetArgValue(args, "--unroute-app");
        if (unrouteAppPath != null)
        {
            if (VPNRouter.App.Services.SingleInstance.TrySendUnrouteAppToRunningInstance(unrouteAppPath, Serilog.Log.Logger))
                return false;
            PendingUnrouteAppPath = unrouteAppPath;
        }


        return true;
    }

    private static void CleanUpOrphansAndHookProcessExit()
    {
        try { SingBoxFeatures.Prewarm(); } catch { }

        try { OrphanCleanup.KillOrphans(); } catch { }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var fw = new FirewallManager(Serilog.Log.Logger ?? new Serilog.LoggerConfiguration().CreateLogger());
                fw.CleanupOrphanedRules();
            }
            else if (OperatingSystem.IsMacOS())
            {
                VPNRouter.Core.Platform.macOS.MacFirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);
            }
            else if (OperatingSystem.IsLinux())
            {
                VPNRouter.Core.Platform.Linux.LinuxFirewallManager.TryCleanupOrphanedRulesSafe(Serilog.Log.Logger);
            }
        }
        catch { }

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
    }

#endif

#if PLATFORM_WINDOWS
    private static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
#endif

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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
