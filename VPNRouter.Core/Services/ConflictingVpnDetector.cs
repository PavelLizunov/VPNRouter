using System.Diagnostics;
using Serilog;

namespace VPNRouter.Core.Services;

/// <summary>
/// Bug-r9-E (2026-05-11) — pre-flight detection of third-party VPN clients
/// that hold the TUN/TAP adapter exclusively on Windows. When such a tool
/// is running, sing-box's wintun creation fails with the cryptic
/// "Cannot create a file when that file already exists" — and a user with
/// e.g. v2RayTun open in another window has no way of mapping that error
/// back to "stop the other VPN first".
///
/// <para>Real-world repro: stas's log batch (2026-05-10) had xraycore.exe
/// from v2RayTun running; sing-box silently failed adapter creation, the
/// status banner said "Failed to start VPN: Cannot create a file..." and
/// stas opened a bug report assuming VPNRouter was broken.</para>
///
/// <para>The detector inspects a small allow-list of process names from
/// known Windows VPN clients that share wintun. It does NOT touch
/// AmneziaWG (10.9.1.1 dev tunnel) — that's a kernel WireGuard adapter
/// owned by the user's own infrastructure, not a competing TUN holder.
/// WireGuard for Windows IS included because its service mode does
/// register a TUN adapter that conflicts with sing-box.</para>
///
/// <para>Windows-only. On macOS/Linux returns an empty list — TUN
/// contention on those platforms is handled by sing-box's own utun /
/// /dev/net/tun probe and surfaces a different (clearer) error.</para>
/// </summary>
public static class ConflictingVpnDetector
{
    /// <summary>
    /// Allow-list of process names known to hold a TUN/TAP adapter on
    /// Windows that would conflict with VPNRouter's sing-box wintun.
    /// Match is case-insensitive (Windows convention) — Process.GetProcessesByName
    /// already normalises.
    ///
    /// <para>Curated from the wild — every entry corresponds to a tool
    /// reported in the field as causing the adapter-locked error.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> KnownVpnProcessNames = new[]
    {
        // v2RayTun, Hiddify-Next, v2rayN — all bundle xray-core as xraycore.exe
        "xraycore",
        // Official WireGuard for Windows (TunnelService-managed adapter)
        "wireguard",
        // OpenVPN GUI (openvpn.exe) and OpenVPN Connect (openvpnconnect.exe)
        "openvpn",
        "openvpnconnect",
        // Hiddify-Next standalone (different process name than xraycore variant)
        "hiddify",
        // AmneziaVPN desktop client (NOT to be confused with raw AmneziaWG
        // wg-quick interfaces — that's a kernel-mode tunnel, no user process
        // to detect. AmneziaVPN.exe is the GUI client that bundles its own
        // sing-box / xray fork and grabs wintun on connect.)
        "amneziavpn",
        // Qv2ray (legacy, still in use)
        "qv2ray",
        // NekoRay / NekoBox — both produce nekoray.exe / nekobox.exe
        "nekoray",
        "nekobox",
    };

    /// <summary>
    /// Snapshot description of one detected conflicting process.
    /// </summary>
    public sealed record ConflictingProcessInfo(string ProcessName, int Pid, string FullPath);

    /// <summary>
    /// Enumerate currently-running processes from <see cref="KnownVpnProcessNames"/>.
    /// Each match is reported as a <see cref="ConflictingProcessInfo"/>.
    ///
    /// <para>The returned list may be empty (no conflict, normal case) or
    /// contain multiple entries (user has e.g. WireGuard service AND a
    /// v2RayTun open). Callers should treat any non-empty result as a
    /// blocker for sing-box startup.</para>
    ///
    /// <para>Implementation note: <c>Process.GetProcessesByName</c>
    /// returns a <c>Process[]</c> whose kernel handles must be disposed
    /// or they accumulate over time (see AU-9 in v2.31.1). We dispose
    /// every Process snapshot in a finally block — this method is safe
    /// to call repeatedly (e.g. from a "Refresh" button on the conflict
    /// banner).</para>
    /// </summary>
    public static List<ConflictingProcessInfo> DetectConflictingVpnProcesses(ILogger? logger = null)
    {
        var matches = new List<ConflictingProcessInfo>();

        if (!OperatingSystem.IsWindows())
            return matches;

        foreach (var name in KnownVpnProcessNames)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    string fullPath = "";
                    try { fullPath = p.MainModule?.FileName ?? ""; }
                    catch { /* protected processes / Access Denied — keep "" */ }

                    matches.Add(new ConflictingProcessInfo(
                        ProcessName: name,
                        Pid: p.Id,
                        FullPath: fullPath));

                    logger?.Information(
                        "[ConflictingVpnDetector] Found: {ProcessName} (PID {Pid}, path {FullPath})",
                        name, p.Id, string.IsNullOrEmpty(fullPath) ? "<protected>" : fullPath);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex,
                    "[ConflictingVpnDetector] Probe failed for {Name} — skipping", name);
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) p.Dispose();
            }
        }

        return matches;
    }
}

/// <summary>
/// Thrown by <see cref="VpnEngine.StartAsync"/> when one or more
/// processes from <see cref="ConflictingVpnDetector.KnownVpnProcessNames"/>
/// are running. Callers (App / CLI / Service) should catch this
/// specifically and surface a friendly "stop X first" message instead of
/// falling through to the generic "Failed to start VPN: ..." path.
/// </summary>
public class ConflictingVpnException : Exception
{
    public IReadOnlyList<ConflictingVpnDetector.ConflictingProcessInfo> Conflicts { get; }

    public ConflictingVpnException(
        IReadOnlyList<ConflictingVpnDetector.ConflictingProcessInfo> conflicts,
        string message)
        : base(message)
    {
        Conflicts = conflicts;
    }
}
