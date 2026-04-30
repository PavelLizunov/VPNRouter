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
/// <c>AndroidManifest.xml</c> — so we don't have to duplicate the
/// registration there.</para>
///
/// <para>Phase 1.C wires VpnService consent + ACTION_START dispatch:
/// 3 seconds after launch we call <see cref="VpnService.Prepare"/>; if
/// consent is needed we present the system dialog via
/// <see cref="StartActivityForResult(Intent?, int)"/>; once granted we
/// fire ACTION_START at <see cref="VpnRouterService"/> with a minimal
/// direct-routing test config to exercise the libbox runtime end-to-end
/// on hardware. Phase 1.D will move this behind a real Connect button
/// in the shared App.axaml.</para>
/// </summary>
[Activity(
    Label = "VPNRouter",
    MainLauncher = true,
    // AppCompat theme required: Avalonia.AvaloniaActivity inherits from
    // AppCompatActivity, which crashes with IllegalStateException at
    // setContentView() unless the active theme is a Theme.AppCompat
    // descendant. Discovered via on-device Phase 0 test on KYOCERA A101BM
    // (Android 12, arm64) — Material theme launched OK on Activity Manager
    // but `am_proc_died: SIG 9` immediately after AvaloniaActivity.OnCreate.
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
    /// minimal log. No proxy server — the goal is just to verify libbox
    /// initialises, opens the TUN, and routes packets out via direct.
    /// Phase 1.D replaces this with config from Settings UI.
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
      "stack": "system",
      "endpoint_independent_nat": true
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

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
