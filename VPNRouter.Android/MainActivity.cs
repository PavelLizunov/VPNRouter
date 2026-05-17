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
    //
    // Bug-AND-023 v2 (2026-05-17): only used for the runtime permission
    // request now. The actual scan result comes back with
    // zxing-android-embedded's hard-coded REQUEST_CODE (0x0000C0DE) —
    // see RequestCodeQrScan below.
    private const int RequestCodeCameraQr = 0x4711;

    // Bug-AND-023 v2 — IntentIntegrator.REQUEST_CODE (com.journeyapps.
    // barcodescanner) is hard-coded to 0x0000C0DE / 49374. Mirroring it
    // here as a private constant so OnActivityResult can branch on it
    // without reflecting into the Java side every time a result lands.
    private const int RequestCodeQrScan = 0x0000C0DE;

    // DEFCT-005 (2026-05-10): the Phase 1.E PlaceholderVlessUri smoke-test
    // fallback was REMOVED here. Pre-fix, when AndroidStorage.GetActiveServer
    // returned null (no subscription server selected, no manual URI saved),
    // the connect path silently fell back to a hardcoded test VLESS URI
    // pointing at a dead server. The UI showed "Connected · 0:07" but every
    // VLESS handshake EOF'd; the user lost internet for the duration with
    // no error surfaced. The placeholder remains accessible only via the
    // file-based test override (`getExternalFilesDir()/test-uri.txt`) used
    // by integration testing, so smoke-test capability is preserved without
    // the silent-wrong-server failure mode.

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
    /// Bug-AND-011 (2026-05-16) — runtime check for whether this build
    /// was signed with a debug key + has Android's debuggable flag set.
    /// Used to gate diagnostic side-channels (config dump, test-uri
    /// override) that would leak credentials on a release build.
    /// Release builds always return false; debug-keystore builds return
    /// true. Reads ApplicationInfo.Flags rather than a compile-time
    /// constant so the same DLL can be reused across configurations
    /// without recompile.
    /// </summary>
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

        // Bug-AND-023 v4 (2026-05-17, user-reported "сервера подписки также
        // продублировались из страницы подписки на страницу сервер"): one-
        // shot cleanup of KeyServersJson rows that mirror subscription
        // servers (v3 SetSubscriptions duplicated them on every save).
        // Stamps a SharedPreferences flag on completion so the parse cost
        // is paid exactly once per install. Bug-AND-011-style best-effort:
        // any failure logs + skips, never blocks startup.
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

        // v2.32.3 (2026-05-17, Z:\kanareik incident follow-up) — one-shot
        // cleanup of placeholder Reality credentials (pubkey=DnT9hI...nckU
        // and its short_id / server-IP siblings) from KeyServersJson +
        // KeySubscriptions[].Servers. The fingerprints leaked from old
        // Android smoke-test code (PlaceholderVlessUri, removed DEFCT-005)
        // into user share-links and propagated through forum/Telegram
        // sample URLs. Same best-effort contract as the line above —
        // any failure logs + skips, never blocks startup.
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

        // DEFCT-001 partial (2026-05-10) — patch Avalonia 11.3.12's
        // buggy ToggleNodeInfoProvider BEFORE Avalonia spins up its
        // accessibility tree inside base.OnCreate → setContentView →
        // AvaloniaView → new AvaloniaAccessHelper(...). The patch
        // overwrites a static PropertyInfo field that PopulateNodeInfo
        // misuses; once it's in place, every IToggleProvider peer in
        // the app is safe from System.Reflection.TargetException, which
        // closes the `adb shell uiautomator dump` crash path that the
        // 2026-05-10 kebab-popup HideSubtreeFromAccessibility workaround
        // could not reach. See AvaloniaToggleNodeInfoProviderPatch.cs
        // for the full rationale.
        AvaloniaToggleNodeInfoProviderPatch.Apply();

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

        // Bug-AND-011 / Medium-3 follow-up (2026-05-16) — sweep leftover
        // QR scan JPEGs from CacheDir on every fresh launch. The v1 QR flow
        // (ACTION_IMAGE_CAPTURE, replaced in Bug-AND-023 v2) minted temp
        // files under qr_scan_*.jpg; on a crash mid-capture the file
        // persisted. v2 (zxing-android-embedded) doesn't write temp files,
        // but we keep this sweep around for users upgrading from a v1 build
        // (or future code that re-uses the same naming).
        try
        {
            var cacheDir = CacheDir;
            if (cacheDir is not null && System.IO.Directory.Exists(cacheDir.AbsolutePath))
            {
                foreach (var f in System.IO.Directory.GetFiles(cacheDir.AbsolutePath, "qr_scan_*.jpg"))
                {
                    try { System.IO.File.Delete(f); } catch { /* best-effort */ }
                }
            }
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter",
                $"Bug-AND-011/Medium-3: QR temp sweep threw: {ex.GetType().Name}: {ex.Message}");
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

        // Bug-AND-023 v2 (2026-05-17) — live-preview QR scan returns from
        // zxing-android-embedded's CaptureActivity with its hard-coded
        // REQUEST_CODE (0x0000C0DE). We decode via QrScanLauncher.parseResult
        // (Java side calls IntentIntegrator.parseActivityResult) and surface
        // the text to the C# callback.
        if (requestCode == RequestCodeQrScan)
        {
            HandleQrScanResult(resultCode, data);
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

    // ── Bug-AND-023 v2 (2026-05-17) — live-preview QR scan ────────────────
    //
    // Flow:
    //   1. AndroidApp.OnSimpleQrScanClicked / OnSubscribeQrScanClicked sets
    //      PendingQrScanCallback and calls RequestQrCodeScan(this).
    //   2. We check Manifest.Permission.Camera. If not granted, fire
    //      ActivityCompat.RequestPermissions(...). The result lands in
    //      OnRequestPermissionsResult, which either re-enters LaunchQrScanner()
    //      or invokes the callback with "permission_denied".
    //   3. LaunchQrScanner() reflects into the Java bridge
    //      (com.ninitux.vpnrouter.QrScanLauncher) which builds an
    //      IntentIntegrator and dispatches its CaptureActivity. Live preview
    //      with autodetect — no shutter button, no JPEG round-trip, no
    //      ZXing.Net decode in C# (the bundled JourneyApps library handles
    //      decode in its own preview pipeline).
    //   4. CaptureActivity finishes with REQUEST_CODE = 0x0000C0DE / 49374.
    //      OnActivityResult routes that to HandleQrScanResult, which asks
    //      the Java bridge to extract the contents via
    //      IntentIntegrator.parseActivityResult and surfaces the text.
    //
    // Callback contract (string second arg):
    //   - ok=true  → decoded QR text.
    //   - ok=false + "permission_denied" → user denied CAMERA at runtime.
    //   - ok=false + "cancelled" → user backed out of the scanner.
    //   - ok=false + "not_recognized" → no QR detected (rare with live
    //     preview — usually the user cancels long before this fires).
    //   - ok=false + anything else → error path (Java reflection failed,
    //     library missing, etc.). Caller logs but surfaces SmpQrNotRecognized.
    //
    // v1 (lucid-pike, 2026-05-09): used MediaStore.ACTION_IMAGE_CAPTURE
    // + ZXing.Net JPEG decode + FileProvider temp file. Worked but felt
    // off — the user had to frame, press shutter, and wait for decode.
    // v2 replaces it with zxing-android-embedded for the standard live-
    // preview experience; the temp-JPEG plumbing + ZXing.Net Bitmap path
    // were removed. QrCodeDecoder.cs (the C# decoder) stays for the
    // future Subscribe-page paste-image flow.

    /// <summary>
    /// One-shot QR-scan result callback. Set by AndroidApp before calling
    /// <see cref="RequestQrCodeScan"/>; cleared on result delivery so a stale
    /// callback can't fire twice if the user re-enters the flow before the
    /// first result lands.
    /// </summary>
    public static Action<bool, string?>? PendingQrScanCallback;

    /// <summary>
    /// Public entry point for the QR-scan buttons. Checks the runtime
    /// camera permission state; if granted, dispatches the IntentIntegrator
    /// scanner immediately, otherwise asks the user via
    /// ActivityCompat.RequestPermissions and re-enters in
    /// <see cref="OnRequestPermissionsResult"/>.
    /// </summary>
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

    /// <summary>
    /// Reflects into QrScanLauncher.launch(this) — the Java bridge over
    /// zxing-android-embedded's IntentIntegrator. Reflection is used (rather
    /// than a direct compile-time reference) so the aar import in csproj
    /// can stay Bind="false" and avoid forcing the Mono.Android binding
    /// generator over the full barcodescanner class graph.
    /// </summary>
    private void LaunchQrScanner()
    {
        try
        {
            var cls = Java.Lang.Class.ForName("com.ninitux.vpnrouter.QrScanLauncher");
            // public static void launch(Activity activity)
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

    /// <summary>
    /// Handles the OnActivityResult callback from zxing-android-embedded's
    /// CaptureActivity (REQUEST_CODE = 0x0000C0DE). The decoded contents
    /// come back as a Java String via QrScanLauncher.parseResult; we
    /// translate to the (ok, text) callback contract.
    /// </summary>
    private void HandleQrScanResult(Result resultCode, Intent? data)
    {
        var cb = PendingQrScanCallback;
        PendingQrScanCallback = null;

        try
        {
            var cls = Java.Lang.Class.ForName("com.ninitux.vpnrouter.QrScanLauncher");
            // public static String parseResult(int requestCode, int resultCode, Intent data)
            var intCls = Java.Lang.Integer.Type;
            var intentCls = Java.Lang.Class.ForName("android.content.Intent");
            var method = cls.GetMethod("parseResult", intCls, intCls, intentCls);
            var result = method.Invoke(null,
                Java.Lang.Integer.ValueOf(RequestCodeQrScan),
                Java.Lang.Integer.ValueOf((int)resultCode),
                data);
            var text = result?.ToString();

            // Contract from QrScanLauncher.parseResult:
            //   null → not our result (shouldn't happen since we already
            //          branched on requestCode; treat as not_recognized).
            //   ""   → user cancelled / back-pressed.
            //   else → decoded QR text.
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
        // Bug-AND-011 / Critical-1 (2026-05-16 code review): write the
        // sing-box log to the app's private sandbox FilesDir instead of
        // GetExternalFilesDir() (which is world-readable on pre-API-30
        // and trivially extractable via adb / file-manager on all API
        // levels). sing-box at level=info emits remote hostnames, UUIDs,
        // and Reality handshake metadata — production users should not
        // be leaking that to anything but their own diagnostics tap.
        // The path is still reachable for debug via `adb shell run-as`
        // on a debuggable build OR a future "Export logs" Settings flow
        // that copies via Storage Access Framework with explicit consent.
        //
        // Path: /data/data/com.ninitux.vpnrouter/files/singbox.log
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

            // Bug-AND-011 / Critical-1 (2026-05-16 code review): config-dump
            // writes the full sing-box JSON (VLESS UUID, server, Reality
            // public key + short id, SNI, custom user JSON) to disk. Gate
            // behind ApplicationInfo.Flags.Debuggable so release builds
            // never leak credentials, and only write to FilesDir (private
            // sandbox) when enabled.
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
            // Bug-AND-011 / Critical-2 (2026-05-16 code review): the
            // test-uri.txt override is a takeover surface. Any app or
            // person with shared-storage write (USB / adb / file-manager
            // / other-app-with-permission) could drop a file that
            // silently redirects all VPN traffic through an attacker
            // server. Gate behind ApplicationInfo.Flags.Debuggable AND
            // move to FilesDir so a release build cannot honour an
            // externally-planted override even if one were placed.
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
                // DEFCT-005 (2026-05-10): no silent placeholder fallback
                // here — see PlaceholderVlessUri removal above. If the user
                // has no active server (no subscription cached, no manual
                // URI), surface an explicit error so they know to add one
                // instead of "connecting" to a dead test server.
                var resolved = AndroidStorage.GetActiveServer();
                if (resolved is null)
                {
                    // Bug-AND-015 (2026-05-16) — use localized string so
                    // RU users see RU error text. Pre-fix the message
                    // was hardcoded EN regardless of current language.
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
            configJson = AndroidConfigBuilder.BuildConfigJson(entry, singboxLogPath);
            // Phase 6.2 debug — write the JSON we hand to libbox to a
            // world-readable file for offline inspection. Useful when
            // diagnosing routing issues like "TCP packets never reach
            // TUN inbound".
            // Bug-AND-011 / Critical-1 (2026-05-16 code review): config
            // dump gated behind debug-only build flag (see comment in
            // the custom-mode branch above). Release builds never write
            // the full sing-box JSON to disk.
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
