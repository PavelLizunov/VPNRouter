using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Views;
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
    // v3.0 Phase 2 (2026-05-04) — soft-input policy.
    //   StateHidden: keyboard NOT auto-shown when activity opens. Pre-2,
    //     the multi-line TextBox auto-grabbed focus → keyboard popped up
    //     unexpectedly when user just wanted to read the screen.
    //   AdjustResize: when keyboard DOES appear (user taps input), the
    //     view resizes so the focused field stays visible above the keys
    //     instead of being covered.
    WindowSoftInputMode = SoftInput.StateHidden | SoftInput.AdjustResize,
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
    // v3.0 Phase 1.I — broadcasts from VpnRouterService so the UI can
    // mirror REAL tunnel state, not just intent.
    private const string ActionTunnelUp = "com.ninitux.vpnrouter.TUNNEL_UP";
    private const string ActionTunnelDown = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    private const string ActionTunnelError = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    private const string ExtraErrorMessage = "error_message";

    private TunnelStateReceiver? _tunnelReceiver;

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

        // v3.0 Phase 1.I — listen for tunnel-up / tunnel-down / tunnel-error
        // broadcasts from VpnRouterService.java so the UI button reflects
        // REAL tunnel state. Pre-1.I the button only showed intent ("we
        // asked the OS to start"), now it shows actual.
        _tunnelReceiver = new TunnelStateReceiver();
        var filter = new IntentFilter();
        filter.AddAction(ActionTunnelUp);
        filter.AddAction(ActionTunnelDown);
        filter.AddAction(ActionTunnelError);
        // Android 13+ requires explicit RECEIVER_NOT_EXPORTED for unprotected
        // broadcasts (we set Intent.SetPackage in the broadcaster, so this
        // is process-local — safe).
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            RegisterReceiver(_tunnelReceiver, filter, ReceiverFlags.NotExported);
        }
        else
        {
            RegisterReceiver(_tunnelReceiver, filter);
        }
    }

    protected override void OnDestroy()
    {
        if (_tunnelReceiver is not null)
        {
            try { UnregisterReceiver(_tunnelReceiver); } catch { /* no-op */ }
            _tunnelReceiver = null;
        }
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    /// <summary>
    /// v3.0 Phase 1.I — receives tunnel state broadcasts from the
    /// foreground VpnRouterService. Updates <see cref="_intendedConnected"/>
    /// (now misnamed — it's the real state, not just intent — but the
    /// existing IntentChanged event keeps its name for back-compat with
    /// the AndroidApp UI).
    /// </summary>
    private sealed class TunnelStateReceiver : global::Android.Content.BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var action = intent?.Action;
            if (string.IsNullOrEmpty(action)) return;

            switch (action)
            {
                case ActionTunnelUp:
                    global::Android.Util.Log.Info("VpnRouter", "Phase 1.I: ACTION_TUNNEL_UP received");
                    SetIntent(true);
                    break;
                case ActionTunnelDown:
                    global::Android.Util.Log.Info("VpnRouter", "Phase 1.I: ACTION_TUNNEL_DOWN received");
                    SetIntent(false);
                    break;
                case ActionTunnelError:
                    var msg = intent?.GetStringExtra(ExtraErrorMessage) ?? "(no detail)";
                    global::Android.Util.Log.Warn("VpnRouter", $"Phase 1.I: ACTION_TUNNEL_ERROR — {msg}");
                    SetIntent(false);
                    break;
            }
        }
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
        // v3.0 Phase 1.H (2026-05-04): resolve the active server from
        // AndroidStorage. Three sources, in priority order:
        //   1. Subscription server selected by Name (Phase 1.H)
        //   2. Manual vless:// URI (Phase 1.F)
        //   3. Hardcoded placeholder (smoke-test fallback)
        // AndroidStorage.GetActiveServer encapsulates 1+2; if it returns
        // null we fall back to placeholder.
        VPNRouter.Core.Models.VlessServerEntry entry;
        try
        {
            entry = AndroidStorage.GetActiveServer()
                ?? VPNRouter.Core.Services.VlessUriParser.Parse(PlaceholderVlessUri);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter",
                $"Phase 1.H: failed to resolve server entry — {ex.GetType().Name}: {ex.Message}");
            SetIntent(false);
            return;
        }

        var label = string.IsNullOrEmpty(entry.Name) ? entry.Server : entry.Name;
        global::Android.Util.Log.Info("VpnRouter",
            $"Phase 1.H: using server {label} ({entry.Server}:{entry.Port})");

        // v3.0 Phase 6.1 (2026-05-04) — point sing-box log.output at a
        // world-readable file under getExternalFilesDir() so we can pull
        // the real sing-box errors via plain `adb shell cat` (no root,
        // no run-as). Pre-6.1 log.output was removed → sing-box wrote
        // to stderr → libbox.redirectStderr captured Go-runtime panics
        // only, NOT sing-box's internal logger. Result: empty stderr
        // file even when routing failed silently.
        //
        // Path: /sdcard/Android/data/com.ninitux.vpnrouter/files/singbox.log
        string? singboxLogPath = null;
        try
        {
            var extDir = GetExternalFilesDir(null);
            if (extDir is not null)
            {
                singboxLogPath = System.IO.Path.Combine(extDir.AbsolutePath, "singbox.log");
                global::Android.Util.Log.Info("VpnRouter",
                    $"Phase 6.1: sing-box log.output → {singboxLogPath}");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Phase 6.1: GetExternalFilesDir failed — {ex.GetType().Name}: {ex.Message}");
        }

        string configJson;
        try
        {
            configJson = AndroidConfigBuilder.BuildConfigJson(entry, singboxLogPath);
            // Phase 6.2 debug — write the JSON we hand to libbox to a
            // world-readable file for offline inspection. Useful when
            // diagnosing routing issues like "TCP packets never reach
            // TUN inbound".
            try
            {
                if (singboxLogPath is not null)
                {
                    var configDumpPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(singboxLogPath)!,
                        "config-dump.json");
                    System.IO.File.WriteAllText(configDumpPath, configJson);
                    global::Android.Util.Log.Info("VpnRouter",
                        $"Phase 6.2 debug: config dumped to {configDumpPath} ({configJson.Length} chars)");
                }
            }
            catch (Exception dumpEx)
            {
                global::Android.Util.Log.Warn("VpnRouter",
                    $"Phase 6.2 debug: config dump failed — {dumpEx.GetType().Name}: {dumpEx.Message}");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter",
                $"Phase 1.H: failed to generate sing-box config — {ex.GetType().Name}: {ex.Message}");
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
