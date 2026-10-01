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

[Activity(
    Label = "VPNRouter",
    MainLauncher = true,
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
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
public class MainActivity : AvaloniaMainActivity
{
    private const int RequestVpnConsent = 0xBEEF;

    private const int RequestExportConfig = 0xC01E;
    private const int RequestImportConfig = 0xC01F;

    public static Thickness CurrentSafeArea { get; private set; }
    public static event Action<Thickness>? SafeAreaChanged;

    /// <summary>Height of the on-screen keyboard in dp (0 when it is hidden). With the enforced edge-to-edge windows of
    /// Android 15 and newer the window no longer shrinks for the keyboard, so the app has to make room itself.</summary>
    public static double CurrentImeBottom { get; private set; }
    public static event Action<double>? ImeChanged;

    private const int RequestCodeCameraQr = 0x4711;
    private const int RequestCodePostNotifications = 0x4712;

    private const int RequestCodeQrScan = 0x0000C0DE;

    private const string ActionStart = "com.ninitux.vpnrouter.START";
    private const string ActionStop = "com.ninitux.vpnrouter.STOP";
    private const string ExtraConfigJson = "config_json";
    private const string ExtraAllowedPackages = "allowed_packages";
    private const string ExtraPerAppMode = "per_app_mode";
    private const string ExtraPerAppPackages = "per_app_packages";
    private const string ExtraNotifText = "notif_text";
    private const string ExtraNotifDisconnect = "notif_disconnect";
    private const string ExtraDnsTunnelDomain = "dns_tunnel_domain";
    private const string ExtraDnsTunnelResolvers = "dns_tunnel_resolvers";
    private const string ExtraDnsTunnelCert = "dns_tunnel_cert";
    private const string ExtraDnsTunnelPort = "dns_tunnel_port";
    private const string ExtraDnsTunnelUseSystemResolver = "dns_tunnel_use_system_resolver";
    private const string ActionTunnelUp = "com.ninitux.vpnrouter.TUNNEL_UP";
    private const string ActionTunnelDown = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    private const string ActionTunnelError = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    private const string ExtraErrorMessage = "error_message";
    private const string ActionStats = "com.ninitux.vpnrouter.STATS";
    private const string ExtraStatsDown = "stats_down_total";
    private const string ExtraStatsUp = "stats_up_total";
    private const string ExtraStatsConn = "stats_conn";

    private TunnelStateReceiver? _tunnelReceiver;

    public static MainActivity? Instance { get; private set; }

    private bool IsDebuggable()
    {
        try
        {
            var appInfo = ApplicationInfo;
            if (appInfo is null) return false;
            return (appInfo.Flags & global::Android.Content.PM.ApplicationInfoFlags.Debuggable) != 0;
        }
        catch
        {
            return false;
        }
    }

    public static event Action<bool>? IntentChanged;

    public static event Action<string>? TunnelErrorReported;
    public static event Action<long, long, int>? StatsReported;

    private static bool _intendedConnected;
    public static bool IntendedConnected => _intendedConnected;

    public static string? LaunchCounterPath { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        try
        {
            var filesDir = FilesDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(filesDir))
            {
                VPNRouter.Core.AppPaths.OverrideDataDir(filesDir);
                VPNRouter.Core.AppPaths.EnsureDirectories();
                VPNRouter.Core.Services.CrashReporter.Install();
            }
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.CrashHook",
                    $"CrashReporter install failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        try { AndroidStorage.PruneSubServerDuplicatesOnce(); }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.Storage",
                    $"PruneSubServerDuplicatesOnce raised: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        try { AndroidStorage.PruneKnownPlaceholdersOnce(); }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.Storage",
                    $"PruneKnownPlaceholdersOnce raised: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        try
        {
            var filesDir = FilesDir?.AbsolutePath;
            if (!string.IsNullOrEmpty(filesDir))
            {
                LaunchCounterPath = System.IO.Path.Combine(filesDir, "launch-counter.json");
                var action = VPNRouter.Core.Services.LaunchFailureCounter.RecommendAction(LaunchCounterPath);
                if (action != "none")
                    DispatchAndroidLaunchRecovery(action);
                VPNRouter.Core.Services.LaunchFailureCounter.IncrementOnStartup(path: LaunchCounterPath);
            }
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"launch-counter early init failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        AvaloniaToggleNodeInfoProviderPatch.Apply();

        SetupWindowInsetsAndEdgeToEdge();

        base.OnCreate(savedInstanceState);
        Instance = this;
#if VPNROUTER_TESTHOOK
        global::Android.Util.Log.Warn("VpnRouterTest",
            "TEST HOOK BUILD: an exported adb test receiver is present; never distribute this APK");
#endif
        HandleTileIntent(Intent);

        _tunnelReceiver = new TunnelStateReceiver();
        var filter = new IntentFilter();
        filter.AddAction(ActionTunnelUp);
        filter.AddAction(ActionTunnelDown);
        filter.AddAction(ActionTunnelError);
        filter.AddAction(ActionStats);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            RegisterReceiver(_tunnelReceiver, filter, ReceiverFlags.NotExported);
        }
        else
        {
            RegisterReceiver(_tunnelReceiver, filter);
        }

        try
        {
            var cacheDir = CacheDir;
            if (cacheDir is not null && System.IO.Directory.Exists(cacheDir.AbsolutePath))
            {
                foreach (var f in System.IO.Directory.GetFiles(cacheDir.AbsolutePath, "qr_scan_*.jpg"))
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Bug-AND-011/Medium-3: QR temp sweep threw: {ex.GetType().Name}: {ex.Message}");
        }

        MaybeRequestPostNotifications();
    }

    private void MaybeRequestPostNotifications()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            if (AndroidStorage.GetPostNotifPromptShown()) return;
            AndroidStorage.SetPostNotifPromptShown(true);
            var granted = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                this, global::Android.Manifest.Permission.PostNotifications) == Permission.Granted;
            if (granted) return;
            AndroidX.Core.App.ActivityCompat.RequestPermissions(
                this,
                new[] { global::Android.Manifest.Permission.PostNotifications },
                RequestCodePostNotifications);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"B1: POST_NOTIFICATIONS request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetupWindowInsetsAndEdgeToEdge()
    {
        try
        {
            if (Window is null) return;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                Window.ClearFlags(WindowManagerFlags.TranslucentStatus | WindowManagerFlags.TranslucentNavigation);
                Window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
                Window.SetStatusBarColor(global::Android.Graphics.Color.Transparent);
                Window.SetNavigationBarColor(global::Android.Graphics.Color.Transparent);
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            }

            var decor = Window.DecorView;
            if (decor != null)
            {
                AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(decor, new InsetsListener(this));
                if (decor.ViewTreeObserver is { } observer)
                    observer.GlobalLayout += (_, _) => ReportImeInset();
                SetSystemBarsAppearance(AndroidStorage.GetTheme() == "dark");
            }
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.Insets",
                    $"SetupWindowInsetsAndEdgeToEdge failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }

    public void SetSystemBarsAppearance(bool isDark)
    {
        try
        {
            if (Window is null) return;
            var controller = AndroidX.Core.View.WindowCompat.GetInsetsController(Window, Window.DecorView);
            if (controller != null)
            {
                controller.AppearanceLightStatusBars = !isDark;
                controller.AppearanceLightNavigationBars = !isDark;
            }
        }
        catch (Exception ex)
        {
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.Insets",
                    $"SetSystemBarsAppearance failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }

    private void ReportImeInset()
    {
        try
        {
            var decor = Window?.DecorView;
            if (decor is null) return;
            var root = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(decor);
            var density = Resources?.DisplayMetrics?.Density ?? 1.0f;
            if (density <= 0.001f) density = 1.0f;
            var ime = root is null
                ? 0.0
                : root.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.Ime()).Bottom / (double)density;
            if (Math.Abs(ime - CurrentImeBottom) < 0.5) return;
            CurrentImeBottom = ime;
            ImeChanged?.Invoke(ime);
        }
        catch (Exception ex)
        {
            try { global::Android.Util.Log.Warn("VpnRouter.Insets", $"ReportImeInset failed: {ex.GetType().Name}: {ex.Message}"); }
            catch { }
        }
    }

    private sealed class InsetsListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        private readonly MainActivity _activity;
        public InsetsListener(MainActivity activity) => _activity = activity;

        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(
            global::Android.Views.View v,
            AndroidX.Core.View.WindowInsetsCompat insets)
        {
            try
            {
                var combined = insets.GetInsets(
                    AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() |
                    AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());

                var density = _activity.Resources?.DisplayMetrics?.Density ?? 1.0f;
                if (density <= 0.001f) density = 1.0f;

                var safeArea = new Thickness(
                    combined.Left / density,
                    combined.Top / density,
                    combined.Right / density,
                    combined.Bottom / density);

                if (CurrentSafeArea != safeArea)
                {
                    CurrentSafeArea = safeArea;
                    SafeAreaChanged?.Invoke(safeArea);
                }
                _activity.ReportImeInset();
            }
            catch (Exception ex)
            {
                try
                {
                    global::Android.Util.Log.Warn("VpnRouter.Insets",
                        $"OnApplyWindowInsets calculation failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }

            return insets;
        }
    }

    // Predictive back keeps calling this for apps that do not register their own callback (the activity's
    // OnBackPressedDispatcher falls back to it), so one override covers the button, the gesture and the adb key.
#pragma warning disable CA1422, CS0672
    public override void OnBackPressed()
    {
        try
        {
            if (AndroidApp.TryHandleBack())
                return;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter", $"back handling failed: {ex.GetType().Name}: {ex.Message}");
        }

        base.OnBackPressed();
    }
#pragma warning restore CA1422, CS0672

    protected override void OnDestroy()
    {
        if (_tunnelReceiver is not null)
        {
            try { UnregisterReceiver(_tunnelReceiver); } catch { }
            _tunnelReceiver = null;
        }
        if (ReferenceEquals(Instance, this))
            Instance = null;
        base.OnDestroy();
    }

    private static volatile bool _isActivityPaused;
    public static bool IsActivityPaused => _isActivityPaused;

    protected override void OnPause()
    {
        _isActivityPaused = true;
        base.OnPause();
    }

    protected override void OnResume()
    {
        _isActivityPaused = false;
        base.OnResume();
        try
        {
            var live = AndroidStorage.GetTunnelLive();
            var vpnActive = IsVpnTransportActive();
            if (VPNRouter.Core.Services.TunnelStateResync.TryResolveOnResume(
                    IntendedConnected, live, vpnActive, out var corrected))
            {
                global::Android.Util.Log.Info("VpnRouter",
                    $"resume re-sync: card showed Connected={IntendedConnected} but tunnel is "
                    + $"down (tunnel_live={live}, vpnActive={vpnActive}) — correcting card to "
                    + $"connected={corrected}");
                SetIntent(corrected);
            }
            else if (VPNRouter.Core.Services.TunnelStateResync.TryPromoteOnResume(
                         IntendedConnected, AndroidStorage.ResolveVpnState(), live, vpnActive))
            {
                global::Android.Util.Log.Info("VpnRouter",
                    "resume re-sync: the service reports a live tunnel that the card did not know about "
                    + "(started outside the app, for example from the quick-settings tile) — showing Connected");
                SetIntent(true);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"resume re-sync threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleTileIntent(intent);
    }

    /// <summary>
    /// The quick-settings tile could not act from the shade: it asks the app to show the system VPN consent dialog
    /// and connect, or to tell the user that nothing is set up yet. The intent is consumed so that a rotation does
    /// not repeat it.
    /// </summary>
    private void HandleTileIntent(Intent? intent)
    {
        if (intent?.Action != VPNRouter.Core.Services.TileIntentContract.ActionOpenFromTile)
            return;

        var reason = intent.GetStringExtra(VPNRouter.Core.Services.TileIntentContract.ExtraReason);
        intent.SetAction(null);
        global::Android.Util.Log.Info("VpnRouter", $"opened from the quick-settings tile (reason={reason})");

        switch (reason)
        {
            case VPNRouter.Core.Services.TileIntentContract.ReasonPermission:
            case VPNRouter.Core.Services.TileIntentContract.ReasonConnect:
                new Handler(Looper.MainLooper!).Post(RequestConnect);
                break;
            case VPNRouter.Core.Services.TileIntentContract.ReasonSetup:
                global::Android.Widget.Toast.MakeText(this, Localization.TileSetupNeeded, global::Android.Widget.ToastLength.Long)?.Show();
                break;
        }
    }

    private bool IsVpnTransportActive() => IsVpnTransportActive(this);

    internal static bool IsVpnTransportActive(Context? ctx)
    {
        try
        {
            if (ctx?.GetSystemService(ConnectivityService) is not ConnectivityManager cm)
                return true;
            var networks = cm.GetAllNetworks();
            if (networks is null) return true;
            foreach (var n in networks)
            {
                var caps = cm.GetNetworkCapabilities(n);
                if (caps is not null && caps.HasTransport(TransportType.Vpn))
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

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
                    try { TunnelErrorReported?.Invoke(msg); }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("VpnRouter",
                            $"AND-DIAG: TunnelErrorReported handler threw: {ex}");
                    }
                    SetIntent(false);
                    break;
                case ActionStats:
                    if (_isActivityPaused) break;
                    try
                    {
                        long d = intent?.GetLongExtra(ExtraStatsDown, 0L) ?? 0L;
                        long u = intent?.GetLongExtra(ExtraStatsUp, 0L) ?? 0L;
                        int c = intent?.GetIntExtra(ExtraStatsConn, 0) ?? 0;
                        StatsReported?.Invoke(d, u, c);
                    }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("VpnRouter", $"P1: StatsReported handler threw: {ex.Message}");
                    }
                    break;
            }
        }
    }

    private readonly VPNRouter.Core.Services.ConnectConsentGate _consentGate = new();

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
            if (!_consentGate.TryOpen())
            {
                global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: consent dialog is already open, ignoring the repeated request");
                return;
            }
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: presenting system VPN consent dialog");
            try
            {
                StartActivityForResult(prepareIntent, RequestVpnConsent);
            }
            catch
            {
                _consentGate.Close();
                throw;
            }
        }
    }

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

        if (requestCode == RequestExportConfig)
        {
            HandleExportResult(resultCode, data);
            return;
        }
        if (requestCode == RequestImportConfig)
        {
            HandleImportResult(resultCode, data);
            return;
        }

        if (requestCode == RequestCodeQrScan)
        {
            HandleQrScanResult(resultCode, data);
            return;
        }

        if (requestCode != RequestVpnConsent)
        {
            return;
        }

        _consentGate.Close();

        if (resultCode == Result.Ok)
        {
            global::Android.Util.Log.Info("VpnRouter", "Phase 1.D: consent granted");
            StartTunnelService();
        }
        else
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Phase 1.D: consent denied (resultCode={resultCode})");
            SetIntent(false);
        }
    }

    private static string? _pendingExportContent;

    public static Action<bool, string?>? PendingExportCallback;

    public static Action<bool, string?>? PendingImportCallback;

    public void RequestExportConfigShare(string content, string suggestedName)
    {
        _pendingExportContent = content;
        try
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.SetType("application/json");
            intent.AddCategory(Intent.CategoryOpenable);
            intent.PutExtra(Intent.ExtraTitle, suggestedName);
            StartActivityForResult(intent, RequestExportConfig);
        }
        catch (Exception ex)
        {
            _pendingExportContent = null;
            PendingExportCallback?.Invoke(false, $"{ex.GetType().Name}: {ex.Message}");
            PendingExportCallback = null;
        }
    }

    public void RequestImportConfigShare()
    {
        try
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.SetType("application/json");
            intent.AddCategory(Intent.CategoryOpenable);
            StartActivityForResult(intent, RequestImportConfig);
        }
        catch (Exception ex)
        {
            PendingImportCallback?.Invoke(false, $"{ex.GetType().Name}: {ex.Message}");
            PendingImportCallback = null;
        }
    }

    private void HandleExportResult(Result resultCode, Intent? data)
    {
        var pendingJson = _pendingExportContent;
        _pendingExportContent = null;
        var callback = PendingExportCallback;
        PendingExportCallback = null;

        if (resultCode != Result.Ok || data?.Data is null)
        {
            callback?.Invoke(false, "cancelled");
            return;
        }
        if (string.IsNullOrEmpty(pendingJson))
        {
            callback?.Invoke(false, "no pending content");
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var stream = ContentResolver!.OpenOutputStream(data.Data);
                if (stream is null)
                {
                    callback?.Invoke(false, "openOutputStream returned null");
                    return;
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(pendingJson);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                callback?.Invoke(true, data.Data.ToString());
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("VpnRouter.ConfigShare",
                    $"export write failed: {ex.GetType().Name}: {ex.Message}");
                callback?.Invoke(false, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void HandleImportResult(Result resultCode, Intent? data)
    {
        var callback = PendingImportCallback;
        PendingImportCallback = null;

        if (resultCode != Result.Ok || data?.Data is null)
        {
            callback?.Invoke(false, "cancelled");
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var stream = ContentResolver!.OpenInputStream(data.Data);
                if (stream is null)
                {
                    callback?.Invoke(false, "openInputStream returned null");
                    return;
                }
                using var sr = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                var content = sr.ReadToEnd();
                callback?.Invoke(true, content);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("VpnRouter.ConfigShare",
                    $"import read failed: {ex.GetType().Name}: {ex.Message}");
                callback?.Invoke(false, $"{ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public static Action<bool, string?>? PendingQrScanCallback;

    public void RequestQrCodeScan()
    {
        try
        {
            var granted = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                this, global::Android.Manifest.Permission.Camera) == Permission.Granted;
            if (granted)
            {
                LaunchQrScanner();
                return;
            }
            AndroidX.Core.App.ActivityCompat.RequestPermissions(
                this,
                new[] { global::Android.Manifest.Permission.Camera },
                RequestCodeCameraQr);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.QrScan",
                $"RequestQrCodeScan failed: {ex.GetType().Name}: {ex.Message}");
            var cb = PendingQrScanCallback;
            PendingQrScanCallback = null;
            cb?.Invoke(false, $"camera_unavailable:{ex.Message}");
        }
    }

    private void LaunchQrScanner()
    {
        try
        {
            var cls = Java.Lang.Class.ForName("com.ninitux.vpnrouter.QrScanLauncher");
            var activityCls = Java.Lang.Class.ForName("android.app.Activity");
            var method = cls.GetMethod("launch", activityCls);
            method.Invoke(null, this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.QrScan",
                $"LaunchQrScanner failed: {ex.GetType().Name}: {ex.Message}");
            var cb = PendingQrScanCallback;
            PendingQrScanCallback = null;
            cb?.Invoke(false, $"camera_unavailable:{ex.Message}");
        }
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        [global::Android.Runtime.GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestCodeCameraQr) return;

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            LaunchQrScanner();
        }
        else
        {
            var cb = PendingQrScanCallback;
            PendingQrScanCallback = null;
            cb?.Invoke(false, "permission_denied");
        }
    }

    private void HandleQrScanResult(Result resultCode, Intent? data)
    {
        var cb = PendingQrScanCallback;
        PendingQrScanCallback = null;

        try
        {
            var cls = Java.Lang.Class.ForName("com.ninitux.vpnrouter.QrScanLauncher");
            var intCls = Java.Lang.Integer.Type;
            var intentCls = Java.Lang.Class.ForName("android.content.Intent");
            var method = cls.GetMethod("parseResult", intCls, intCls, intentCls);
            var result = method.Invoke(null,
                Java.Lang.Integer.ValueOf(RequestCodeQrScan),
                Java.Lang.Integer.ValueOf((int)resultCode),
                data);
            var text = result?.ToString();

            if (text is null)
            {
                cb?.Invoke(false, "not_recognized");
                return;
            }
            if (text.Length == 0)
            {
                cb?.Invoke(false, "cancelled");
                return;
            }
            cb?.Invoke(true, text);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.QrScan",
                $"HandleQrScanResult failed: {ex.GetType().Name}: {ex.Message}");
            cb?.Invoke(false, $"decode_error:{ex.Message}");
        }
    }

    private async void StartTunnelService()
    {
        string? singboxLogPath = null;
        try
        {
            var filesDir = FilesDir;
            if (filesDir is not null)
            {
                singboxLogPath = System.IO.Path.Combine(filesDir.AbsolutePath, "singbox.log");
                global::Android.Util.Log.Info("VpnRouter",
                    $"Bug-AND-011: sing-box log.output → {singboxLogPath} (private sandbox)");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Bug-AND-011: FilesDir failed — {ex.GetType().Name}: {ex.Message}");
        }

        var configMode = AndroidStorage.GetConfigMode();
        global::Android.Util.Log.Info("VpnRouter",
            $"AND-CC: ConfigMode={configMode}");

        string configJson;
        if (configMode == "custom")
        {
            var rawJson = AndroidStorage.GetCustomConfigJson();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                global::Android.Util.Log.Error("VpnRouter",
                    "AND-CC: ConfigMode=custom but custom_config_json is empty");
                SetIntent(false);
                return;
            }

            try
            {
                configJson = await System.Threading.Tasks.Task.Run(
                    () => AndroidConfigBuilder.BuildConfigJsonFromCustom(rawJson, singboxLogPath));
                global::Android.Util.Log.Info("VpnRouter",
                    $"AND-CC: custom JSON injected ({rawJson.Length} → {configJson.Length} chars)");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("VpnRouter",
                    $"AND-CC: BuildConfigJsonFromCustom failed — {ex.GetType().Name}: {ex.Message}");
                SetIntent(false);
                return;
            }

            // The config dump contains credentials (UUID, Reality keys): debug builds only.
            if (IsDebuggable() && singboxLogPath is not null)
            {
                try
                {
                    var configDumpPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(singboxLogPath)!,
                        "config-dump.json");
                    System.IO.File.WriteAllText(configDumpPath, configJson);
                }
                catch (Exception dumpEx)
                {
                    global::Android.Util.Log.Warn("VpnRouter",
                        $"AND-CC: config dump failed — {dumpEx.GetType().Name}: {dumpEx.Message}");
                }
            }

            DispatchTunnelStart(configJson);
            return;
        }

        VPNRouter.Core.Models.VlessServerEntry entry;
        try
        {
            string? testUri = null;
            if (IsDebuggable())
            {
                try
                {
                    var filesDir = FilesDir;
                    if (filesDir is not null)
                    {
                        var path = System.IO.Path.Combine(filesDir.AbsolutePath, "test-uri.txt");
                        if (System.IO.File.Exists(path))
                        {
                            testUri = System.IO.File.ReadAllText(path).Trim();
                            global::Android.Util.Log.Info("VpnRouter",
                                $"Bug-AND-011: test-uri.txt override active (debug build, {testUri.Length} chars)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("VpnRouter",
                        $"Bug-AND-011: test-uri.txt read failed — {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(testUri))
            {
                entry = VPNRouter.Core.Services.ServerUriParser.Parse(testUri);
            }
            else
            {
                var resolved = AndroidStorage.GetActiveServer();
                if (resolved is null)
                {
                    var msg = global::VPNRouter.Core.Localization.Strings.AndroidErrorNoServerConfigured;
                    global::Android.Util.Log.Error("VpnRouter",
                        $"DEFCT-005: GetActiveServer returned null — {msg}");
                    try
                    {
                        TunnelErrorReported?.Invoke(msg);
                    }
                    catch (Exception cbEx)
                    {
                        global::Android.Util.Log.Warn("VpnRouter",
                            $"DEFCT-005: TunnelErrorReported callback raised: {cbEx.Message}");
                    }
                    SetIntent(false);
                    return;
                }
                entry = resolved;
            }
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

        try
        {
            configJson = await System.Threading.Tasks.Task.Run(
                () => AndroidConfigBuilder.BuildConfigJson(entry, singboxLogPath));
            // The config dump contains credentials (UUID, Reality keys): debug builds only.
            if (IsDebuggable() && singboxLogPath is not null)
            {
                try
                {
                    var configDumpPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(singboxLogPath)!,
                        "config-dump.json");
                    System.IO.File.WriteAllText(configDumpPath, configJson);
                    global::Android.Util.Log.Info("VpnRouter",
                        $"Phase 6.2 debug: config dumped to {configDumpPath} ({configJson.Length} chars)");
                }
                catch (Exception dumpEx)
                {
                    global::Android.Util.Log.Warn("VpnRouter",
                        $"Phase 6.2 debug: config dump failed — {dumpEx.GetType().Name}: {dumpEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter",
                $"Phase 1.H: failed to generate sing-box config — {ex.GetType().Name}: {ex.Message}");
            SetIntent(false);
            return;
        }

        DispatchTunnelStart(configJson, entry);
    }

    private void DispatchTunnelStart(string configJson, VPNRouter.Core.Models.VlessServerEntry? dnsTunnelEntry = null)
    {
        var perAppMode = AndroidStorage.GetPerAppMode();
        var perAppPackages = AndroidStorage.GetPerAppPackages().ToArray();
        global::Android.Util.Log.Info("VpnRouter",
            $"Phase 7.5: per-app mode={perAppMode}, packages={perAppPackages.Length}");

        var intent = new Intent()
            .SetClassName(PackageName!, "com.ninitux.vpnrouter.VpnRouterService")
            .SetAction(ActionStart)
            .PutExtra(ExtraConfigJson, configJson)
            .PutExtra(ExtraAllowedPackages, Array.Empty<string>())
            .PutExtra(ExtraPerAppMode, perAppMode)
            .PutExtra(ExtraPerAppPackages, perAppPackages)
            .PutExtra(ExtraNotifText, Localization.NotifTunnelActive)
            .PutExtra(ExtraNotifDisconnect, Localization.NotifDisconnect);

        if (dnsTunnelEntry is not null &&
            string.Equals(dnsTunnelEntry.Protocol, "dns-tunnel", System.StringComparison.OrdinalIgnoreCase))
        {
            var resolverList = new System.Collections.Generic.List<string>();
            if (dnsTunnelEntry.DnsResolvers is not null)
            {
                foreach (var r in dnsTunnelEntry.DnsResolvers)
                {
                    if (!string.IsNullOrWhiteSpace(r))
                        resolverList.Add(r.Trim());
                }
            }
            var resolvers = resolverList.ToArray();
            intent
                .PutExtra(ExtraDnsTunnelDomain, dnsTunnelEntry.DnsDomain ?? string.Empty)
                .PutExtra(ExtraDnsTunnelResolvers, resolvers)
                .PutExtra(ExtraDnsTunnelCert, dnsTunnelEntry.DnsLeafCertPem ?? string.Empty)
                .PutExtra(ExtraDnsTunnelPort, VPNRouter.Core.Services.SlipstreamManager.DefaultLocalPort)
                .PutExtra(ExtraDnsTunnelUseSystemResolver, dnsTunnelEntry.DnsUseSystemResolver);
            global::Android.Util.Log.Info("VpnRouter",
                $"dns-tunnel: forwarding slipstream params (domain={dnsTunnelEntry.DnsDomain}, " +
                $"resolvers={resolvers.Length}, systemResolver={dnsTunnelEntry.DnsUseSystemResolver})");
        }

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

    private void DispatchAndroidLaunchRecovery(string action)
    {
        try
        {
            switch (action)
            {
                case "self-repair":
                    global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                        "3 launch failures in a row — clearing transient caches");
                    TryClearAndroidCaches();
                    break;

                case "config-reset":
                    global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                        "5 launch failures — clearing caches AND resetting user settings");
                    TryClearAndroidCaches();
                    try { AndroidStorage.ResetUserSettings(); }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                            $"ResetUserSettings failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    break;

                case "safe-mode-prompt":
                    global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                        "7 launch failures — surfacing safe-mode prompt");
                    AndroidStorage.QueueSafeModeBannerForUi();
                    break;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.SelfRepair",
                $"DispatchAndroidLaunchRecovery({action}) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TryClearAndroidCaches()
    {
        try
        {
            var cacheDir = VPNRouter.Core.AppPaths.CacheDir;
            if (System.IO.Directory.Exists(cacheDir))
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(cacheDir, "*.json"))
                {
                    try { System.IO.File.Delete(f); }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                            $"could not delete {f}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                $"cache wipe failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var ext = GetExternalFilesDir(null);
            if (ext != null)
            {
                foreach (var name in new[] { "singbox.log", "config-dump.json" })
                {
                    var path = System.IO.Path.Combine(ext.AbsolutePath, name);
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                    catch { }
                }
            }
        }
        catch { }
    }
}
