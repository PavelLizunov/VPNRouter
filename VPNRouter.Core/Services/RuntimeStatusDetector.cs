using System.Diagnostics;

namespace VPNRouter.Core.Services;

public enum ComponentRuntimeStatus
{
    Idle,

    Running,

    Failed
}

public static class RuntimeStatusDetector
{
    public sealed record VpnRuntimeProcess(
        int Pid,
        DateTime StartedAt,
        string ExecutablePath);

    public static VpnRuntimeProcess? GetVpnRuntime()
    {
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

    public static bool PersistedCliStateMatches(int singBoxPid, DateTime stateWrittenAtUtc)
        => ProcessOwnership.PersistedCliStateMatches(singBoxPid, stateWrittenAtUtc);

    public static bool IsPersistedChildAlive(int singBoxPid)
        => ProcessOwnership.PersistedChildIsAlive(singBoxPid);

    public static bool IsZapretRunning()
        => AnyProcessAlive("winws");

    private static bool AnyProcessAlive(string processName) => ProcessQuery.AnyAlive(processName);
}
