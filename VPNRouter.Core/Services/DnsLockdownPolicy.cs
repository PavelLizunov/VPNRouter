#nullable enable

namespace VPNRouter.Core.Services;

public enum DnsLockdownAction
{
    None,
    Enable,
    Disable,
}

public static class DnsLockdownPolicy
{
    public static DnsLockdownAction Decide(bool settingEnabled, bool tunnelServing, bool currentlyEffective)
    {
        bool desired = settingEnabled && tunnelServing;
        if (desired && !currentlyEffective) return DnsLockdownAction.Enable;
        if (!desired && currentlyEffective) return DnsLockdownAction.Disable;
        return DnsLockdownAction.None;
    }
}
