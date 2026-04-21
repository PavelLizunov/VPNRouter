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
        key?.SetValue(AppName, BuildRunValue(exePath));
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

    /// <summary>
    /// v2.25.13 — self-heal for moved/reinstalled binaries. User symptom
    /// that triggered this method: "в автозапуске висит старая версия
    /// приложения которую я уже удалил а новая не перезаписывает её".
    ///
    /// Background: <see cref="Enable(string)"/> bakes an ABSOLUTE path into
    /// the Run key at the moment the user toggles autostart. If the binary
    /// later moves (reinstall, copy to new folder, ZIP re-extract to a
    /// different location), the Run key still points to the old ghost
    /// binary — silent fail at next login, no autostart.
    ///
    /// This method runs on every normal startup of VPNRouter.App.exe. If
    /// autostart is currently enabled but the Run-key value doesn't match
    /// the CURRENTLY RUNNING exe, rewrite the value so the next login
    /// starts the real binary. Non-destructive: if autostart is disabled,
    /// does nothing.
    ///
    /// Returns true if the value was updated, false otherwise (including
    /// the no-op case of already-correct path).
    /// </summary>
    public static bool EnsureCurrentPath(string currentExePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(currentExePath)) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return false;

            if (key.GetValue(AppName) is not string existingValue)
                return false;  // autostart not enabled → nothing to heal

            var expected = BuildRunValue(currentExePath);
            if (string.Equals(existingValue, expected, StringComparison.OrdinalIgnoreCase))
                return false;  // already correct

            key.SetValue(AppName, expected);
            return true;
        }
        catch
        {
            // Registry inaccessible — don't crash the startup over a cosmetic fix
            return false;
        }
    }

    private static string BuildRunValue(string exePath) => $"\"{exePath}\" --minimized";
}
