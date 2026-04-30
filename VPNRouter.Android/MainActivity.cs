using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Android port — entry Activity.
///
/// <para>Inherits from <see cref="AvaloniaMainActivity{TApp}"/> so the
/// Avalonia framework spins up our XAML-driven UI inside this Activity's
/// lifecycle. The <c>[Activity]</c> attribute is what .NET Android uses
/// to auto-generate the corresponding <c>&lt;activity&gt;</c> entry inside
/// <c>AndroidManifest.xml</c>.</para>
///
/// <para>Phase 1.C wires VpnService consent + ACTION_START dispatch:
/// 3 seconds after launch we call <see cref="VpnService.Prepare"/>; if
/// consent is needed we present the system dialog via
/// <see cref="StartActivityForResult(Intent?, int)"/>; once granted we
/// fire ACTION_START at the (Java) <c>VpnRouterService</c> with a
/// minimal direct-routing test config to exercise the libbox runtime
/// end-to-end on hardware.</para>
/// </summary>
[Activity(
    Label = "VPNRouter",
    MainLauncher = true,
    // AppCompat theme required: Avalonia.AvaloniaActivity inherits from
    // AppCompatActivity, which crashes with IllegalStateException at
    // setContentView() unless the active theme is a Theme.AppCompat
    // descendant.
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges =
        ConfigChanges.ScreenSize |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.Keyboard |
        ConfigChanges.ScreenLayout |
        ConfigChanges.UiMode |
        ConfigChanges.FontScale |
        ConfigChanges.Locale |
        ConfigChanges.Navigation |
        ConfigChanges.Orientation |
        ConfigChanges.Density)]
public class MainActivity : AvaloniaMainActivity<AndroidApp>
{
    private const int RequestVpnConsent = 0xBEEF;

    /// <summary>
    /// Phase 1.C smoke-test config: TUN inbound + direct outbound +
    /// minimal log + Clash API. No proxy server — the goal is to
    /// verify libbox initialises, opens the TUN, and routes packets out
    /// via direct.
    /// </summary>
    private const string Phase1cTestConfig = """
{
  "log": { "level": "info" },
  "inbounds": [
    {
      "type": "tun",
      "tag": "tun-in",
      "interface_name": "tun0",
      "address": ["172.19.0.1/30", "fdfe:dcba:9876::1/126"],
      "mtu": 1500,
      "auto_route": true,
      "stack": "system"
    }
  ],
  "outbounds": [
    { "type": "direct", "tag": "direct" }
  ],
  "experimental": {
    "clash_api": {
      "external_controller": "127.0.0.1:9090"
    }
  }
}
""";

    // Mirrors VpnRouterService.java's intent contract.
    private const string ActionStart = "com.ninitux.vpnrouter.START";
    private const string ExtraConfigJson = "config_json";
    private const string ExtraAllowedPackages = "allowed_packages";

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Schedule the libbox start ~3 seconds after onCreate so the
        // Activity is fully attached + Avalonia surface visible before
        // the system VpnService consent dialog appears.
        new Handler(Looper.MainLooper!).PostDelayed(SchedulePhase1cStart, 3000);
    }

    private void SchedulePhase1cStart()
    {
        global::Android.Util.Log.Info("VpnRouter", "Phase 1.C: requesting VPN consent");
        var prepareIntent = VpnService.Prepare(this);
        if (prepareIntent is null)
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.C: consent already granted, starting service");
            StartTunnelService();
        }
        else
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.C: presenting system VPN consent dialog");
            StartActivityForResult(prepareIntent, RequestVpnConsent);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RequestVpnConsent)
        {
            return;
        }

        if (resultCode == Result.Ok)
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.C: consent granted");
            StartTunnelService();
        }
        else
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Phase 1.C: consent denied (resultCode={resultCode})");
        }
    }

    private void StartTunnelService()
    {
        // VpnRouterService is a Java class (VpnRouterService.java) — we
        // address it via fully-qualified component name rather than
        // typeof() because the .NET Android side has no C# binding for it.
        var intent = new Intent()
            .SetClassName(PackageName!, "com.ninitux.vpnrouter.VpnRouterService")
            .SetAction(ActionStart)
            .PutExtra(ExtraConfigJson, Phase1cTestConfig)
            .PutExtra(ExtraAllowedPackages, Array.Empty<string>());

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            StartForegroundService(intent);
        }
        else
        {
            StartService(intent);
        }
    }
}
