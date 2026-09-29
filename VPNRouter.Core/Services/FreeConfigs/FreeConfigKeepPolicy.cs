using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services.FreeConfigs;

public static class FreeConfigKeepPolicy
{
    public static bool ShouldKeepInLiveCache(FreeConfigEntry entry)
    {
        if (entry == null) return false;
        return entry.Status == FreeConfigStatus.Verified;
    }

    public const int SavedListRetentionDays = 30;

    public static bool ShouldRetainInSavedList(FreeConfigEntry entry, DateTime nowUtc)
    {
        if (entry == null) return false;
        if (entry.Status != FreeConfigStatus.Verified) return false;
        if (!entry.LastTestedAt.HasValue) return true;
        return (nowUtc - entry.LastTestedAt.Value).TotalDays <= SavedListRetentionDays;
    }
}
