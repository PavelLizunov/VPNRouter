namespace VPNRouter.Core.Services;

/// <summary>
/// Android resume re-sync decision core — decides whether an Activity-resume
/// must correct the connection status card, given the optimistic intent the UI
/// is currently showing vs the service-persisted authoritative live tunnel
/// state.
///
/// <para><strong>Why this exists.</strong> On Android the status card is driven
/// by exactly two sources: (a) the optimistic <c>SetIntent</c> on Connect/
/// Disconnect button taps, and (b) <c>TUNNEL_UP</c>/<c>TUNNEL_DOWN</c>
/// broadcasts delivered to a <c>TunnelStateReceiver</c> that only lives while
/// the Activity is alive (registered in <c>OnCreate</c>, unregistered in
/// <c>OnDestroy</c>). Those broadcasts are process-local and <em>not</em>
/// sticky, so one that fires while the Activity is destroyed in the background
/// ("Don't keep activities", an aggressive OEM task-killer, or the
/// unregister/register gap during a config-change recreation) is lost. There is
/// no other re-sync — <c>OnFrameworkInitializationCompleted</c> runs once per
/// process — so the card can stay falsely "Connected" until the next tap or a
/// process restart. This helper is the decision core of the resume re-sync that
/// closes that gap. See
/// <c>plans/android-status-card-stale-lifecycle-investigation-2026-06-13.md</c>.</para>
///
/// <para><strong>Demote-only by design.</strong> It only corrects a falsely-
/// "connected" card <em>down</em> to disconnected when the tunnel is not
/// actually up — judged from two signals: the service-persisted live-state flag
/// (catches an explicit stop / lost TUNNEL_DOWN) and the live VPN-transport
/// state from the platform (catches a silent tun death the service never
/// noticed). It never promotes Off → On from the persisted flag,
/// because a fresh process can carry a stale <c>tunnel_live = true</c> after the
/// process (and its in-process service) was killed without a clean teardown;
/// promoting from that would falsely show "Connected" on a launch where the
/// tunnel is actually down. The promote direction (e.g. an Always-on tunnel
/// brought up without the Activity) is handled by the live <c>TUNNEL_UP</c>
/// broadcast once a receiver exists, not by this flag.</para>
/// </summary>
public static class TunnelStateResync
{
    /// <summary>
    /// Decide the resume re-sync action.
    /// </summary>
    /// <param name="intendedConnected">What the card currently reflects
    /// (<c>MainActivity.IntendedConnected</c>).</param>
    /// <param name="serviceTunnelLive">The service-persisted live-state
    /// (<c>AndroidStorage.GetTunnelLive()</c>): true between a <c>TUNNEL_UP</c>
    /// and the next <c>TUNNEL_DOWN</c>. NOT sufficient alone — some OEMs (e.g.
    /// KYOCERA A101BM / Android 12) tear down the tun on a system-settings VPN
    /// disconnect WITHOUT invoking <c>VpnService.onRevoke</c>, so the service
    /// never runs <c>stopTunnel</c> and this flag stays <c>true</c> while the
    /// tunnel is actually dead. Hence the second signal below.</param>
    /// <param name="vpnTransportActive">Ground truth from the platform: is a
    /// VPN-transport network actually active right now
    /// (<c>ConnectivityManager</c> <c>TRANSPORT_VPN</c>)? Pass <c>true</c> when
    /// it cannot be determined (fail-safe: don't demote on unknown).</param>
    /// <param name="correctedIntent">When this returns true, the intent the
    /// card should be set to.</param>
    /// <returns>true when the card is stale and must be corrected; false when
    /// it is already in sync or a correction would be unsafe (see class doc).</returns>
    public static bool TryResolveOnResume(
        bool intendedConnected, bool serviceTunnelLive, bool vpnTransportActive, out bool correctedIntent)
    {
        // Demote a falsely-"connected" card when the UI thinks we're up but the
        // tunnel isn't, via either signal:
        //   * !serviceTunnelLive  — explicit stop / a lost TUNNEL_DOWN broadcast
        //     (the receiver was gone when it fired).
        //   * !vpnTransportActive — silent tun death the service never noticed
        //     (no onRevoke/stopTunnel ran, so the flag is stale-true).
        if (intendedConnected && (!serviceTunnelLive || !vpnTransportActive))
        {
            correctedIntent = false;
            return true;
        }

        // Already in sync, OR a promotion we deliberately don't make from these
        // signals (Off + live). Leave as-is. (Never promotes Off -> On — a stale
        // tunnel_live=true after an unclean kill must not show Connected on a
        // fresh process; and an Always-on bring-up is promoted by the live
        // TUNNEL_UP broadcast, not here.)
        correctedIntent = intendedConnected;
        return false;
    }
}
