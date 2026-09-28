namespace VPNRouter.Core.Services;

internal static class WedgeKillPolicy
{
    public static bool ShouldKill(bool serving, ref bool servingConfirmed, ref int streak, int threshold)
    {
        if (serving) { servingConfirmed = true; streak = 0; return false; }
        if (!servingConfirmed) return false;
        return ++streak >= threshold;
    }
}
