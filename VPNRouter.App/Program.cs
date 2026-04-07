using Avalonia;
using System;
#if PLATFORM_WINDOWS
using System.Diagnostics;
using System.Security.Principal;
#endif

namespace VPNRouter.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if PLATFORM_WINDOWS
        // Auto-elevate to admin (required for TUN + ETW + Firewall)
        if (OperatingSystem.IsWindows() && !IsAdmin())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
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
