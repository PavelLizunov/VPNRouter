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

    // v2.32.0 (Android-led, 2026-05-07) — config share intent codes.
    // Distinct request codes so OnActivityResult can dispatch the result
    // to the right handler. CreateDocument is for export (write JSON to
    // a user-picked location); OpenDocument is for import (read JSON
    // from a user-picked file).
    private const int RequestExportConfig = 0xC01E;
    private const int RequestImportConfig = 0xC01F;

    // lucid-pike (2026-05-09) — Simple-page QR scan.
    //
    //   RequestCodeCameraQr: shared between the runtime-permission request
    //     (ActivityCompat.RequestPermissions) and the camera-intent
    //     StartActivityForResult. OnRequestPermissionsResult and
    //     OnActivityResult both branch on it.
    //
    // Distinct from VPN consent / config share request codes so cross-
    // dispatch can't mistake the camera result for a VpnService.Prepare
    // outcome.
    private const int RequestCodeCameraQr = 0x4711;

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
    // v3.0 Phase 7.5 — per-app filter intent extras (handbook §5.5).
    private const string ExtraPerAppMode = "per_app_mode";
    private const string ExtraPerAppPackages = "per_app_packages";
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

    /// <summary>
    /// v2.32.0 (AND-DIAG, 2026-05-07) — fires when VpnRouterService
    /// broadcasts ACTION_TUNNEL_ERROR. Carries the EXTRA_ERROR_MESSAGE
    /// payload so the UI status card can surface a one-liner under the
    /// status dot, mirroring desktop's StatusErrorBadge pattern. Pre-DIAG
    /// the receiver only logged the message; the user saw a silent
    /// disconnect with no clue why. Subscribers should marshal to UI
    /// thread (the receiver runs on a binder dispatch thread).
    /// </summary>
    public static event Action<string>? TunnelErrorReported;

    private static bool _intendedConnected;
    public static bool IntendedConnected => _intendedConnected;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    /// <summary>
    /// v2.32.0 SR-2 (Android port) — path of the launch-failure counter
    /// JSON, resolved against the app's private internal data dir
    /// (<c>/data/user/0/&lt;package&gt;/files</c>). Set in
    /// <see cref="OnCreate"/> as the very first thing so both the
    /// strikes-bumping and MarkStable paths see a deterministic file
    /// location regardless of <see cref="VPNRouter.Core.AppPaths"/>'s
    /// Linux-branch resolution. Exposed as a property so
    /// <see cref="AndroidApp"/> can pass the same path to
    /// <c>LaunchFailureCounter.MarkStable</c> after first frame.
    /// </summary>
    public static string? LaunchCounterPath { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — pin AppPaths.DataDir to
        // the per-app sandbox files dir BEFORE any Core code resolves it,
        // and install the unhandled-exception hook. The Linux fallback in
        // AppPaths (~/.config/vpnrouter) does not map onto Android's
        // sandbox — without this override the crash reporter would either
        // fail to create its directory or write to whatever HOME happened
        // to be set, which is not user-recoverable. Both calls are
        // best-effort: a failure here must not block startup.
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

        // v2.32.0 SR-2 — bump launch-failure counter BEFORE
        // base.OnCreate (which spins up Avalonia). This way, any crash
        // inside Avalonia init / view-model construction / first-frame
        // render still leaves the counter incremented; only when
        // AndroidApp.OnFrameworkInitializationCompleted reaches the
        // post-render handler does MarkStable zero it. Mirrors desktop
        // Program.Main → MainWindow.Opened wiring.
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
            // Counter is advisory — never block app startup.
            try
            {
                global::Android.Util.Log.Warn("VpnRouter.SelfRepair",
                    $"launch-counter early init failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

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
                    // v2.32.0 (AND-DIAG) — propagate to UI before the
                    // SetIntent(false) below so the status card can show
                    // the error one-liner alongside the disconnect.
                    try { TunnelErrorReported?.Invoke(msg); }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("VpnRouter",
                            $"AND-DIAG: TunnelErrorReported handler threw: {ex}");
                    }
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

        // v2.32.0 (Android-led, 2026-05-07) — config share roundtrip.
        // Both CreateDocument (export) and OpenDocument (import) deliver
        // their result here; we route by request code.
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

        // lucid-pike (2026-05-09) — Simple-page QR scan returns from
        // MediaStore.ActionImageCapture. Decode is in-process via ZXing.Net;
        // see HandleQrCameraResult.
        if (requestCode == RequestCodeCameraQr)
        {
            HandleQrCameraResult(resultCode, data);
            return;
        }

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

    // ── v2.32.0 (Android-led, 2026-05-07) — config share intent helpers ──
    //
    // Public entry points the Avalonia UI calls when the user taps Export
    // or Import. We bridge to the Android Storage Access Framework via
    // ACTION_CREATE_DOCUMENT / ACTION_OPEN_DOCUMENT and stash the pending
    // payload (export JSON) or callback (import) on static fields so
    // OnActivityResult can complete the round-trip.
    //
    // Pre-ConfigShare: this kind of file I/O wasn't done from inside the
    // app — only auto-update sideload, which uses a content:// URI minted
    // by FileProvider. SAF gives us a free permission story (user picks
    // the location, no MANAGE_EXTERNAL_STORAGE required).

    /// <summary>JSON content waiting to be written to a CreateDocument
    /// destination. Set by <see cref="RequestExportConfig"/>; consumed in
    /// <see cref="HandleExportResult"/>; cleared on completion.</summary>
    private static string? _pendingExportContent;

    /// <summary>One-shot export-result callback. Invoked with
    /// (ok, message) — message = success summary or error string.
    /// Stashed in <see cref="RequestExportConfigShare"/> by the UI so
    /// the post-result toast hits the right overlay.</summary>
    public static Action<bool, string?>? PendingExportCallback;

    /// <summary>One-shot import-result callback. Invoked with
    /// (ok, contentOrError) — content = raw JSON bytes decoded as UTF-8
    /// when ok, error string otherwise.</summary>
    public static Action<bool, string?>? PendingImportCallback;

    /// <summary>
    /// Launch ACTION_CREATE_DOCUMENT (system "Save as" picker) so the user
    /// can choose where to drop the export JSON. <paramref name="content"/>
    /// is held until the picker resolves; the eventual write happens in
    /// <see cref="HandleExportResult"/> via ContentResolver.OpenOutputStream.
    /// </summary>
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

    /// <summary>
    /// Launch ACTION_OPEN_DOCUMENT (system "Pick file") with mime filter
    /// application/json. Result handled in
    /// <see cref="HandleImportResult"/> — reads via OpenInputStream + UTF-8.
    /// </summary>
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
            // Surface the human-readable URI so the UI can echo it.
            callback?.Invoke(true, data.Data.ToString());
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.ConfigShare",
                $"export write failed: {ex.GetType().Name}: {ex.Message}");
            callback?.Invoke(false, $"{ex.GetType().Name}: {ex.Message}");
        }
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
    }

    // ── lucid-pike (2026-05-09) — Simple-page QR scan ──────────────────────
    //
    // Flow:
    //   1. AndroidApp.OnScanQrClicked sets PendingQrScanCallback +
    //      calls RequestQrCodeScan(this).
    //   2. We check Manifest.Permission.Camera. If not granted, fire
    //      ActivityCompat.RequestPermissions and bail out — the result lands
    //      in OnRequestPermissionsResult, which either re-enters
    //      LaunchCameraForQr() or invokes the callback with "permission_denied".
    //   3. LaunchCameraForQr() mints a temp file URI under our cache dir via
    //      FileProvider (the same ${applicationId}.fileprovider authority the
    //      auto-update sideload already uses) and dispatches MediaStore.
    //      ActionImageCapture with EXTRA_OUTPUT pointed at it. EXTRA_OUTPUT
    //      gives us the full-resolution JPEG instead of the thumbnail bundle
    //      Android otherwise hands back via data.Extras["data"] — much better
    //      decode reliability for a far-away QR.
    //   4. OnActivityResult dispatches RequestCodeCameraQr to
    //      HandleQrCameraResult, which decodes the JPEG via ZXing.Net (see
    //      QrCodeDecoder.cs) and invokes the callback with the text.
    //
    // Callback contract (string second arg):
    //   - ok=true  → decoded QR text.
    //   - ok=false + "permission_denied" → user denied CAMERA at runtime.
    //   - ok=false + "cancelled" → user backed out of the camera intent.
    //   - ok=false + "not_recognized" → image had no decodable QR.
    //   - ok=false + anything else → error path (camera unavailable, decode
    //     threw, etc.). Caller logs but surfaces SmpQrNotRecognized to user.
    //
    // The temp JPEG is deleted in the finally block of HandleQrCameraResult
    // regardless of decode success, so we don't leave images in cache.

    /// <summary>
    /// One-shot QR-scan result callback. Set by AndroidApp before calling
    /// <see cref="RequestQrCodeScan"/>; cleared on result delivery so a stale
    /// callback can't fire twice if the user re-enters the flow before the
    /// first result lands.
    /// </summary>
    public static Action<bool, string?>? PendingQrScanCallback;

    /// <summary>Path of the temp JPEG handed to the camera intent via
    /// EXTRA_OUTPUT. Stashed here so HandleQrCameraResult can decode it +
    /// delete it without round-tripping through the Intent.</summary>
    private static string? _pendingQrTempFilePath;

    /// <summary>
    /// Public entry point for the Simple-page Scan-QR button. Checks the
    /// runtime camera permission state; if granted, dispatches the camera
    /// intent immediately, otherwise asks the user via
    /// ActivityCompat.RequestPermissions and re-enters the camera path on
    /// grant in <see cref="OnRequestPermissionsResult"/>.
    /// </summary>
    public void RequestQrCodeScan()
    {
        try
        {
            var granted = AndroidX.Core.Content.ContextCompat.CheckSelfPermission(
                this, global::Android.Manifest.Permission.Camera) == Permission.Granted;
            if (granted)
            {
                LaunchCameraForQr();
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

    private void LaunchCameraForQr()
    {
        try
        {
            // Use the existing ${applicationId}.fileprovider authority
            // declared in AndroidManifest.xml — no extra wiring needed.
            // Cache dir is already covered by the FileProvider's
            // <cache-path> in res/xml/file_paths.xml.
            var cacheDir = CacheDir!;
            var tempFile = new Java.IO.File(
                cacheDir,
                $"qr_scan_{DateTime.UtcNow.Ticks}.jpg");
            // Pre-create the file so FileProvider grants the camera app
            // a writable URI; on some OEMs the camera app silently fails
            // if the target file doesn't exist yet.
            try { tempFile.CreateNewFile(); } catch { /* createNewFile is best-effort */ }
            _pendingQrTempFilePath = tempFile.AbsolutePath;

            var authority = $"{PackageName}.fileprovider";
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(this, authority, tempFile);

            var intent = new Intent(global::Android.Provider.MediaStore.ActionImageCapture);
            intent.PutExtra(global::Android.Provider.MediaStore.ExtraOutput, uri);
            intent.AddFlags(ActivityFlags.GrantWriteUriPermission);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);

            // Some camera apps (notably Samsung, MIUI) don't honour
            // EXTRA_OUTPUT if the granted permissions aren't resolved
            // explicitly to every Activity that resolves the intent.
            // The standard workaround:
            try
            {
                var resInfos = PackageManager?.QueryIntentActivities(
                    intent, PackageInfoFlags.MatchDefaultOnly);
                if (resInfos is not null)
                {
                    foreach (var info in resInfos)
                    {
                        var pkg = info.ActivityInfo?.PackageName;
                        if (!string.IsNullOrEmpty(pkg))
                        {
                            GrantUriPermission(pkg, uri,
                                ActivityFlags.GrantWriteUriPermission |
                                ActivityFlags.GrantReadUriPermission);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VpnRouter.QrScan",
                    $"QueryIntentActivities/GrantUriPermission warmup failed: {ex.Message}");
            }

            StartActivityForResult(intent, RequestCodeCameraQr);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.QrScan",
                $"LaunchCameraForQr failed: {ex.GetType().Name}: {ex.Message}");
            _pendingQrTempFilePath = null;
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
            LaunchCameraForQr();
        }
        else
        {
            var cb = PendingQrScanCallback;
            PendingQrScanCallback = null;
            cb?.Invoke(false, "permission_denied");
        }
    }

    private void HandleQrCameraResult(Result resultCode, Intent? data)
    {
        var cb = PendingQrScanCallback;
        PendingQrScanCallback = null;
        var tempPath = _pendingQrTempFilePath;
        _pendingQrTempFilePath = null;

        if (resultCode != Result.Ok)
        {
            cb?.Invoke(false, "cancelled");
            TryDeleteQrTemp(tempPath);
            return;
        }

        try
        {
            global::Android.Graphics.Bitmap? bitmap = null;

            // Preferred path: full-resolution JPEG written to our temp
            // file via EXTRA_OUTPUT. We downscale to ≤ 1280×1280 before
            // ZXing decode so memory stays bounded on cheap devices.
            if (!string.IsNullOrEmpty(tempPath) && System.IO.File.Exists(tempPath))
            {
                bitmap = QrCodeDecoder.LoadDownscaledBitmap(tempPath, 1280);
            }

            // Fallback: thumbnail extra (low-res, but better than nothing
            // when the OEM camera app ignored EXTRA_OUTPUT).
            if (bitmap is null && data?.Extras is not null)
            {
                var extra = data.Extras.Get("data");
                if (extra is global::Android.Graphics.Bitmap thumb) bitmap = thumb;
            }

            if (bitmap is null)
            {
                cb?.Invoke(false, "no_image");
                return;
            }

            string? text;
            try { text = QrCodeDecoder.TryDecode(bitmap); }
            finally
            {
                try { bitmap.Recycle(); } catch { /* best effort */ }
            }

            if (string.IsNullOrEmpty(text))
            {
                cb?.Invoke(false, "not_recognized");
                return;
            }

            cb?.Invoke(true, text);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("VpnRouter.QrScan",
                $"HandleQrCameraResult decode failed: {ex.GetType().Name}: {ex.Message}");
            cb?.Invoke(false, $"decode_error:{ex.Message}");
        }
        finally
        {
            TryDeleteQrTemp(tempPath);
        }
    }

    private static void TryDeleteQrTemp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"temp QR JPEG cleanup failed at {path}: {ex.Message}");
        }
    }

    private void StartTunnelService()
    {
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

        // v2.32.0 (AND-CC, 2026-05-07) — branch on stored ConfigMode.
        // "custom" path takes a user-pasted full sing-box JSON (no URI
        // parsing, no subscription) and runs it through Inject +
        // StripUnsupportedFeatures so the same 1.13 migration logic
        // desktop uses applies on Android. "subscribe" / "manual" both
        // resolve to a single VlessServerEntry via the existing
        // AndroidStorage.GetActiveServer flow.
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
                configJson = AndroidConfigBuilder.BuildConfigJsonFromCustom(rawJson, singboxLogPath);
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

            try
            {
                if (singboxLogPath is not null)
                {
                    var configDumpPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(singboxLogPath)!,
                        "config-dump.json");
                    System.IO.File.WriteAllText(configDumpPath, configJson);
                }
            }
            catch (Exception dumpEx)
            {
                global::Android.Util.Log.Warn("VpnRouter",
                    $"AND-CC: config dump failed — {dumpEx.GetType().Name}: {dumpEx.Message}");
            }

            DispatchTunnelStart(configJson);
            return;
        }

        // ── subscribe / manual path (existing v3.0 flow) ────────────────
        //
        // v3.0 Phase 1.H (2026-05-04): resolve the active server from
        // AndroidStorage. Three sources, in priority order:
        //   1. Subscription server selected by Name (Phase 1.H)
        //   2. Manual vless:// URI (Phase 1.F)
        //   3. Hardcoded placeholder (smoke-test fallback)
        // v3.0 Phase 6.4 (2026-05-04) — debug override path via
        // <c>getExternalFilesDir()/test-uri.txt</c>. Lets me ship a
        // fixed APK and rotate the test URI via plain `adb push` — no
        // UI tapping per protocol.
        VPNRouter.Core.Models.VlessServerEntry entry;
        try
        {
            string? testUri = null;
            try
            {
                var extDir = GetExternalFilesDir(null);
                if (extDir is not null)
                {
                    var path = System.IO.Path.Combine(extDir.AbsolutePath, "test-uri.txt");
                    if (System.IO.File.Exists(path))
                    {
                        testUri = System.IO.File.ReadAllText(path).Trim();
                        global::Android.Util.Log.Info("VpnRouter",
                            $"Phase 6.4: test-uri.txt override active ({testUri.Length} chars)");
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VpnRouter",
                    $"Phase 6.4: test-uri.txt read failed — {ex.GetType().Name}: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(testUri))
            {
                entry = VPNRouter.Core.Services.ServerUriParser.Parse(testUri);
            }
            else
            {
                entry = AndroidStorage.GetActiveServer()
                    ?? VPNRouter.Core.Services.ServerUriParser.Parse(PlaceholderVlessUri);
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

        DispatchTunnelStart(configJson);
    }

    /// <summary>
    /// v2.32.0 (AND-CC, 2026-05-07) — extracted intent dispatch so the
    /// custom-config and subscription/manual paths share the same
    /// per-app filter forwarding + foreground-service launch. Pre-CC
    /// the dispatch was inlined at the bottom of StartTunnelService.
    /// </summary>
    private void DispatchTunnelStart(string configJson)
    {
        // v3.0 Phase 7.5 (2026-05-04) — per-app filter (handbook §5.5).
        // Read user's saved selection and forward to VpnRouterService.
        var perAppMode = AndroidStorage.GetPerAppMode();
        var perAppPackages = AndroidStorage.GetPerAppPackages().ToArray();
        global::Android.Util.Log.Info("VpnRouter",
            $"Phase 7.5: per-app mode={perAppMode}, packages={perAppPackages.Length}");

        // VpnRouterService is a Java class (VpnRouterService.java) — we
        // address it via fully-qualified component name rather than
        // typeof() because the .NET Android side has no C# binding for it.
        var intent = new Intent()
            .SetClassName(PackageName!, "com.ninitux.vpnrouter.VpnRouterService")
            .SetAction(ActionStart)
            .PutExtra(ExtraConfigJson, configJson)
            .PutExtra(ExtraAllowedPackages, Array.Empty<string>())
            .PutExtra(ExtraPerAppMode, perAppMode)
            .PutExtra(ExtraPerAppPackages, perAppPackages);

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

    // ── v2.32.0 SR-2 — Android-specific launch-recovery dispatch ──────────
    //
    // Mirrors VPNRouter.App/Program.cs::DispatchLaunchRecovery but with
    // tiers that make sense without a Windows/Mac/Linux installer surface:
    //
    //   "self-repair"      → wipe transient cache files (FreeConfigCache
    //                         JSON + sing-box log dump). Most chronic
    //                         crash sources are cache-driven on Android
    //                         (corrupt subscription pool, malformed
    //                         test-uri.txt, etc.) so this is the first-line
    //                         remedy. Equivalent to desktop's web reinstall
    //                         in spirit: "throw out the gunk, keep settings".
    //   "config-reset"     → ALSO clear all SharedPreferences user-data
    //                         keys (subscriptions, server cache, per-app
    //                         filter, theme, etc.). Quarantine companions
    //                         (key__corrupt_*) are preserved so a future
    //                         bug report can still inspect what was there.
    //                         Equivalent to desktop's config.yaml backup
    //                         + reset.
    //   "safe-mode-prompt" → record a persistent flag in SharedPreferences
    //                         that AndroidApp surfaces as a top-of-screen
    //                         banner: "If problems persist, go to Settings
    //                         > Apps > VPNRouter > Storage > Clear data".
    //                         Equivalent to desktop's --safe / repair.cmd
    //                         pointer — manual user action of last resort.
    //
    // Each tier is best-effort: a failure here cannot block startup.
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
                    // Re-stamp the recovery notice so AndroidApp's banner
                    // surfaces it on the next successful render.
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

    /// <summary>
    /// Best-effort cache wipe. FreeConfigs cache lives at
    /// <c>~/.config/vpnrouter/cache/free_configs.json</c> via Core's
    /// AppPaths Linux branch; we also clear the sing-box log dump and
    /// config-dump.json files we wrote under getExternalFilesDir() since
    /// those can grow unbounded across launches.
    /// </summary>
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

        // External files (log + config dump) — purely diagnostic, safe to
        // erase. The next StartTunnelService rewrites them.
        try
        {
            var ext = GetExternalFilesDir(null);
            if (ext != null)
            {
                foreach (var name in new[] { "singbox.log", "config-dump.json" })
                {
                    var path = System.IO.Path.Combine(ext.AbsolutePath, name);
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                    catch { /* per-file best effort */ }
                }
            }
        }
        catch { /* GetExternalFilesDir may fail on some OEMs */ }
    }
}
