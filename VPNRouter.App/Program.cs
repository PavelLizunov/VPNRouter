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
