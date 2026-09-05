using System.Diagnostics;

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
    /// <summary>A live, ownership-filtered tunnel child.</summary>
    public sealed record VpnRuntimeProcess(
        int Pid,
        DateTime StartedAt,
        string ExecutablePath);

    /// <summary>
    /// Read config.yaml afresh and detect the real tunnel child. The YAML path
    /// is a discovery candidate only; ProcessOwnership independently decides
    /// whether the image is trusted. A positively free semaphore rejects even a
    /// trusted-bin process (deep verifiers do not own TUN), while an unavailable
    /// semaphore preserves the historical process-only fail-open behaviour.
    /// </summary>
    public static VpnRuntimeProcess? GetVpnRuntime()
    {
        // Fast path: if an authoritative v2 runtime-owner record is live, resolve it directly
        // without reading or parsing config.yaml from disk.
        var child = ProcessOwnership.FindOwnedSingBox(null);
        if (child is null && System.IO.File.Exists(AppPaths.ConfigYamlPath))
        {
            var configuredCandidate = ProcessOwnership.ReadConfiguredExecutablePath(
                AppPaths.ConfigYamlPath);
            child = ProcessOwnership.FindOwnedSingBox(configuredCandidate);
        }

        var ownership = TunOwnershipLock.ProbeOwnership();
        if (!IsTunnelPresent(child is not null, ownership) || child is not { } live)
            return null;

        return new VpnRuntimeProcess(
            live.Pid,
            new DateTime(live.StartedAtUtcTicks, DateTimeKind.Utc),
            live.ExecutablePath);
    }

    public static bool IsVpnRunning() => GetVpnRuntime() is not null;

    internal static bool IsTunnelPresent(bool liveTunnelChild, TunOwnershipStatus ownership)
        => liveTunnelChild && ownership != TunOwnershipStatus.Free;

    /// <summary>
    /// Validate CLI state.json against the durable child identity. The state
    /// file write may be arbitrarily delayed; it only has to be at or after the
    /// recorded child start. This accepts a precisely recorded crashed child,
    /// but rejects a live process that merely reused its PID.
    /// </summary>
    public static bool PersistedCliStateMatches(int singBoxPid, DateTime stateWrittenAtUtc)
        => ProcessOwnership.PersistedCliStateMatches(singBoxPid, stateWrittenAtUtc);

    public static bool IsPersistedChildAlive(int singBoxPid)
        => ProcessOwnership.PersistedChildIsAlive(singBoxPid);

    /// <summary>True if any winws.exe process is running (Zapret DPI bypass).</summary>
    public static bool IsZapretRunning()
        => AnyProcessAlive("winws");

    /// <summary>
    /// v2.31.1-r1 (AU-9 fix): <c>Process.GetProcessesByName</c> returns
    /// <c>Process[]</c> where each entry holds a kernel handle. The detector
    /// is polled every 1–2 seconds (see class summary), so without explicit
    /// disposal we leaked one OS handle per <c>Process</c> per poll until GC
    /// finalised the orphaned objects — matching the audit's "+170 handles
    /// per VPN start/stop cycle" symptom.
    ///
    /// <para>v2.40.0-r3: the disposal logic moved to the shared
    /// <see cref="ProcessQuery.AnyAlive(string)"/> so every name-based detector
    /// (Zapret, VM status, Public Configs) shares one handle-safe path; this
    /// stays as a thin alias for the existing call sites + the
    /// RuntimeStatusDetectorHandleLeakTests pin.</para>
    /// </summary>
    private static bool AnyProcessAlive(string processName) => ProcessQuery.AnyAlive(processName);

    /// <summary>
    /// Port occupancy is not process ownership: foreign listeners are unknown, not owned.
    /// Retained for signature compatibility; returns false to prevent port-only truth claims.
    /// </summary>
    /// <param name="port">Configured TgProxy port from AppSettings.</param>
    public static bool IsTgProxyRunning(int port)
    {
        return false;
    }
}
