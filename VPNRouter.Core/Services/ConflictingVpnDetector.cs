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
/// <para>TWO classes of peer (2026-06-26):
/// <list type="bullet">
///   <item><b>Hard conflict</b> (<see cref="KnownVpnProcessNames"/>) — sing-box
///   / xray forks that create THEIR OWN wintun and collide with VPNRouter's
///   adapter creation. These HARD-BLOCK startup (throw
///   <see cref="ConflictingVpnException"/>).</item>
///   <item><b>Coexisting</b> (<see cref="CoexistingVpnProcessNames"/>) —
///   WireGuard / AmneziaVPN run their own SEPARATE tunnel adapter and coexist
///   with VPNRouter-TUN; route overlap is already handled by excluding their
///   subnet from TUN routing (<see cref="NetworkInterfaceDetector"/>). These
///   are a SOFT WARNING, not a blocker — confirmed in the field that VPNRouter
///   connects fine with AmneziaVPN running (diag 20260626-212741).</item>
/// </list></para>
///
/// <para>Windows-only. On macOS/Linux returns an empty list — TUN
/// contention on those platforms is handled by sing-box's own utun /
/// /dev/net/tun probe and surfaces a different (clearer) error.</para>
/// </summary>
public static class ConflictingVpnDetector
{
    /// <summary>
    /// Allow-list of process names that create their OWN wintun adapter and
    /// genuinely collide with VPNRouter's sing-box ("Cannot create a file when
    /// that file already exists"). A non-empty match HARD-BLOCKS startup.
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
        // OpenVPN GUI (openvpn.exe) and OpenVPN Connect (openvpnconnect.exe)
        "openvpn",
        "openvpnconnect",
        // Hiddify-Next standalone (different process name than xraycore variant)
        "hiddify",
        // Qv2ray (legacy, still in use)
        "qv2ray",
        // NekoRay / NekoBox — both produce nekoray.exe / nekobox.exe
        "nekoray",
        "nekobox",
    };

    /// <summary>
    /// VPN clients that run their OWN separate tunnel adapter and COEXIST with
    /// VPNRouter rather than competing for VPNRouter-TUN. A non-empty match is a
    /// SOFT WARNING (logged + surfaced), NOT a startup blocker — VPNRouter's
    /// <see cref="NetworkInterfaceDetector"/> already excludes their subnet from
    /// TUN routing (route_exclude_address), so the two run side-by-side.
    ///
    /// <para>Why these moved out of the hard list (2026-06-26): the user runs
    /// VPNRouter on top of AmneziaVPN; the old hard-block threw
    /// <see cref="ConflictingVpnException"/> on the first connect even though the
    /// auto-retry then connected cleanly (diag 20260626-212741). WireGuard for
    /// Windows likewise uses its own TunnelService adapter, never VPNRouter-TUN;
    /// the only coexistence concern is route overlap, handled by the WG/AWG
    /// subnet exclusion. (Raw AmneziaWG wg-quick tunnels have no user process to
    /// detect — kernel-mode — so they were never on either list.)</para>
    ///
    /// <para>Note: AmneziaVPN can run an OpenVPN backend, in which case it
    /// spawns <c>openvpn.exe</c> — still on the HARD list above — so that mode
    /// keeps hard-blocking (OpenVPN's tap/tun contends differently). Only the
    /// AmneziaWG / native-adapter backend coexists via the soft path here.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> CoexistingVpnProcessNames = new[]
    {
        // Official WireGuard for Windows (TunnelService-managed adapter)
        "wireguard",
        // AmneziaVPN desktop GUI client (own adapter; coexists via route-exclude)
        "amneziavpn",
    };

    /// <summary>
    /// Snapshot description of one detected process.
    /// </summary>
    public sealed record ConflictingProcessInfo(string ProcessName, int Pid, string FullPath);

    /// <summary>
    /// Enumerate currently-running HARD-CONFLICT processes from
    /// <see cref="KnownVpnProcessNames"/>. Callers should treat any non-empty
    /// result as a blocker for sing-box startup.
    /// </summary>
    public static List<ConflictingProcessInfo> DetectConflictingVpnProcesses(ILogger? logger = null)
        => DetectByNames(KnownVpnProcessNames, "ConflictingVpnDetector", logger);

    /// <summary>
    /// Enumerate currently-running COEXISTING VPN clients from
    /// <see cref="CoexistingVpnProcessNames"/>. Unlike
    /// <see cref="DetectConflictingVpnProcesses"/>, a non-empty result is a soft
    /// warning (they run side-by-side via route-exclude), not a startup blocker.
    /// </summary>
    public static List<ConflictingProcessInfo> DetectCoexistingVpnProcesses(ILogger? logger = null)
        => DetectByNames(CoexistingVpnProcessNames, "CoexistingVpnDetector", logger);

    /// <summary>
    /// Shared enumeration over a process-name allow-list.
    ///
    /// <para>Implementation note: <c>Process.GetProcessesByName</c>
    /// returns a <c>Process[]</c> whose kernel handles must be disposed
    /// or they accumulate over time (see AU-9 in v2.31.1). We dispose
    /// every Process snapshot in a finally block — this method is safe
    /// to call repeatedly (e.g. from a "Refresh" button on the conflict
    /// banner).</para>
    /// </summary>
    private static List<ConflictingProcessInfo> DetectByNames(
        IReadOnlyList<string> names, string logTag, ILogger? logger)
    {
        var matches = new List<ConflictingProcessInfo>();

        if (!OperatingSystem.IsWindows())
            return matches;

        foreach (var name in names)
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
                        "[{LogTag}] Found: {ProcessName} (PID {Pid}, path {FullPath})",
                        logTag, name, p.Id, string.IsNullOrEmpty(fullPath) ? "<protected>" : fullPath);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex,
                    "[{LogTag}] Probe failed for {Name} — skipping", logTag, name);
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
