using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;

namespace VPNRouter.Android;

/// <summary>
/// P4 (2026-06-21) — external broadcast control for automation (Tasker, home-screen
/// widgets, adb). An exported receiver that forwards START / STOP / TOGGLE intents to
/// <c>VpnRouterService</c>.
///
/// <para><strong>Security model</strong>: a third-party automation app (Tasker) is
/// signed with its OWN key, so a <c>signature</c>-level permission would BLOCK it —
/// defeating the purpose. Instead this is gated by an explicit in-app opt-in
/// (<see cref="AndroidStorage.GetExternalControlEnabled"/>, default <c>OFF</c>): the
/// receiver no-ops unless the user has turned "Allow external control" on in Settings,
/// and every command is logged. So out of the box NO external app can touch the VPN;
/// the user makes a deliberate, reversible choice to enable it, exactly like granting a
/// dangerous permission. Default-OFF + user-opt-in + audit-log is the standard secure
/// pattern for VPN automation hooks.</para>
///
/// <para><strong>START is best-effort</strong>: a broadcast can't show the VpnService
/// consent dialog, so START only succeeds when consent was already granted (a prior
/// manual connect), and a background-initiated foreground-service start is restricted on
/// Android 12+ unless the app is battery-opt exempt. STOP / TOGGLE-to-stop always work.
/// Failures are logged, never thrown.</para>
/// </summary>
[BroadcastReceiver(Name = "com.ninitux.vpnrouter.VpnControlReceiver", Exported = true, Enabled = true)]
[IntentFilter(new[]
{
    "com.ninitux.vpnrouter.EXT_START",
    "com.ninitux.vpnrouter.EXT_STOP",
    "com.ninitux.vpnrouter.EXT_TOGGLE",
})]
public class VpnControlReceiver : BroadcastReceiver
{
    public const string ActExtStart = "com.ninitux.vpnrouter.EXT_START";
    public const string ActExtStop = "com.ninitux.vpnrouter.EXT_STOP";
    public const string ActExtToggle = "com.ninitux.vpnrouter.EXT_TOGGLE";

    // Mirrors VpnRouterService.java's intent contract (same strings MainActivity uses).
    private const string SvcActionStart = "com.ninitux.vpnrouter.START";
    private const string SvcActionStop = "com.ninitux.vpnrouter.STOP";
    private const string SvcClass = "com.ninitux.vpnrouter.VpnRouterService";

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action;
        if (context is null || string.IsNullOrEmpty(action)) return;

        // Secure-by-default gate: ignore everything unless the user opted in.
        if (!AndroidStorage.GetExternalControlEnabled())
        {
            Log.Warn("VpnRouter",
                $"P4: external-control broadcast '{action}' IGNORED — disabled in Settings (default OFF)");
            return;
        }

        try
        {
            bool start;
            switch (action)
            {
                case ActExtStart: start = true; break;
                case ActExtStop: start = false; break;
                case ActExtToggle: start = !MainActivity.IntendedConnected; break;
                default: return;
            }

            var svc = new Intent()
                .SetClassName(context.PackageName!, SvcClass)!
                .SetAction(start ? SvcActionStart : SvcActionStop);

            if (start && Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(svc);
            else
                context.StartService(svc);

            Log.Info("VpnRouter", $"P4: external-control '{action}' -> service {(start ? "START" : "STOP")}");
        }
        catch (Exception ex)
        {
            Log.Warn("VpnRouter",
                $"P4: external-control '{action}' failed — {ex.GetType().Name}: {ex.Message} " +
                "(START needs prior VpnService consent + is background-FGS-limited on Android 12+)");
        }
    }
}
