using System.Diagnostics;
using System.Net.NetworkInformation;

namespace VPNRouter.Core.Services;

/// <summary>
/// Runtime status of a VPNRouter background component, for UI display purposes.
/// Not tied to a specific VM/process instance — detects via external signals
/// (running processes, bound ports) so it works whether the component was
/// started by the desktop app, the Windows Service, or the CLI.
/// </summary>
public enum ComponentRuntimeStatus
{
    /// <summary>Not running (neither by us nor anyone else).</summary>
    Idle,

    /// <summary>Detected as running.</summary>
    Running,

    /// <summary>Recently failed (retries exhausted) — set by caller, not detected.</summary>
    Failed
}

/// <summary>
/// Detects whether VPNRouter background components (sing-box, Zapret, TgProxy)
/// are currently running via process enumeration + port probing. Stateless and
/// cheap enough to poll every 1–2 seconds.
/// </summary>
public static class RuntimeStatusDetector
{
    /// <summary>True if any sing-box.exe process is running on this machine.</summary>
    public static bool IsVpnRunning()
    {
        try
        {
            return Process.GetProcessesByName("sing-box").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True if any winws.exe process is running (Zapret DPI bypass).</summary>
    public static bool IsZapretRunning()
    {
        try
        {
            return Process.GetProcessesByName("winws").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True if something is listening on the configured TgProxy port.
    /// Port-based detection is used because TgProxy runs as python.exe which
    /// we can't easily distinguish from other Python processes.
    /// </summary>
    /// <param name="port">Configured TgProxy port from AppSettings.</param>
    public static bool IsTgProxyRunning(int port)
    {
        if (port <= 0) return false;

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners();
            foreach (var ep in listeners)
            {
                if (ep.Port == port) return true;
            }
        }
        catch
        {
            // Access denied, feature unsupported, etc. — treat as "unknown, assume idle"
        }

        return false;
    }
}
