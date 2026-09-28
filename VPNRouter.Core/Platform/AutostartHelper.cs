using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace VPNRouter.Core.Platform;

public static class AutostartHelper
{
    private const string WinRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string WinAppName = "VPNRouter";

    private const string MacPlistLabel = "com.ninitux.vpnrouter";

    private const string LinuxDesktopFileName = "vpnrouter.desktop";

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
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var desktopPath = LinuxDesktopPath();
            Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
            File.WriteAllText(desktopPath, BuildLinuxDesktop(exePath));
            try { File.SetUnixFileMode(desktopPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead); }
            catch {  }
        }
    }

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
            TryLaunchctl("unload", "-w", plistPath);
            try { File.Delete(plistPath); } catch {  }
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try { File.Delete(LinuxDesktopPath()); } catch {  }
        }
    }

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
                File.WriteAllText(plistPath, BuildMacPlist(currentExePath));
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

    private static string BuildWinRunValue(string exePath) => $"\"{exePath}\" --minimized";

    private static string MacPlistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{MacPlistLabel}.plist");
    }

    private static string BuildMacPlist(string exePath)
    {
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
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
            }
        }
        catch {  }
    }

    private static string LinuxDesktopPath()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfig))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdgConfig = Path.Combine(home, ".config");
        }
        return Path.Combine(xdgConfig, "autostart", LinuxDesktopFileName);
    }

    private static string BuildLinuxExecLine(string exePath) => $"Exec={exePath} --minimized";

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
