using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace VPNRouter.Core.Platform;

/// <summary>
/// Cross-platform autostart at user login. Manages a single OS-native
/// "start this binary at login" entry per platform.
///
/// <para>v2.29.0 — extended to Mac (LaunchAgent) and Linux (XDG autostart
/// .desktop file). Pre-v2.29 only Windows was supported (HKCU\Run); the
/// Settings → Autostart panel showed an "available on Windows only"
/// notice on Mac/Linux. Mac tester reported (2026-04-29) that the notice
/// was a roadblock and asked for the feature.</para>
///
/// <para>All platforms use the SAME public API surface
/// (<see cref="Enable(string)"/>, <see cref="Disable"/>, <see cref="IsEnabled"/>,
/// <see cref="EnsureCurrentPath(string)"/>); platform branching happens
/// inside. Caller doesn't need <c>#if PLATFORM_*</c> guards.</para>
///
/// <para>Always user-session, post-login. None of the three platform
/// implementations register at boot / pre-login — that's a separate
/// concept (Windows Service / launchd Daemon / systemd unit) handled
/// elsewhere when applicable.</para>
/// </summary>
public static class AutostartHelper
{
    // ── Windows: HKCU\Run ──
    private const string WinRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string WinAppName = "VPNRouter";

    // ── macOS: ~/Library/LaunchAgents/com.ninitux.vpnrouter.plist ──
    private const string MacPlistLabel = "com.ninitux.vpnrouter";

    // ── Linux: ~/.config/autostart/vpnrouter.desktop ──
    private const string LinuxDesktopFileName = "vpnrouter.desktop";

    /// <summary>Enable autostart for the given binary path.
    /// On Windows: writes <c>HKCU\Run\VPNRouter</c> with <c>--minimized</c> arg.
    /// On macOS: writes a LaunchAgent .plist + <c>launchctl load</c>.
    /// On Linux: writes a XDG <c>vpnrouter.desktop</c> autostart entry.</summary>
    public static void Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return;

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WinRunKey, writable: true);
            key?.SetValue(WinAppName, BuildWinRunValue(exePath));
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var plistPath = MacPlistPath();
            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            File.WriteAllText(plistPath, BuildMacPlist(exePath));
            // Try to load immediately so the agent is "live" without re-login.
            // Failure is non-fatal — the next login will pick the .plist up
            // anyway via launchd's directory scan.
            TryLaunchctl("load", "-w", plistPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var desktopPath = LinuxDesktopPath();
            Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
            File.WriteAllText(desktopPath, BuildLinuxDesktop(exePath));
            // Make sure XDG sessions can read it (mode 0644).
            try { File.SetUnixFileMode(desktopPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead); }
            catch { /* best-effort; not all FSes support chmod */ }
        }
    }

    /// <summary>Disable autostart on the current platform. No-op if it
    /// wasn't enabled. Safe to call repeatedly.</summary>
    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WinRunKey, writable: true);
            key?.DeleteValue(WinAppName, throwOnMissingValue: false);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var plistPath = MacPlistPath();
            // Unload first so launchd drops the in-memory copy of the agent;
            // then delete the .plist on disk so the next login won't re-load
            // from the directory scan.
            TryLaunchctl("unload", "-w", plistPath);
            try { File.Delete(plistPath); } catch { /* best-effort */ }
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try { File.Delete(LinuxDesktopPath()); } catch { /* best-effort */ }
        }
    }

    /// <summary>True if autostart is currently configured for this user.
    /// Only checks for the existence of the entry; does NOT verify that
    /// the path inside still points to a valid binary (use
    /// <see cref="EnsureCurrentPath(string)"/> for self-heal).</summary>
    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WinRunKey);
            return key?.GetValue(WinAppName) != null;
        }

        if (OperatingSystem.IsMacOS())
            return File.Exists(MacPlistPath());

        if (OperatingSystem.IsLinux())
            return File.Exists(LinuxDesktopPath());

        return false;
    }

    /// <summary>
    /// v2.25.13 — self-heal for moved/reinstalled binaries. User symptom
    /// that triggered this method on Windows: "в автозапуске висит старая
    /// версия приложения которую я уже удалил а новая не перезаписывает её".
    ///
    /// <para>Background: <see cref="Enable(string)"/> bakes an ABSOLUTE
    /// path into the autostart entry. If the binary later moves
    /// (reinstall, new path), the entry still points to the ghost binary
    /// — silent fail at next login.</para>
    ///
    /// <para>This method runs on every normal startup. If autostart is
    /// currently enabled but the entry's path doesn't match the
    /// CURRENTLY RUNNING binary, rewrite the entry. Non-destructive: if
    /// autostart is disabled, does nothing. v2.29.0 extends this to
    /// Mac/Linux too.</para>
    /// </summary>
    /// <returns>True if the entry was rewritten, false otherwise (no
    /// autostart, already correct, or transient platform error).</returns>
    public static bool EnsureCurrentPath(string currentExePath)
    {
        if (string.IsNullOrWhiteSpace(currentExePath)) return false;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WinRunKey, writable: true);
                if (key == null) return false;
                if (key.GetValue(WinAppName) is not string existingValue) return false;
                var expected = BuildWinRunValue(currentExePath);
                if (string.Equals(existingValue, expected, StringComparison.OrdinalIgnoreCase))
                    return false;
                key.SetValue(WinAppName, expected);
                return true;
            }
            catch { return false; }
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var plistPath = MacPlistPath();
                if (!File.Exists(plistPath)) return false;
                var existing = File.ReadAllText(plistPath);
                if (existing.Contains(currentExePath, StringComparison.Ordinal)) return false;
                // Rewrite + reload so launchd picks the new path right away.
                File.WriteAllText(plistPath, BuildMacPlist(currentExePath));
                TryLaunchctl("unload", "-w", plistPath);
                TryLaunchctl("load",   "-w", plistPath);
                return true;
            }
            catch { return false; }
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var desktopPath = LinuxDesktopPath();
                if (!File.Exists(desktopPath)) return false;
                var existing = File.ReadAllText(desktopPath);
                var expectedExec = BuildLinuxExecLine(currentExePath);
                if (existing.Contains(expectedExec, StringComparison.Ordinal)) return false;
                File.WriteAllText(desktopPath, BuildLinuxDesktop(currentExePath));
                return true;
            }
            catch { return false; }
        }

        return false;
    }

    // ── Windows helpers ──

    private static string BuildWinRunValue(string exePath) => $"\"{exePath}\" --minimized";

    // ── macOS helpers ──

    private static string MacPlistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{MacPlistLabel}.plist");
    }

    /// <summary>Build a minimal LaunchAgent .plist that runs the binary
    /// with <c>--autostart</c> at login. <c>RunAtLoad=true</c> fires
    /// immediately on load (next login or on-demand <c>launchctl load</c>);
    /// <c>KeepAlive=false</c> means launchd will NOT respawn the app
    /// when the user quits — we want explicit-quit to stick.</summary>
    private static string BuildMacPlist(string exePath)
    {
        // Minimal escape: plist XML treats &, <, > as special. The
        // ProgramArguments path is the only field with user-controlled
        // content — everything else is constants.
        var escaped = exePath
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>             <string>{MacPlistLabel}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{escaped}</string>
        <string>--minimized</string>
    </array>
    <key>RunAtLoad</key>         <true/>
    <key>KeepAlive</key>         <false/>
    <key>ProcessType</key>       <string>Interactive</string>
</dict>
</plist>
";
    }

    private static void TryLaunchctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/launchctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return;
            // Don't wait forever — `launchctl load` is normally instant.
            // 5 s is plenty; if it hangs the agent file is still on disk
            // and next login will pick it up.
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
            }
        }
        catch { /* ignore: enable/disable is best-effort, file write is the truth */ }
    }

    // ── Linux helpers ──

    private static string LinuxDesktopPath()
    {
        // Honor XDG_CONFIG_HOME if set, fall back to ~/.config.
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfig))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgConfig = Path.Combine(home, ".config");
        }
        return Path.Combine(xdgConfig, "autostart", LinuxDesktopFileName);
    }

    private static string BuildLinuxExecLine(string exePath) => $"Exec={exePath} --minimized";

    /// <summary>Minimal XDG autostart .desktop file. Compatible with
    /// GNOME, KDE Plasma, XFCE, Cinnamon, MATE, LXQt, etc. — the spec
    /// is honoured by every freedesktop-compatible session.</summary>
    private static string BuildLinuxDesktop(string exePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]");
        sb.AppendLine("Type=Application");
        sb.AppendLine("Name=VPNRouter");
        sb.AppendLine("Comment=Process-based split-tunnel VPN router");
        sb.AppendLine(BuildLinuxExecLine(exePath));
        sb.AppendLine("Icon=vpnrouter");
        sb.AppendLine("Terminal=false");
        sb.AppendLine("Categories=Network;Utility;");
        sb.AppendLine("X-GNOME-Autostart-enabled=true");
        sb.AppendLine("NoDisplay=false");
        sb.AppendLine("Hidden=false");
        return sb.ToString();
    }
}
