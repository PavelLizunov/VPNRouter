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
/// <para>Phase 1.D (current): the Connect / Disconnect actions are wired
/// to the Avalonia UI in <see cref="AndroidApp"/>. The Activity exposes
/// <see cref="Instance"/> + <see cref="RequestConnect"/> /
/// <see cref="RequestDisconnect"/> so the button click handlers can talk
/// to Android-only APIs (<c>VpnService.Prepare</c>,
/// <c>StartActivityForResult</c>, <c>StartForegroundService</c>) without
/// pulling Android.* references into the shared Avalonia layer.</para>
///
/// <para>Phase 1.C (replaced): used to auto-start the libbox tunnel ~3 s
/// after launch via a <c>Handler.PostDelayed</c> smoke-test. That worked
/// well enough for end-to-end runtime verification on hardware but
/// hard-coded the consent flow and gave the user no way to disconnect.
/// 1.D moves the trigger behind a proper Connect button.</para>
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
    /// Phase 1.E (2026-05-04) — placeholder VLESS-Reality URI used to
    /// smoke-test the full VPNRouter.Core → ConfigGenerator → libbox
    /// pipeline end-to-end. Same URI used in the desktop unit-test
    /// fixture for VlessUriParser (see VPNRouter.Tests/UnitTest1.cs
    /// VlessUriParserTests.RealityUri). Server is a real Reality
    /// endpoint that the test suite uses; the VLESS UUID is published
    /// in the open-source repo and works for verification but isn't a
    /// production server.
    ///
    /// <para>Phase 1.F will replace this with a stored subscription
    /// URL pulled from SettingsLoader once the Avalonia subscription
    /// settings UI is ported. For Phase 1.E the goal is just to
    /// confirm the Core pipeline + libbox handshake works — once the
    /// generated JSON spawns a libbox service without errors we know
    /// the bridge is sound.</para>
    /// </summary>
    private const string PlaceholderVlessUri =
        "vless://2d54442d-158f-49e2-b225-67ba1a5b77f4@194.87.222.111:443" +
        "?security=reality&sni=yahoo.com&fp=firefox" +
        "&pbk=DnT9hIvt5QEx07unHUeXbWxN4Qo1gnecN4p0s62nckU&sid=78ca7952" +
        "&spx=/&type=tcp&flow=xtls-rprx-vision&encryption=none#android-test";

    // Mirrors VpnRouterService.java's intent contract.
    private const string ActionStart = "com.ninitux.vpnrouter.START";
    private const string ActionStop = "com.ninitux.vpnrouter.STOP";
    private const string ExtraConfigJson = "config_json";
    private const string ExtraAllowedPackages = "allowed_packages";

    /// <summary>
    /// Singleton-ish reference so the Avalonia button handlers in
    /// <see cref="AndroidApp"/> can reach the Activity-scoped Android APIs
    /// (consent dialog, foreground service start). Set in
    /// <see cref="OnCreate"/>, cleared in <see cref="OnDestroy"/>.
    /// </summary>
    public static MainActivity? Instance { get; private set; }

    /// <summary>
    /// Fires whenever <see cref="RequestConnect"/> /
    /// <see cref="RequestDisconnect"/> changes the user-visible tunnel
    /// intent. The Avalonia UI subscribes so it can flip the button label
    /// and status text. Phase 1.D scope: fire-and-forget — the boolean
    /// reflects "we asked the OS to start/stop", not "the tunnel is
    /// actually carrying packets". Real state sync (libbox status →
    /// broadcast → UI) is Phase 1.E.
    /// </summary>
    public static event Action<bool>? IntentChanged;

    private static bool _intendedConnected;
    public static bool IntendedConnected => _intendedConnected;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Instance = this;
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Phase 1.D: invoked by the Avalonia Connect button. Asks Android for
    /// VpnService consent (system dialog the first time per app
    /// installation, instant otherwise) and dispatches an ACTION_START
    /// intent at <c>VpnRouterService</c> with the smoke-test config.
    /// </summary>
    public void RequestConnect()
    {
        global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: Connect requested by UI");
        var prepareIntent = VpnService.Prepare(this);
        if (prepareIntent is null)
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: consent already granted, starting service");
            StartTunnelService();
        }
        else
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: presenting system VPN consent dialog");
            StartActivityForResult(prepareIntent, RequestVpnConsent);
        }
    }

    /// <summary>
    /// Phase 1.D: invoked by the Avalonia Disconnect button. Sends an
    /// ACTION_STOP intent at the (foreground) <c>VpnRouterService</c>,
    /// which tears down the libbox tunnel + closes the system tun fd +
    /// removes the persistent notification.
    /// </summary>
    public void RequestDisconnect()
    {
        global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: Disconnect requested by UI");
        var intent = new Intent()
            .SetClassName(PackageName!, "com.ninitux.vpnrouter.VpnRouterService")
            .SetAction(ActionStop);
        StartService(intent);
        SetIntent(false);
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
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: consent granted");
            StartTunnelService();
        }
        else
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Phase 1.D: consent denied (resultCode={resultCode})");
            // User declined — keep IntendedConnected false so the button
            // resets to "Connect" and they can retry.
            SetIntent(false);
        }
    }

    private void StartTunnelService()
    {
        // v3.0 Phase 1.E (2026-05-04): generate the sing-box config from
        // VPNRouter.Core.ConfigGenerator instead of using the hand-rolled
        // smoke-test JSON. AndroidConfigBuilder.BuildConfigJson runs the
        // same pipeline as desktop (parse VLESS URI → AppSettings →
        // ConfigGenerator → JSON) so the Android app gets DNS routing,
        // Reality fingerprinting, route rules etc. for free. Generation
        // failures fall back to logging + cancelling the start.
        string configJson;
        try
        {
            configJson = AndroidConfigBuilder.BuildConfigJson(PlaceholderVlessUri);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter",
                $"Phase 1.E: failed to generate sing-box config — {ex.GetType().Name}: {ex.Message}");
            SetIntent(false);
            return;
        }

        // VpnRouterService is a Java class (VpnRouterService.java) — we
        // address it via fully-qualified component name rather than
        // typeof() because the .NET Android side has no C# binding for it.
        var intent = new Intent()
            .SetClassName(PackageName!, "com.ninitux.vpnrouter.VpnRouterService")
            .SetAction(ActionStart)
            .PutExtra(ExtraConfigJson, configJson)
            .PutExtra(ExtraAllowedPackages, Array.Empty<string>());

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            StartForegroundService(intent);
        }
        else
        {
            StartService(intent);
        }
        SetIntent(true);
    }

    private static void SetIntent(bool connected)
    {
        if (_intendedConnected == connected) return;
        _intendedConnected = connected;
        try { IntentChanged?.Invoke(connected); }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Phase 1.D: IntentChanged handler raised: {ex}");
        }
    }
}
