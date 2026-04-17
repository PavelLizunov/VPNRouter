using Avalonia;
using System;
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

    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized");

#if PLATFORM_WINDOWS
        // Auto-elevate to admin (required for TUN + ETW + Firewall)
        if (OperatingSystem.IsWindows() && !IsAdmin())
        {
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
            catch
            {
                // User cancelled UAC, or failed to elevate
            }
            return;
        }

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
