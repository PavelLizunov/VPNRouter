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

        correctedIntent = intendedConnected;
        return false;
    }
}
