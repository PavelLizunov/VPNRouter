// VpnRouterService — Android-native service that owns the VpnService
// lifecycle and hosts the libbox.aar runtime.
//
// v2.32.0 (2026-05-07) — Network-resilience hardening (AND-NETRES):
//   • Always-on VPN compatibility: when the system starts us via
//     <intent-filter><action android:name="android.net.VpnService"/></intent-filter>
//     after device boot or user toggle in Settings → VPN, action is null
//     (or VpnService.SERVICE_INTERFACE on some OEMs). Pre-NETRES we
//     ignored these intents and stopped the service immediately. Now we
//     reload the last-known-good config from SharedPreferences and bring
//     the tunnel up without needing the Activity to launch first.
//   • Doze mode hardening: wake-lock acquired during connect-init so the
//     box service has CPU time to finish its initial dial even with the
//     screen off. Released after success / failure.
//   • Last-good-config persistence: after a successful boxService.start()
//     we copy pendingConfigJson + pendingPerAppMode + pendingPerAppPackages
//     into SharedPreferences. Always-on reads these back. Survives reboot
//     because SharedPreferences are flushed to disk by the framework.
//   • Auto-reconnect toggle: AndroidStorage's "auto_reconnect_on_network_change"
//     pref controls whether fireUpdate forwards subsequent default-interface
//     changes to libbox. ON (default) = sing-box re-binds upstream sockets
//     on Wi-Fi ↔ cellular handoff. OFF = first-bind only, sing-box keeps
//     using whatever interface it dialed on (less robust, included for
//     debugging interference between libbox's own interface monitor and
//     the platform monitor).
//   • START_STICKY: tells the framework to recreate us if the kernel kills
//     us under memory pressure. Pre-NETRES we used START_NOT_STICKY which
//     is wrong for an Always-on VPN — the system would not bring us back.
//
// v3.0 Phase 5 (2026-05-04) — REWRITE based on sagernet/sing-box-for-android
// reference (BoxService.kt + VPNService.kt + PlatformInterfaceWrapper.kt).
// Pre-5 we used commandServer.startOrReloadService() with a minimal
// PlatformInterface — most callbacks returned null/throw. That LEFT
// sing-box without a network-interface list, no system CA certificates,
// and no localDNSTransport, so its outbound sockets had nowhere to bind
// and TLS handshakes always failed. Symptom: tun0 UP, libbox claims
// "service started", but every routed packet ends in TCP-connect
// timeout.
//
// Phase 5 changes:
//   1. Direct Libbox.newService(json, platformInterface) — matches
//      reference. CommandServer is now optional / removed (we don't
//      use Clash API on Android).
//   2. Keep ParcelFileDescriptor reference, return pfd.getFd() peek
//      (NOT detachFd) so libbox can close the fd via its own
//      lifecycle without us double-closing.
//   3. Real getInterfaces() — enumerates wifi/cellular/ethernet via
//      ConnectivityManager + NetworkInterface.
//   4. Real systemCertificates() — pulls from AndroidCAStore.
//   5. useProcFS() returns true on Android < Q (sing-box uses /proc
//      for uid resolution there).
//   6. autoDetectInterfaceControl(fd) → protect(fd) — already correct
//      from Phase 3.
//
// Reference: bg/BoxService.kt, bg/VPNService.kt, bg/PlatformInterfaceWrapper.kt
// in https://github.com/PavelLizunov/vpnrouter-android.

package com.ninitux.vpnrouter;

import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.VpnService;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.os.PowerManager;
import android.system.OsConstants;
import android.util.Base64;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import java.io.File;
import java.net.Inet6Address;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.security.KeyStore;
import java.security.cert.Certificate;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Enumeration;
import java.util.Iterator;
import java.util.List;

import io.nekohasekai.libbox.BoxService;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.PlatformInterface;
import io.nekohasekai.libbox.RoutePrefix;
import io.nekohasekai.libbox.RoutePrefixIterator;
import io.nekohasekai.libbox.SetupOptions;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class VpnRouterService extends VpnService {

    public static final String ACTION_START = "com.ninitux.vpnrouter.START";
    public static final String ACTION_STOP = "com.ninitux.vpnrouter.STOP";
    public static final String EXTRA_CONFIG_JSON = "config_json";
    // v3.0 Phase 7.5 (2026-05-04) — per-app filter (handbook §5.5).
    // EXTRA_PER_APP_MODE: "off" / "include" / "exclude". When "include",
    // ONLY the EXTRA_PER_APP_PACKAGES list routes via the tunnel; when
    // "exclude", those packages BYPASS it. Pre-7.5 we shipped the
    // EXTRA_ALLOWED_PACKAGES name (kept for back-compat) but always
    // empty — TunOptions.includePackage came from libbox alone.
    public static final String EXTRA_ALLOWED_PACKAGES = "allowed_packages";
    public static final String EXTRA_PER_APP_MODE = "per_app_mode";
    public static final String EXTRA_PER_APP_PACKAGES = "per_app_packages";
    // v3.0 Phase 1.I — broadcasts so the Avalonia UI can flip its button
    // label on real tunnel-state events instead of intent-only.
    public static final String ACTION_TUNNEL_UP = "com.ninitux.vpnrouter.TUNNEL_UP";
    public static final String ACTION_TUNNEL_DOWN = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    public static final String ACTION_TUNNEL_ERROR = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    public static final String EXTRA_ERROR_MESSAGE = "error_message";

    private static final int NOTIFICATION_ID = 100;
    private static final String NOTIFICATION_CHANNEL_ID = "vpnrouter_tunnel";
    private static final String LOG_TAG = "VpnRouter";

    // v2.32.0 AND-NETRES — SharedPreferences keys read from BOTH this Java
    // service AND the C# AndroidStorage. The "vpnrouter_settings" prefs
    // file is the same one AndroidStorage uses (PrefsName const), so the
    // keys must stay in sync with AndroidStorage.cs.
    private static final String PREFS_NAME = "vpnrouter_settings";
    private static final String KEY_LAST_GOOD_CONFIG = "last_good_config_json";
    private static final String KEY_LAST_GOOD_PER_APP_MODE = "last_good_per_app_mode";
    // Stored as newline-separated package names. Java doesn't ship a JSON
    // parser in android.jar (org.json works but adds boilerplate); newline
    // is illegal inside Android package names, so it's a safe delimiter
    // and keeps the persistence path simple.
    private static final String KEY_LAST_GOOD_PER_APP_PACKAGES = "last_good_per_app_packages_lines";
    private static final String KEY_AUTO_RECONNECT = "auto_reconnect_on_network_change";

    private static boolean libboxSetupDone = false;

    private String pendingConfigJson;
    private String[] pendingAllowedPackages;
    private String pendingPerAppMode;
    private String[] pendingPerAppPackages;
    private BoxService boxService;
    private ParcelFileDescriptor currentPfd;
    // v2.32.0 AND-NETRES — wake-lock held during connect-init so the
    // box service can finish its first dial even on a screen-off / Doze
    // device. Acquired in startTunnel(), released when tunnel-up fires
    // OR the start path errors. 60-second hard cap so a stuck dial can't
    // drain battery indefinitely.
    private PowerManager.WakeLock connectWakeLock;

    @Override
    public void onCreate() {
        super.onCreate();
        // v2.32.0 (AND-CRASH-HOOK, 2026-05-08) — Java unhandled exceptions
        // in this service (libbox crashes, network-callback bugs, NPEs in
        // builder.establish()) currently get swallowed into logcat, which
        // a non-rooted user cannot read. Install a default uncaught
        // handler that writes a minimal text report to <filesDir>/crashes/
        // before chaining to the previous default — chaining is critical:
        // without it the JVM keeps the dead thread alive and the process
        // ends up in an undefined state. The C# CrashReporter on the
        // Activity side reads the same dir on next launch and the kebab
        // "View crash log" surface picks up either origin transparently.
        installJavaUncaughtHandler();
    }

    private void installJavaUncaughtHandler() {
        try {
            final Thread.UncaughtExceptionHandler previous =
                    Thread.getDefaultUncaughtExceptionHandler();
            // Anonymous inner class (not a lambda) because the javac
            // pipeline driving this project's AndroidJavaSource items
            // targets a pre-8 source level — LambdaMetafactory is not
            // resolvable on the classpath. Existing service code uses
            // the same pattern (see InterfaceUpdateListener wiring), so
            // we follow it here for consistency.
            Thread.setDefaultUncaughtExceptionHandler(new Thread.UncaughtExceptionHandler() {
                @Override
                public void uncaughtException(Thread thread, Throwable throwable) {
                    try {
                        writeJavaCrashReport(thread, throwable);
                    } catch (Throwable t) {
                        // The reporter must never throw — if writing the
                        // file failed for any reason, fall through to the
                        // original handler so the process still terminates
                        // correctly.
                        Log.w(LOG_TAG, "AND-CRASH-HOOK: writeJavaCrashReport threw: " + t.getMessage());
                    }
                    if (previous != null) {
                        previous.uncaughtException(thread, throwable);
                    }
                }
            });
            Log.i(LOG_TAG, "AND-CRASH-HOOK: Java uncaught-handler installed");
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-CRASH-HOOK: install failed: " + e.getMessage());
        }
    }

    private void writeJavaCrashReport(Thread thread, Throwable throwable) {
        try {
            File filesDir = getFilesDir();
            if (filesDir == null) return;
            File crashesDir = new File(filesDir, "crashes");
            if (!crashesDir.exists() && !crashesDir.mkdirs()) {
                // mkdirs() can return false if the dir already exists due
                // to a race; check existsAfter and bail only if truly absent.
                if (!crashesDir.exists()) return;
            }

            String stamp = new java.text.SimpleDateFormat(
                    "yyyyMMdd-HHmmss-SSS", java.util.Locale.US)
                    .format(new java.util.Date());
            File crashFile = new File(crashesDir, "java-crash-" + stamp + ".txt");

            StringBuilder sb = new StringBuilder();
            sb.append("VPNRouter Java crash report\n");
            sb.append("Source:    VpnRouterService (Java)\n");
            sb.append("Thread:    ").append(thread != null ? thread.getName() : "<null>").append('\n');
            sb.append("Time:      ").append(new java.util.Date()).append('\n');
            sb.append("Android:   ").append(Build.VERSION.RELEASE)
                    .append(" (SDK ").append(Build.VERSION.SDK_INT).append(")\n");
            sb.append("Device:    ").append(Build.MANUFACTURER).append(' ').append(Build.MODEL).append('\n');
            sb.append('\n');
            sb.append("──── Exception ────\n");
            if (throwable != null) {
                java.io.StringWriter sw = new java.io.StringWriter();
                throwable.printStackTrace(new java.io.PrintWriter(sw));
                // Same scrub patterns the C# CrashReporter applies — kept
                // minimal here to avoid depending on a Java regex library
                // beyond what's in android.jar. Covers vless://… in
                // exception messages (the most common leak vector).
                sb.append(scrubSecrets(sw.toString()));
            } else {
                sb.append("(no throwable)\n");
            }
            sb.append('\n');

            java.io.FileWriter fw = new java.io.FileWriter(crashFile, false);
            try {
                fw.write(sb.toString());
            } finally {
                try { fw.close(); } catch (Exception ignored) { }
            }
        } catch (Throwable t) {
            // Best-effort — swallow.
        }
    }

    private static String scrubSecrets(String s) {
        if (s == null || s.isEmpty()) return s;
        String out = s.replaceAll(
                "(?i)\\b(vless|vmess|trojan|ss|hysteria2?|tuic|naive)://\\S+",
                "$1://[redacted]");
        out = out.replaceAll(
                "(?i)(https?://[^\\s/?#]+)/\\S*",
                "$1/[redacted]");
        out = out.replaceAll(
                "\\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\\b",
                "<uuid>");
        out = out.replaceAll(
                "\\b[A-Za-z0-9+/_\\-]{40,}={0,2}\\b",
                "<key>");
        return out;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_START.equals(action)) {
            pendingConfigJson = intent.getStringExtra(EXTRA_CONFIG_JSON);
            pendingAllowedPackages = intent.getStringArrayExtra(EXTRA_ALLOWED_PACKAGES);
            pendingPerAppMode = intent.getStringExtra(EXTRA_PER_APP_MODE);
            pendingPerAppPackages = intent.getStringArrayExtra(EXTRA_PER_APP_PACKAGES);
            startTunnel();
        } else if (ACTION_STOP.equals(action)) {
            stopTunnel();
            stopSelf();
        } else {
            // v2.32.0 AND-NETRES — Always-on entry path.
            // The system starts us via the <intent-filter> declared in the
            // AndroidManifest (action="android.net.VpnService"). On Android
            // 7+ the system passes intent.action = VpnService.SERVICE_INTERFACE;
            // some OEMs / older Androids fire a null-action restart. Both
            // mean "user enabled Always-on for VPNRouter — bring the tunnel
            // up using whatever config last worked".
            //
            // Pre-NETRES this path fell through and the service stopped.
            // Result: Always-on flag was set in system Settings but the
            // tunnel never actually established at boot.
            Log.i(LOG_TAG, "AND-NETRES: system-initiated start (action=" + action
                    + ") — attempting last-good config restore");
            if (loadLastGoodConfig()) {
                startTunnel();
            } else {
                Log.w(LOG_TAG, "AND-NETRES: no last-good config saved; "
                        + "user must launch app and tap Connect at least once");
                stopSelf();
            }
        }
        // START_STICKY: if the kernel kills us under memory pressure, the
        // framework recreates the service with a null intent → we hit the
        // Always-on branch above and rebuild from last-good config. This
        // is the right policy for a long-running VPN service that owns a
        // foreground notification.
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void startTunnel() {
        // Bug-AND-011 / Medium-5 (2026-05-16 code review) — call the
        // 3-arg startForeground on Android 14+ (API 34) with explicit
        // FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED. The 2-arg form
        // works today via manifest declaration but is fragile under
        // future ANR enforcement; the explicit form is the documented
        // best practice for VPN services on API 34+.
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, buildNotification(),
                    android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
        } else {
            startForeground(NOTIFICATION_ID, buildNotification());
        }

        // AND-NETRES: hold a partial wake-lock during the ~5 s connect-init
        // window. Without it, on a screen-off / Doze device the kernel
        // can swap us out mid-handshake and the first dial silently
        // hangs. 60 s timeout is the fail-safe — well over the typical
        // libbox.start time (~1-2 s on this hardware).
        acquireConnectWakeLock();

        try {
            ensureLibboxSetup();
            startLibboxService();
            persistLastGoodConfig();
            sendBroadcast(new Intent(ACTION_TUNNEL_UP).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.e(LOG_TAG, "startTunnel failed: " + e.getClass().getName() + ": " + e.getMessage(), e);
            Intent err = new Intent(ACTION_TUNNEL_ERROR).setPackage(getPackageName());
            err.putExtra(EXTRA_ERROR_MESSAGE, e.getClass().getSimpleName() + ": " + e.getMessage());
            sendBroadcast(err);
            stopSelf();
        } finally {
            releaseConnectWakeLock();
        }
    }

    /**
     * v2.32.0 AND-NETRES — persist the just-started config so an Always-on
     * trigger can rebuild the tunnel without going through the Activity.
     * Called only from <code>startTunnel</code> AFTER
     * <code>boxService.start()</code> succeeded — we never overwrite a
     * known-good config with one that failed to start. Best-effort: if
     * SharedPreferences write throws, the tunnel keeps running, but the
     * next Always-on bring-up will fall back to the previous good config.
     */
    private void persistLastGoodConfig() {
        try {
            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
            SharedPreferences.Editor editor = prefs.edit();
            editor.putString(KEY_LAST_GOOD_CONFIG, pendingConfigJson);
            editor.putString(KEY_LAST_GOOD_PER_APP_MODE,
                    pendingPerAppMode != null ? pendingPerAppMode : "off");
            if (pendingPerAppPackages != null && pendingPerAppPackages.length > 0) {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < pendingPerAppPackages.length; i++) {
                    if (i > 0) sb.append('\n');
                    sb.append(pendingPerAppPackages[i] != null ? pendingPerAppPackages[i] : "");
                }
                editor.putString(KEY_LAST_GOOD_PER_APP_PACKAGES, sb.toString());
            } else {
                editor.remove(KEY_LAST_GOOD_PER_APP_PACKAGES);
            }
            // apply() is async + non-throwing — for a "best effort" path
            // that's the right choice. commit() would block the foreground
            // start path on disk I/O for ~5-50 ms.
            editor.apply();
            Log.i(LOG_TAG, "AND-NETRES: persisted last-good config ("
                    + pendingConfigJson.length() + " chars, perAppMode="
                    + pendingPerAppMode + ", packages="
                    + (pendingPerAppPackages == null ? 0 : pendingPerAppPackages.length) + ")");
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NETRES: persistLastGoodConfig threw: " + e.getMessage());
        }
    }

    /**
     * v2.32.0 AND-NETRES — load the last-known-good config from
     * SharedPreferences into the same <code>pending*</code> fields the
     * ACTION_START path uses. Returns true if a non-empty config was
     * loaded.
     */
    private boolean loadLastGoodConfig() {
        try {
            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
            String json = prefs.getString(KEY_LAST_GOOD_CONFIG, null);
            if (json == null || json.isEmpty()) return false;

            pendingConfigJson = json;
            pendingPerAppMode = prefs.getString(KEY_LAST_GOOD_PER_APP_MODE, "off");
            String packed = prefs.getString(KEY_LAST_GOOD_PER_APP_PACKAGES, null);
            if (packed == null || packed.isEmpty()) {
                pendingPerAppPackages = new String[0];
            } else {
                pendingPerAppPackages = packed.split("\n");
            }
            // Allowed-packages extra was the legacy slot; AND-NETRES restore
            // path always uses the per-app filter mode/packages exclusively.
            pendingAllowedPackages = new String[0];
            Log.i(LOG_TAG, "AND-NETRES: loaded last-good config (" + json.length()
                    + " chars, perAppMode=" + pendingPerAppMode
                    + ", packages=" + pendingPerAppPackages.length + ")");
            return true;
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NETRES: loadLastGoodConfig threw: " + e.getMessage());
            return false;
        }
    }

    /**
     * v2.32.0 AND-NETRES — acquire a partial wake-lock for the connect-init
     * window. PARTIAL_WAKE_LOCK keeps the CPU running but lets the screen
     * sleep, exactly what we want for a background VPN bring-up. The
     * 60-second timeout is a fail-safe in case <code>releaseConnectWakeLock</code>
     * is somehow skipped (e.g. JVM kill mid-startup). setReferenceCounted(false)
     * means a stray double-acquire just no-ops instead of leaking grants.
     */
    private void acquireConnectWakeLock() {
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            if (pm == null) return;
            if (connectWakeLock != null && connectWakeLock.isHeld()) return;
            connectWakeLock = pm.newWakeLock(
                    PowerManager.PARTIAL_WAKE_LOCK,
                    "VpnRouter:tunnel-init");
            connectWakeLock.setReferenceCounted(false);
            connectWakeLock.acquire(60_000L);
            Log.i(LOG_TAG, "AND-NETRES: acquired connect wake-lock (60s timeout)");
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NETRES: acquireConnectWakeLock threw: " + e.getMessage());
        }
    }

    private void releaseConnectWakeLock() {
        try {
            if (connectWakeLock != null && connectWakeLock.isHeld()) {
                connectWakeLock.release();
                Log.i(LOG_TAG, "AND-NETRES: released connect wake-lock");
            }
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NETRES: releaseConnectWakeLock threw: " + e.getMessage());
        } finally {
            connectWakeLock = null;
        }
    }

    /**
     * Initialise libbox once per process. Sets up the working / base /
     * temp paths so the Go side can write its caches and logs.
     */
    private synchronized void ensureLibboxSetup() throws Exception {
        if (libboxSetupDone) return;

        File filesDir = getFilesDir();
        File workingDir = new File(filesDir, "data");
        File cacheDir = getCacheDir();
        if (!workingDir.exists()) {
            //noinspection ResultOfMethodCallIgnored
            workingDir.mkdirs();
        }

        SetupOptions options = new SetupOptions();
        options.setBasePath(filesDir.getAbsolutePath());
        options.setWorkingPath(workingDir.getAbsolutePath());
        options.setTempPath(cacheDir.getAbsolutePath());
        options.setFixAndroidStack(false);
        Libbox.setup(options);

        // Bug-AND-011 / Critical-1 follow-up (2026-05-16 code review):
        // route Go-side stderr to the app's private sandbox FilesDir
        // instead of getExternalFilesDir(). Pre-fix the sing-box
        // stderr (which captures Go-runtime panics and Reality
        // handshake traces) was world-readable via adb / file manager
        // / USB. Now stays inside /data/data/com.ninitux.vpnrouter/files/
        // (only this app's UID can read it). For diagnostics on a
        // debug build, use `adb shell run-as com.ninitux.vpnrouter cat`.
        try {
            File stderrFile = new File(filesDir, "singbox.stderr.log");
            Libbox.redirectStderr(stderrFile.getAbsolutePath());
            Log.i(LOG_TAG, "Bug-AND-011: stderr → " + stderrFile.getAbsolutePath()
                    + " (private sandbox)");
        } catch (Exception e) {
            Log.w(LOG_TAG, "redirectStderr failed: " + e.getMessage());
        }

        libboxSetupDone = true;
        Log.i(LOG_TAG, "libbox setup OK (base=" + filesDir.getAbsolutePath() + ")");
    }

    private void startLibboxService() throws Exception {
        if (pendingConfigJson == null || pendingConfigJson.isEmpty()) {
            throw new Exception("config_json missing");
        }

        Libbox.checkConfig(pendingConfigJson);

        // v2.32.0 (2026-05-07) — libbox API migration. The 1.13.x AAR
        // dropped OverrideOptions + CommandServer.startOrReloadService;
        // service creation now goes directly through Libbox.newService.
        // CommandServer remains in libbox but is purely a Clash-API RPC
        // gateway (Connections / Groups / URLTest / Stats). VPNRouter on
        // Android drives lifecycle from the Java side via Intent
        // broadcasts and never exposes a Clash dashboard, so we drop the
        // CommandServer entirely. Reference: BoxService.kt in
        // sagernet/sing-box-for-android — minimal flow is identical.
        VpnRouterPlatformInterface platformInterface = new VpnRouterPlatformInterface(this);
        boxService = Libbox.newService(pendingConfigJson, platformInterface);
        boxService.start();

        Log.i(LOG_TAG, "libbox service started successfully (v2.32.0)");
    }

    private void stopTunnel() {
        if (boxService != null) {
            try {
                boxService.close();
            } catch (Exception e) {
                Log.w(LOG_TAG, "boxService.close threw: " + e.getMessage());
            }
            boxService = null;
        }
        if (currentPfd != null) {
            try { currentPfd.close(); } catch (Exception e) {
                Log.w(LOG_TAG, "pfd.close threw: " + e.getMessage());
            }
            currentPfd = null;
        }
        // AND-NETRES belt-and-suspenders — startTunnel's finally already
        // releases this, but if a kill landed between acquire and finally
        // (or the tunnel was running and got externally stopped) we make
        // sure the wake-lock doesn't leak.
        releaseConnectWakeLock();
        stopForeground(STOP_FOREGROUND_REMOVE);
        try {
            sendBroadcast(new Intent(ACTION_TUNNEL_DOWN).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.w(LOG_TAG, "broadcast tunnel-down threw: " + e.getMessage());
        }
    }

    @Override
    public void onRevoke() {
        stopTunnel();
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        stopTunnel();
        super.onDestroy();
    }

    private Notification buildNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    NOTIFICATION_CHANNEL_ID,
                    "VPNRouter Tunnel",
                    NotificationManager.IMPORTANCE_LOW);
            channel.setDescription("VPN tunnel running");
            channel.setShowBadge(false);
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) nm.createNotificationChannel(channel);
        }

        Intent stopIntent = new Intent(this, VpnRouterService.class).setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(
                this, 0, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        return new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .setContentTitle("VPNRouter")
                .setContentText("Tunnel active")
                .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
                .setOngoing(true)
                .addAction(android.R.drawable.ic_menu_close_clear_cancel, "Disconnect", stopPi)
                .build();
    }

    /**
     * v3.0 Phase 5 — tun fd handed to libbox. Reference impl approach:
     * keep PFD reference, return pfd.getFd() PEEK. Lifetime managed by
     * stopTunnel() which closes PFD.
     */
    int openTun(TunOptions options) throws Exception {
        Builder builder = new Builder()
                .setSession("Virtual Penguin Network")
                .setMtu(options.getMTU());

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            builder.setMetered(false);
        }

        addPrefixesAsAddresses(builder, options.getInet4Address());
        addPrefixesAsAddresses(builder, options.getInet6Address());

        boolean any4 = addPrefixesAsRoutes(builder, options.getInet4RouteAddress());
        if (!any4) builder.addRoute("0.0.0.0", 0);
        boolean any6 = addPrefixesAsRoutes(builder, options.getInet6RouteAddress());
        if (!any6) builder.addRoute("::", 0);

        boolean dnsAdded = false;
        try {
            String dns = options.getDNSServerAddress() != null
                    ? options.getDNSServerAddress().getValue() : null;
            if (dns != null && !dns.isEmpty()) {
                builder.addDnsServer(dns);
                dnsAdded = true;
            }
        } catch (Exception ignored) {}
        if (!dnsAdded) builder.addDnsServer("1.1.1.1");

        addPackages(builder, options.getIncludePackage(), true);
        addPackages(builder, options.getExcludePackage(), false);

        // v3.0 Phase 7.5 (2026-05-04) — per-app filter (handbook §5.5).
        // Apply user's package allow/disallow list from the Activity-side
        // settings. "include" → only listed packages route via tunnel.
        // "exclude" → listed packages bypass tunnel. "off" / null →
        // no filter (existing behaviour).
        //
        // Note: Android's VpnService.Builder doesn't allow mixing
        // addAllowedApplication + addDisallowedApplication on the same
        // Builder (throws IllegalArgumentException), so we pick one path.
        if ("include".equalsIgnoreCase(pendingPerAppMode) && pendingPerAppPackages != null) {
            for (String pkg : pendingPerAppPackages) {
                if (pkg == null || pkg.isEmpty()) continue;
                try {
                    builder.addAllowedApplication(pkg);
                } catch (PackageManager.NameNotFoundException ignored) {}
            }
            // Per-app include doesn't require self-disallow because if
            // we're not in the allowed list, we're already excluded.
        } else if ("exclude".equalsIgnoreCase(pendingPerAppMode) && pendingPerAppPackages != null) {
            for (String pkg : pendingPerAppPackages) {
                if (pkg == null || pkg.isEmpty()) continue;
                try {
                    builder.addDisallowedApplication(pkg);
                } catch (PackageManager.NameNotFoundException ignored) {}
            }
            // Self-disallow is still important here so VpnRouter's own
            // traffic doesn't loop through its own TUN.
            try {
                builder.addDisallowedApplication(getPackageName());
            } catch (PackageManager.NameNotFoundException ignored) {}
        } else {
            // Mode off / null — keep the original always-self-disallow
            // safety net.
            try {
                builder.addDisallowedApplication(getPackageName());
            } catch (PackageManager.NameNotFoundException ignored) {}
        }

        ParcelFileDescriptor pfd = builder.establish();
        if (pfd == null) {
            throw new Exception("VpnService.Builder.establish returned null");
        }
        currentPfd = pfd;
        // PEEK fd — do not detach. libbox uses the fd; we keep the PFD
        // reference alive for the duration of the service to prevent GC
        // from closing the fd prematurely.
        return pfd.getFd();
    }

    private static void addPrefixesAsAddresses(Builder builder, RoutePrefixIterator iter) {
        if (iter == null) return;
        while (iter.hasNext()) {
            RoutePrefix p = iter.next();
            if (p == null) continue;
            String addr = p.address();
            if (addr != null && !addr.isEmpty()) builder.addAddress(addr, p.prefix());
        }
    }

    private static boolean addPrefixesAsRoutes(Builder builder, RoutePrefixIterator iter) {
        if (iter == null) return false;
        boolean any = false;
        while (iter.hasNext()) {
            RoutePrefix p = iter.next();
            if (p == null) continue;
            String addr = p.address();
            if (addr != null && !addr.isEmpty()) {
                builder.addRoute(addr, p.prefix());
                any = true;
            }
        }
        return any;
    }

    private static void addPackages(Builder builder, StringIterator iter, boolean allow) {
        if (iter == null) return;
        while (iter.hasNext()) {
            String pkg = iter.next();
            if (pkg == null || pkg.isEmpty()) continue;
            try {
                if (allow) builder.addAllowedApplication(pkg);
                else builder.addDisallowedApplication(pkg);
            } catch (PackageManager.NameNotFoundException ignored) {}
        }
    }

    /**
     * v3.0 Phase 5 — full PlatformInterface implementation following
     * sagernet/sing-box-for-android/PlatformInterfaceWrapper.kt. Pre-5
     * most callbacks returned null; sing-box couldn't enumerate
     * interfaces (no upstream socket binding), couldn't validate TLS
     * (no system CAs), couldn't resolve DNS via system resolver. All
     * routed traffic ended in TCP-connect timeout.
     */
    private static final class VpnRouterPlatformInterface implements PlatformInterface {
        private final VpnRouterService service;

        // v3.0 Phase 6.2 — DefaultNetworkMonitor state. Holds the
        // current InterfaceUpdateListener libbox is interested in plus the
        // ConnectivityManager.NetworkCallback we registered to feed it.
        private InterfaceUpdateListener defaultListener;
        private ConnectivityManager.NetworkCallback defaultCallback;
        private final Handler mainHandler = new Handler(Looper.getMainLooper());
        // v2.32.0 AND-NETRES — first-bind tracking. When the user disables
        // the "auto-reconnect on network change" toggle, we still need to
        // fire the FIRST updateDefaultInterface so sing-box's outbound
        // sockets bind to a real interface and dialing works. Subsequent
        // changes (Wi-Fi → cellular, network-loss-and-recovery) are then
        // suppressed — sing-box keeps using its initial interface even if
        // the kernel reports a new default. ON (default) = forward all
        // updates; OFF = first update only.
        private boolean firstUpdateFired = false;

        VpnRouterPlatformInterface(VpnRouterService service) {
            this.service = service;
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            return service.openTun(options);
        }

        @Override
        public boolean useProcFS() {
            // sing-box reads /proc for uid resolution on Android < Q.
            // On Q+ it uses ConnectivityManager.getConnectionOwnerUid
            // (see findConnectionOwner). Pre-Phase-5 we returned false
            // unconditionally — that breaks /proc access on Android 9.
            return Build.VERSION.SDK_INT < Build.VERSION_CODES.Q;
        }

        @Override
        public boolean usePlatformAutoDetectInterfaceControl() {
            return true;
        }

        @Override
        public void autoDetectInterfaceControl(int fd) throws Exception {
            // Phase 3 fix carried forward — protect the fd from VPN routing
            // so libbox's upstream sockets reach the real network.
            if (!service.protect(fd)) {
                throw new Exception("VpnService.protect(" + fd + ") failed");
            }
        }

        @Override
        public void clearDNSCache() {
            // sing-box invalidating its own resolver cache — Android
            // doesn't expose a system-wide DNS cache flush from a normal
            // app, so we no-op. Reference impl does the same.
        }

        /**
         * v3.0 Phase 5 — real interface enumeration. Pre-5 we returned
         * null; libbox saw no interfaces and couldn't bind upstream
         * sockets to wlan0/cellular. Reference: PlatformInterfaceWrapper
         * .kt getInterfaces().
         */
        @SuppressLint("MissingPermission")
        @Override
        public NetworkInterfaceIterator getInterfaces() {
            try {
                ConnectivityManager cm = (ConnectivityManager)
                        service.getSystemService(CONNECTIVITY_SERVICE);
                if (cm == null) return null;

                Network[] networks = cm.getAllNetworks();
                List<NetworkInterface> sysIfaces;
                try {
                    sysIfaces = Collections.list(NetworkInterface.getNetworkInterfaces());
                } catch (Exception e) {
                    sysIfaces = new ArrayList<>();
                }

                List<io.nekohasekai.libbox.NetworkInterface> list = new ArrayList<>();
                for (Network net : networks) {
                    LinkProperties lp = cm.getLinkProperties(net);
                    NetworkCapabilities nc = cm.getNetworkCapabilities(net);
                    if (lp == null || nc == null) continue;

                    String ifName = lp.getInterfaceName();
                    if (ifName == null) continue;

                    NetworkInterface sysIface = null;
                    for (NetworkInterface si : sysIfaces) {
                        if (ifName.equals(si.getName())) { sysIface = si; break; }
                    }
                    if (sysIface == null) continue;

                    io.nekohasekai.libbox.NetworkInterface bi =
                            new io.nekohasekai.libbox.NetworkInterface();
                    bi.setName(ifName);

                    // DNS servers
                    List<String> dnsHosts = new ArrayList<>();
                    if (lp.getDnsServers() != null) {
                        for (java.net.InetAddress a : lp.getDnsServers()) {
                            String h = a.getHostAddress();
                            if (h != null) dnsHosts.add(h);
                        }
                    }
                    bi.setDNSServer(new SimpleStringIterator(dnsHosts));

                    // Type
                    int t;
                    if (nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) {
                        t = Libbox.InterfaceTypeWIFI;
                    } else if (nc.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)) {
                        t = Libbox.InterfaceTypeCellular;
                    } else if (nc.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) {
                        t = Libbox.InterfaceTypeEthernet;
                    } else {
                        t = Libbox.InterfaceTypeOther;
                    }
                    bi.setType(t);
                    bi.setIndex(sysIface.getIndex());
                    try { bi.setMTU(sysIface.getMTU()); } catch (Exception ignored) {}

                    // Addresses
                    List<String> addrs = new ArrayList<>();
                    for (InterfaceAddress ia : sysIface.getInterfaceAddresses()) {
                        java.net.InetAddress a = ia.getAddress();
                        String host = a.getHostAddress();
                        if (host == null) continue;
                        if (a instanceof Inet6Address) {
                            // Strip zone id
                            int pct = host.indexOf('%');
                            if (pct >= 0) host = host.substring(0, pct);
                        }
                        addrs.add(host + "/" + ia.getNetworkPrefixLength());
                    }
                    bi.setAddresses(new SimpleStringIterator(addrs));

                    // Flags
                    int flags = 0;
                    if (nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                        flags = OsConstants.IFF_UP | OsConstants.IFF_RUNNING;
                    }
                    try {
                        if (sysIface.isLoopback()) flags |= OsConstants.IFF_LOOPBACK;
                        if (sysIface.isPointToPoint()) flags |= OsConstants.IFF_POINTOPOINT;
                        if (sysIface.supportsMulticast()) flags |= OsConstants.IFF_MULTICAST;
                    } catch (Exception ignored) {}
                    bi.setFlags(flags);

                    bi.setMetered(!nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED));

                    list.add(bi);
                }
                return new SimpleInterfaceIterator(list);
            } catch (Exception e) {
                Log.w(LOG_TAG, "getInterfaces failed: " + e.getMessage());
                return null;
            }
        }

        /**
         * v3.0 Phase 5 — system trust anchors. Pre-5 returned null;
         * sing-box had NO CAs and every TLS handshake failed.
         */
        @Override
        public StringIterator systemCertificates() {
            try {
                List<String> certs = new ArrayList<>();
                KeyStore ks = KeyStore.getInstance("AndroidCAStore");
                ks.load(null, null);
                Enumeration<String> aliases = ks.aliases();
                while (aliases.hasMoreElements()) {
                    Certificate cert = ks.getCertificate(aliases.nextElement());
                    if (cert == null) continue;
                    String pem = "-----BEGIN CERTIFICATE-----\n"
                            + Base64.encodeToString(cert.getEncoded(), Base64.DEFAULT)
                            + "-----END CERTIFICATE-----";
                    certs.add(pem);
                }
                return new SimpleStringIterator(certs);
            } catch (Exception e) {
                Log.w(LOG_TAG, "systemCertificates failed: " + e.getMessage());
                return new SimpleStringIterator(new ArrayList<>());
            }
        }

        @Override
        public LocalDNSTransport localDNSTransport() {
            // Phase 5 simplification: return null, sing-box falls back
            // to its own DNS resolution. Reference impl provides a
            // LocalResolver bridging to Android's network DNS — that's
            // a substantial port (DnsResolver API). Phase 6 if needed.
            return null;
        }

        @Override
        public WIFIState readWIFIState() {
            return null; // optional, sing-box uses fallback
        }

        @Override
        public boolean includeAllNetworks() { return false; }

        @Override
        public boolean underNetworkExtension() { return false; }

        /**
         * v3.0 Phase 6.2 (2026-05-04) — wire ConnectivityManager.NetworkCallback
         * to libbox's InterfaceUpdateListener.
         *
         * <para>Pre-6.2 this was a no-op stub. Symptom: every upstream
         * connection from sing-box failed with "no available network
         * interface" — sing-box has the interface list (from getInterfaces),
         * but it doesn't know which one is the DEFAULT to bind upstream
         * sockets to. Without that, all outbound dialing fails.</para>
         *
         * <para>Per sagernet/sing-box-for-android DefaultNetworkListener.kt
         * + DefaultNetworkMonitor.kt, on Android P+ we cannot use
         * <code>registerDefaultNetworkCallback</code> because since DP1 it
         * returns the VPN interface itself (which would loop our own
         * traffic). Instead:
         *   - API 31+ → registerBestMatchingNetworkCallback (Android 12+)
         *   - API 28-30 → requestNetwork(NetworkRequest, callback)
         *   - API 26-27 → registerDefaultNetworkCallback
         *   - API 24-25 → registerDefaultNetworkCallback (no Handler arg)
         * </para>
         *
         * <para>When the callback fires onAvailable / onCapabilitiesChanged,
         * we resolve the interface name → kernel index via
         * NetworkInterface.getByName, then call
         * <code>listener.updateDefaultInterface(name, index, false, false)</code>.
         * The kernel can briefly report a network as available before its
         * <code>NetworkInterface</code> entry shows up — we retry up to
         * 10× with 50 ms backoff (per reference impl) before giving up.</para>
         */
        @Override
        public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
            this.defaultListener = listener;
            ConnectivityManager cm = (ConnectivityManager)
                    service.getSystemService(CONNECTIVITY_SERVICE);
            if (cm == null) {
                Log.w(LOG_TAG, "Phase 6.2: ConnectivityManager unavailable");
                return;
            }

            defaultCallback = new ConnectivityManager.NetworkCallback() {
                @Override
                public void onAvailable(Network network) {
                    Log.i(LOG_TAG, "Phase 6.2: default network onAvailable " + network);
                    fireUpdate(cm, network);
                }
                @Override
                public void onCapabilitiesChanged(Network network, NetworkCapabilities caps) {
                    fireUpdate(cm, network);
                }
                @Override
                public void onLost(Network network) {
                    Log.i(LOG_TAG, "Phase 6.2: default network onLost " + network);
                    InterfaceUpdateListener l = defaultListener;
                    if (l == null) return;
                    try { l.updateDefaultInterface("", -1, false, false); }
                    catch (Exception e) {
                        Log.w(LOG_TAG, "Phase 6.2: updateDefaultInterface(lost) threw: " + e.getMessage());
                    }
                }
            };

            try {
                if (Build.VERSION.SDK_INT >= 31) {
                    NetworkRequest request = new NetworkRequest.Builder()
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
                            .build();
                    cm.registerBestMatchingNetworkCallback(request, defaultCallback, mainHandler);
                    Log.i(LOG_TAG, "Phase 6.2: registerBestMatchingNetworkCallback (API 31+)");
                } else if (Build.VERSION.SDK_INT >= 28) {
                    NetworkRequest request = new NetworkRequest.Builder()
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .build();
                    cm.requestNetwork(request, defaultCallback, mainHandler);
                    Log.i(LOG_TAG, "Phase 6.2: requestNetwork (API 28-30)");
                } else if (Build.VERSION.SDK_INT >= 26) {
                    cm.registerDefaultNetworkCallback(defaultCallback, mainHandler);
                    Log.i(LOG_TAG, "Phase 6.2: registerDefaultNetworkCallback (API 26-27)");
                } else if (Build.VERSION.SDK_INT >= 24) {
                    cm.registerDefaultNetworkCallback(defaultCallback);
                    Log.i(LOG_TAG, "Phase 6.2: registerDefaultNetworkCallback (API 24-25)");
                } else {
                    NetworkRequest request = new NetworkRequest.Builder()
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .build();
                    cm.requestNetwork(request, defaultCallback);
                    Log.i(LOG_TAG, "Phase 6.2: requestNetwork (API 23 fallback)");
                }
            } catch (Exception e) {
                Log.e(LOG_TAG, "Phase 6.2: registering NetworkCallback failed: " + e.getMessage(), e);
            }

            // Initial fire — there's almost certainly already an active
            // network when libbox starts. Kicking it now means the very
            // first outbound dial doesn't have to wait for a callback.
            try {
                Network active = cm.getActiveNetwork();
                if (active != null) {
                    fireUpdate(cm, active);
                }
            } catch (Exception e) {
                Log.w(LOG_TAG, "Phase 6.2: initial getActiveNetwork failed: " + e.getMessage());
            }
        }

        private void fireUpdate(ConnectivityManager cm, Network network) {
            InterfaceUpdateListener l = defaultListener;
            if (l == null) return;

            // v2.32.0 AND-NETRES — auto-reconnect toggle. When OFF, fire
            // only the first update (so sing-box's initial bind works) and
            // ignore subsequent changes. ON / unset = pre-NETRES Phase 6.2
            // behavior (every change forwarded). The pref is read from
            // SharedPreferences each time rather than cached because the
            // user can toggle it from the Reliability section while the
            // tunnel is running.
            if (firstUpdateFired) {
                try {
                    SharedPreferences prefs = service.getSharedPreferences(
                            PREFS_NAME, Context.MODE_PRIVATE);
                    boolean autoReconnect = prefs.getBoolean(KEY_AUTO_RECONNECT, true);
                    if (!autoReconnect) {
                        Log.i(LOG_TAG, "AND-NETRES: auto-reconnect OFF — "
                                + "skipping default-interface update");
                        return;
                    }
                } catch (Exception ignored) {
                    // Pref read failed → fall through to forward (default-on
                    // semantics), better to over-forward than under-forward.
                }
            }

            try {
                LinkProperties lp = cm.getLinkProperties(network);
                if (lp == null) return;
                String name = lp.getInterfaceName();
                if (name == null || name.isEmpty()) return;

                int index = -1;
                for (int attempt = 0; attempt < 10 && index < 0; attempt++) {
                    try {
                        java.net.NetworkInterface ni = java.net.NetworkInterface.getByName(name);
                        if (ni != null) { index = ni.getIndex(); break; }
                    } catch (Exception e) {
                        // Kernel hasn't created the interface entry yet —
                        // back off and retry. After 10 × 50 ms we give up
                        // and pass index=-1 to libbox so it falls back to
                        // its own resolution.
                    }
                    try { Thread.sleep(50); } catch (InterruptedException ignored) {}
                }

                Log.i(LOG_TAG, "Phase 6.2: updateDefaultInterface(" + name + ", " + index + ")");
                l.updateDefaultInterface(name, index, false, false);
                firstUpdateFired = true;
            } catch (Exception e) {
                Log.w(LOG_TAG, "Phase 6.2: fireUpdate threw: " + e.getMessage());
            }
        }

        @Override
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
            if (defaultCallback != null) {
                try {
                    ConnectivityManager cm = (ConnectivityManager)
                            service.getSystemService(CONNECTIVITY_SERVICE);
                    if (cm != null) cm.unregisterNetworkCallback(defaultCallback);
                } catch (Exception e) {
                    Log.w(LOG_TAG, "Phase 6.2: unregisterNetworkCallback threw: " + e.getMessage());
                }
                defaultCallback = null;
            }
            defaultListener = null;
            // AND-NETRES — reset the first-update guard so the next
            // boxService.start() (e.g. after a stop+start cycle) re-fires
            // the initial bind unconditionally even if auto-reconnect is OFF.
            firstUpdateFired = false;
        }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) {
            String type = notification != null ? notification.getTypeName() : "null";
            String title = notification != null ? notification.getTitle() : "null";
            Log.i("Libbox", "notification: type=" + type + " title=" + title);
        }

        @Override
        public int findConnectionOwner(
                int ipProtocol,
                String sourceAddress, int sourcePort,
                String destinationAddress, int destinationPort) throws Exception {
            // v2.32.0 (2026-05-07) — libbox API drift: return type
            // changed from ConnectionOwner (a struct) to a raw int uid,
            // with -1 meaning "owner unknown / unsupported". sing-box
            // treats -1 as a fallback that disables per-uid rules for
            // the connection. We filter at the VpnService.Builder layer
            // (addAllowed/DisallowedApplication) and don't enable
            // sing-box per-uid rules in our generated config, so a
            // stub return is fine and saves the JNI round-trip into
            // ConnectivityManager.getConnectionOwnerUid that the
            // sagernet reference does on Android Q+.
            return -1;
        }

        // ── v2.32.0 (2026-05-07) libbox API drift: PlatformInterface gained
        // three new abstract methods. Stub implementations follow:
        //
        //   writeLog(String)    — replaces CommandServerHandler.writeDebugMessage,
        //                          libbox now logs through PlatformInterface
        //   packageNameByUid(int) — used for human-readable per-uid logs
        //   uidByPackageName(String) — inverse of above
        //
        // All three are best-effort log-side helpers; functional VPN does
        // not require them to return real data. We log the writeLog
        // calls so libbox-internal diagnostics still surface, and use
        // PackageManager for the uid↔package mapping when convenient.

        @Override
        public void writeLog(String message) {
            if (message != null && !message.isEmpty()) {
                Log.d("Libbox", message);
            }
        }

        @Override
        public String packageNameByUid(int uid) throws Exception {
            try {
                String[] packages = service.getPackageManager().getPackagesForUid(uid);
                if (packages != null && packages.length > 0) return packages[0];
            } catch (Exception ignore) { /* best-effort */ }
            return "uid=" + uid;
        }

        @Override
        public int uidByPackageName(String packageName) throws Exception {
            try {
                return service.getPackageManager()
                        .getApplicationInfo(packageName, 0).uid;
            } catch (Exception ignore) {
                return -1;
            }
        }
    }

    private static final class SimpleStringIterator implements StringIterator {
        private final Iterator<String> iter;
        private final int total;
        SimpleStringIterator(List<String> list) {
            this.iter = list.iterator();
            this.total = list.size();
        }
        @Override public boolean hasNext() { return iter.hasNext(); }
        @Override public String next() { return iter.next(); }
        @Override public int len() { return total; }
    }

    private static final class SimpleInterfaceIterator implements NetworkInterfaceIterator {
        private final Iterator<io.nekohasekai.libbox.NetworkInterface> iter;
        SimpleInterfaceIterator(List<io.nekohasekai.libbox.NetworkInterface> list) {
            this.iter = list.iterator();
        }
        @Override public boolean hasNext() { return iter.hasNext(); }
        @Override public io.nekohasekai.libbox.NetworkInterface next() { return iter.next(); }
    }
}
