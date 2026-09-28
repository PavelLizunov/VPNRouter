using System;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;

namespace VPNRouter.Android;

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

    private const string SvcActionStart = "com.ninitux.vpnrouter.RESTART";
    private const string SvcActionStop = "com.ninitux.vpnrouter.STOP";
    private const string SvcClass = "com.ninitux.vpnrouter.VpnRouterService";

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action;
        if (context is null || string.IsNullOrEmpty(action)) return;

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
                case ActExtToggle: start = !AndroidStorage.GetTunnelLive(); break;
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
