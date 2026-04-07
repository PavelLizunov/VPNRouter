using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Flushes the system DNS cache before VPN starts.
///
/// Why: Without flushing, DNS entries resolved BEFORE the VPN was started
/// remain in the system cache. When apps try to connect, they use the cached
/// IP — which was resolved through the direct route, possibly leaking to
/// non-VPN DNS servers and revealing what you intend to access.
///
/// Platforms:
///   - Windows: ipconfig /flushdns
///   - macOS:   sudo dscacheutil -flushcache &amp;&amp; sudo killall -HUP mDNSResponder
///   - Linux:   not implemented (varies by resolver)
/// </summary>
public static class DnsFlusher
{
    public static void Flush(ILogger? logger = null)
    {
        var log = logger ?? Log.Logger;

        try
        {
            if (OperatingSystem.IsWindows())
                FlushWindows(log);
            else if (OperatingSystem.IsMacOS())
                FlushMac(log);
            else
                log.Debug("[DnsFlusher] Platform not supported — skipping DNS flush");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[DnsFlusher] DNS flush failed (non-critical)");
        }
    }

    private static void FlushWindows(ILogger log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ipconfig.exe",
            Arguments = "/flushdns",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            log.Warning("[DnsFlusher] Failed to start ipconfig.exe");
            return;
        }

        proc.WaitForExit(5000);

        if (proc.ExitCode == 0)
            log.Information("[DnsFlusher] Windows DNS cache flushed");
        else
            log.Warning("[DnsFlusher] ipconfig /flushdns returned exit code {Code}", proc.ExitCode);
    }

    private static void FlushMac(ILogger log)
    {
        // Both commands needed: dscacheutil for system cache, killall for mDNSResponder cache
        // sudo with NOPASSWD requires sudoers entries — but flushing DNS doesn't need root.
        // dscacheutil -flushcache works without sudo.
        // killall -HUP mDNSResponder DOES need sudo, but if it fails we still flushed dscacheutil.

        // dscacheutil (no sudo needed)
        try
        {
            using var p1 = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/dscacheutil",
                Arguments = "-flushcache",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p1?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsFlusher] dscacheutil failed");
        }

        // mDNSResponder restart — needs sudo, may silently fail
        try
        {
            using var p2 = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/sudo",
                Arguments = "-n killall -HUP mDNSResponder",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p2?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[DnsFlusher] mDNSResponder restart failed (sudo not configured for killall)");
        }

        log.Information("[DnsFlusher] macOS DNS cache flush attempted");
    }
}
