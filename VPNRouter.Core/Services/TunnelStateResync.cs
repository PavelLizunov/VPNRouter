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
/// "connected" card <em>down</em> to disconnected when the service's persisted
/// live-state says down. It never promotes Off → On from the persisted flag,
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
    /// <param name="serviceTunnelLive">The service-persisted authoritative
    /// live-state (<c>AndroidStorage.GetTunnelLive()</c>): true between a
    /// <c>TUNNEL_UP</c> and the next <c>TUNNEL_DOWN</c>.</param>
    /// <param name="correctedIntent">When this returns true, the intent the
    /// card should be set to.</param>
    /// <returns>true when the card is stale and must be corrected; false when
    /// it is already in sync or a correction would be unsafe (see class doc).</returns>
    public static bool TryResolveOnResume(bool intendedConnected, bool serviceTunnelLive, out bool correctedIntent)
    {
        // Falsely-connected: the UI thinks we're up but the service tore the
        // tunnel down (a lost TUNNEL_DOWN). Correct down.
        if (intendedConnected && !serviceTunnelLive)
        {
            correctedIntent = false;
            return true;
        }

        // Already in sync, OR a promotion we deliberately don't trust the
        // persisted flag for (Off + stale-or-real live=true). Leave as-is.
        correctedIntent = intendedConnected;
        return false;
    }
}
