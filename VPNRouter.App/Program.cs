using Avalonia;
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

        // Defensive cleanup: kill orphan sing-box / older VPNRouter instances
        // left behind by failed updates or v2.3.x→v2.4.x migration.
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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
