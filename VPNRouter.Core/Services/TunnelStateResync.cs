namespace VPNRouter.Core.Services;

public static class TunnelStateResync
{
    public static bool TryResolveOnResume(
        bool intendedConnected, bool serviceTunnelLive, bool vpnTransportActive, out bool correctedIntent)
    {
        if (intendedConnected && (!serviceTunnelLive || !vpnTransportActive))
        {
            correctedIntent = false;
            return true;
        }

        // Never promote Off to On from these signals: a stale tunnel_live after an unclean kill must not show Connected.
        correctedIntent = intendedConnected;
        return false;
    }
}
