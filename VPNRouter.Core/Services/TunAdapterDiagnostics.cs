using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Best-effort diagnostic snapshot of the Windows TUN interface state.
///
/// <para>v2.27.2: introduced as a passive data-gathering step so we can
/// see — in production logs — whether our "orphan sing-box kill" path
/// actually leaves dangling <c>VPNRouter-TUN</c> adapters behind.</para>
///
/// <para>v2.30.1-r5: hypothesis confirmed by user reports
/// ("periodically the network interface doesn't die and Windows reboot
/// is required"). Added active cleanup via
/// <see cref="DisableOrphanedAdapter"/> — disables the wintun adapter
/// in the device manager when sing-box exits without releasing it,
/// freeing the OS network stack from the dangling routes / DNS that
/// were keeping the user's network state stuck.</para>
/// </summary>
public static class TunAdapterDiagnostics
{
    /// <summary>
    /// Log current TUN adapter inventory via <c>netsh interface show interface</c>.
    /// Windows-only; returns silently on other platforms. Errors are swallowed
    /// — diagnostics must never block startup.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void LogAdapterState(ILogger? logger, string context)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var psi = new ProcessStartInfo("netsh", "interface show interface")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Parse out lines referencing our interface name. The netsh
            // output is verbose and English-locale-dependent; we only
            // want the rows that mention VPNRouter-TUN or any
            // "sing-box-tun-" adapter (sing-box's fallback when a custom
            // InterfaceName is unavailable) so log noise stays minimal.
            var hits = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(l =>
                    l.IndexOf("VPNRouter-TUN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    l.IndexOf("sing-box-tun", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (hits.Count == 0)
            {
                logger?.Information("[TunDiag] {Ctx}: no VPNRouter-TUN or sing-box-tun adapters found", context);
                return;
            }

            logger?.Information(
                "[TunDiag] {Ctx}: found {Count} TUN adapter row(s) in netsh:",
                context, hits.Count);
            foreach (var line in hits)
            {
                logger?.Information("[TunDiag]   {Line}", line.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Debug(ex, "[TunDiag] {Ctx}: inventory query failed (non-fatal)", context);
        }
    }

    /// <summary>
    /// v2.30.1-r5: aggressive cleanup for orphaned wintun adapters.
    /// Called when sing-box exits unexpectedly (crash, silent kill on
    /// Windows wake, etc.) to disable the dangling network interface
    /// so the OS releases the cached routes / DNS / TUN handle.
    ///
    /// <para>Without this, users hit "the network interface doesn't
    /// die and I have to reboot Windows" after sing-box silent-kill —
    /// the wintun adapter stays in the netsh inventory in a half-alive
    /// state, holding TUN-routed default routes that the network stack
    /// can't easily flush. Disabling the adapter via netsh forces
    /// Windows to drop those routes immediately.</para>
    ///
    /// <para>Non-fatal: any error is swallowed and logged at Warning
    /// level. Cleanup is idempotent — disabling an already-disabled or
    /// already-deleted adapter is a no-op (with a "not found" stderr
    /// from netsh that we ignore).</para>
    ///
    /// <para>Intentionally uses <c>netsh interface set interface ...
    /// admin=disabled</c> instead of <c>Remove-NetAdapter</c> because:
    /// (a) PowerShell isn't always on PATH inside our service-managed
    /// process tree, (b) wintun adapters refuse Remove-NetAdapter when
    /// the underlying handle is still open by sing-box's GC-pending
    /// cleanup, but disable always succeeds. After disable, sing-box's
    /// next start will re-enable the adapter automatically.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void DisableOrphanedAdapter(ILogger? logger, string interfaceName, string context)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(interfaceName)) return;

        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"interface set interface name=\"{interfaceName}\" admin=disabled")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                logger?.Warning("[TunDiag] {Ctx}: failed to spawn netsh for adapter disable", context);
                return;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(3000);

            // netsh exit codes: 0 = success, 1 = "element not found"
            // (adapter already gone — fine), other = real failure.
            if (proc.ExitCode == 0)
            {
                logger?.Information(
                    "[TunDiag] {Ctx}: disabled orphaned adapter '{Iface}' (network stack should release routes)",
                    context, interfaceName);
            }
            else if (proc.ExitCode == 1
                     || stdout.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                     || stderr.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                logger?.Debug(
                    "[TunDiag] {Ctx}: adapter '{Iface}' already gone — nothing to clean up",
                    context, interfaceName);
            }
            else
            {
                logger?.Warning(
                    "[TunDiag] {Ctx}: netsh disable for '{Iface}' returned exit {Code}: stdout='{Out}' stderr='{Err}'",
                    context, interfaceName, proc.ExitCode, stdout.Trim(), stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "[TunDiag] {Ctx}: disable orphaned adapter '{Iface}' failed (non-fatal)", context, interfaceName);
        }
    }
}
