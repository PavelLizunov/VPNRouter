using Microsoft.Win32;

namespace VPNRouter.Core.Platform;

/// <summary>
/// Manages GUI autostart on Windows logon via HKCU\Run registry key.
/// Writes path to VPNRouter.App.exe with --minimized flag.
/// </summary>
public static class AutostartHelper
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VPNRouter";

    /// <summary>Enable autostart: adds VPNRouter.App.exe --minimized to HKCU\Run.</summary>
    public static void Enable(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.SetValue(AppName, $"\"{exePath}\" --minimized");
    }

    /// <summary>Disable autostart: removes VPNRouter from HKCU\Run.</summary>
    public static void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    /// <summary>Check if autostart is enabled in registry.</summary>
    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) != null;
    }
}
