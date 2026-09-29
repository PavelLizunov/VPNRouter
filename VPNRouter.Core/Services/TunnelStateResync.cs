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

        // Never promote Off to On from these two signals alone: a stale tunnel_live after an unclean kill must not
        // show Connected. Promotion has its own rule below, which also needs the pid-checked state record.
        correctedIntent = intendedConnected;
        return false;
    }

    /// <summary>
    /// The card shows Off but the service says it is connected (for example the quick-settings tile started the VPN
    /// while the app was closed). Only a state record written by this very process, plus a live tunnel flag and an
    /// active VPN transport, may turn the card On.
    /// </summary>
    public static bool TryPromoteOnResume(
        bool intendedConnected, VpnStateSnapshot resolved, bool serviceTunnelLive, bool vpnTransportActive) =>
        !intendedConnected
        && resolved.State == VpnConnectionState.Connected
        && serviceTunnelLive
        && vpnTransportActive;
}
