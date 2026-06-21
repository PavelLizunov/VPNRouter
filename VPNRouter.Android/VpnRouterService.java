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
import android.app.AlarmManager;
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
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import android.os.PowerManager;
import android.os.SystemClock;
import android.system.OsConstants;
import android.util.Base64;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import java.io.File;
import java.net.Inet6Address;
import java.net.InetSocketAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.Socket;
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
    // v2.40.0 AND-NODOZE (2026-06-02) — self-restart trigger fired from
    // onTaskRemoved via AlarmManager when an aggressive OEM stopService's us
    // on swipe-away. Carries no config; falls through to the last-good-config
    // restore branch in onStartCommand (same path as the Always-on null-action
    // restart), so the tunnel rebuilds from SharedPreferences.
    public static final String ACTION_RESTART = "com.ninitux.vpnrouter.RESTART";
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
    public static final String EXTRA_NOTIF_TEXT = "notif_text";             // B7: localized FGS text
    public static final String EXTRA_NOTIF_DISCONNECT = "notif_disconnect"; // B7: localized Disconnect label

    // B7 (2026-06-21) — localized notification strings from the C# side (Localization),
    // set in onStartCommand before ensureForegroundStarted(). Defaults are the prior
    // hardcoded English so a missing extra (or an old caller) keeps current behavior.
    private String notifText = "Tunnel active";
    private String notifDisconnect = "Disconnect";
    // v3.0 Phase 1.I — broadcasts so the Avalonia UI can flip its button
    // label on real tunnel-state events instead of intent-only.
    public static final String ACTION_TUNNEL_UP = "com.ninitux.vpnrouter.TUNNEL_UP";
    public static final String ACTION_TUNNEL_DOWN = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    public static final String ACTION_TUNNEL_ERROR = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    // P1 (2026-06-21): live tunnel stats broadcast (clash_api polled via a protected socket).
    public static final String ACTION_STATS = "com.ninitux.vpnrouter.STATS";
    public static final String EXTRA_STATS_DOWN = "stats_down_total";
    public static final String EXTRA_STATS_UP = "stats_up_total";
    public static final String EXTRA_STATS_CONN = "stats_conn";
    public static final String EXTRA_ERROR_MESSAGE = "error_message";
    // DNS-tunnel (slipstream) — when the active server is dns-tunnel the
    // service brings up the in-process Slipstream client (libslipstream_jni)
    // BEFORE libbox and points sing-box's generated VLESS outbound at
    // 127.0.0.1:<port>. These carry the tunnel parameters parsed from the
    // dns-tunnel:// link; absent (null) for every other scheme, which keeps
    // the slipstream path inert. See SlipstreamNative.java.
    public static final String EXTRA_DNS_TUNNEL_DOMAIN = "dns_tunnel_domain";
    public static final String EXTRA_DNS_TUNNEL_RESOLVERS = "dns_tunnel_resolvers";
    public static final String EXTRA_DNS_TUNNEL_CERT = "dns_tunnel_cert";
    public static final String EXTRA_DNS_TUNNEL_PORT = "dns_tunnel_port";
    // When true, ignore EXTRA_DNS_TUNNEL_RESOLVERS as the primary path and use the
    // active network's OS resolver(s) (ConnectivityManager → LinkProperties →
    // getDnsServers()). The operator-agnostic WL-BYPASS path: on a strict RU mobile
    // whitelist the operator's own resolver is the only reachable DNS, so a link
    // cannot hardcode НСДИ IPs and work for every operator. The forwarded resolvers
    // stay as the fallback when no OS resolver is discoverable.
    public static final String EXTRA_DNS_TUNNEL_USE_SYSTEM_RESOLVER = "dns_tunnel_use_system_resolver";

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
    // v2.42.0 resume re-sync — authoritative live tunnel state, written in
    // lockstep with the ACTION_TUNNEL_UP/DOWN broadcasts. The C# side reads it
    // (AndroidStorage.GetTunnelLive) on MainActivity.OnResume to demote a stale
    // "Connected" status card when a broadcast was lost because no Activity
    // (hence no receiver) was alive at send time.
    private static final String KEY_TUNNEL_LIVE = "tunnel_live";
    // DNS-tunnel (slipstream) last-good params, so an Always-on / swipe-recovery
    // bring-up rebuilds the Slipstream front too (not just libbox). Resolvers
    // are newline-packed like the per-app packages slot.
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN = "last_good_dns_tunnel_domain";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS = "last_good_dns_tunnel_resolvers_lines";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_CERT = "last_good_dns_tunnel_cert";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_PORT = "last_good_dns_tunnel_port";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER = "last_good_dns_tunnel_use_system_resolver";

    private static boolean libboxSetupDone = false;
    // A10 (2026-06-15): process-lifetime cache of the system CA store as PEM
    // strings. The AndroidCAStore is effectively immutable for a process run, but
    // libbox can call systemCertificates() on every TLS-using connection — re-
    // enumerating + Base64-PEM-encoding ~150 certs each time is needless work. A CA
    // change requires Settings and is picked up on the next app launch. Held on the
    // outer service (Java forbids static fields in the non-static PlatformInterface
    // inner class); volatile for the cross-thread publish.
    private static volatile List<String> sCachedSystemCertificatePems;

    private String pendingConfigJson;
    private String[] pendingAllowedPackages;
    private String pendingPerAppMode;
    private String[] pendingPerAppPackages;
    // DNS-tunnel (slipstream) pending params — non-null only when the active
    // server arrived as dns-tunnel. startTunnel() brings up the Slipstream
    // front before libbox when pendingDnsTunnelDomain is set.
    private String pendingDnsTunnelDomain;
    private String[] pendingDnsTunnelResolvers;
    private String pendingDnsTunnelCert;
    private int pendingDnsTunnelPort;
    private boolean pendingDnsTunnelUseSystemResolver;
    // True once nativeStart spawned the Slipstream worker, so stopTunnel
    // tears it down after libbox. Reset on stop.
    // B1 (v2.42.0-r14): volatile — written on the lifecycle worker, read on the
    // main/binder thread (onTaskRemoved, the restore-branch hint).
    private volatile boolean slipstreamRunning;
    private volatile BoxService boxService;
    private volatile ParcelFileDescriptor currentPfd;
    // v2.32.0 AND-NETRES — wake-lock held during connect-init so the
    // box service can finish its first dial even on a screen-off / Doze
    // device. Acquired in startTunnel(), released when tunnel-up fires
    // OR the start path errors. 60-second hard cap so a stuck dial can't
    // drain battery indefinitely.
    private PowerManager.WakeLock connectWakeLock;

    // A3 (v2.42.0): dedicated thread for ConnectivityManager.NetworkCallback
    // delivery — created lazily on first connect, reused across reconnects, and
    // quit on service destroy. The libbox default-interface monitor's fireUpdate()
    // does a bounded NetworkInterface.getByName retry (~500ms of Thread.sleep
    // worst case) plus a blocking updateDefaultInterface() JNI call into libbox;
    // delivering those on the MAIN looper added foreground-service main-thread
    // pressure on every Wi-Fi<->cellular handoff (worst while a dns-tunnel was
    // mid-reconnect). A SERVICE-level HandlerThread (vs one per platformInterface
    // instance) avoids both a per-connect thread leak and a reuse-after-quit
    // hazard. Matches sagernet's DefaultNetworkListener design.
    private HandlerThread netCallbackThread;
    private Handler netCallbackHandler;

    // B1 (v2.42.0-r14) — the VPN lifecycle (start/stopTunnel) runs on this
    // dedicated single-thread executor, NOT the service main thread. start/stop
    // do BLOCKING native + libbox work: SlipstreamNative.nativeStart/nativeStop,
    // waitForLocalPort's ~10s join, BoxService.start()/close(). On the main thread
    // a Stop while the dns-tunnel is mid-reconnect (НСДИ resolver rate-limit →
    // QUIC 0x433 → reconnect backoff) wedged the foreground-service main thread →
    // ANR → "freeze until force-stop" (reported on two phones). onStartCommand now
    // only calls startForeground (fast, main thread per the FGS contract) and
    // enqueues the lifecycle work here. Single-thread ⇒ start/stop are serialized.
    private final java.util.concurrent.ExecutorService lifecycleExecutor = newLifecycleExecutor();

    // B4 (2026-06-15, A101BM, 21 connect/disconnect cycles): an explicit single-thread
    // pool with core-thread timeout so an idle "vpn-lifecycle" worker self-reaps after
    // 30s. Rationale: if a service instance is killed/recreated WITHOUT onDestroy
    // (START_STICKY recreate, OEM power-kill), its lifecycleExecutor never gets
    // shutdown(); a parked daemon worker keeps the orphaned executor alive (worker ->
    // executor ref => not GC'd), leaking for the process lifetime. Core-timeout is the
    // only way that orphaned worker dies. Still core=max=1 ⇒ start/stop stay strictly
    // serialized (the B1 contract); the worker just respawns on the next task.
    //   NOTE: this is NOT a per-connect leak. Device measurement: "vpn-lifecycle"
    //   oscillates 4 (idle) <-> 5 (just after a connect, reaped back to 4 within 30s),
    //   and total process threads warm up (~39 -> ~48 over the first ~6-8 connects as
    //   the libbox Go runtime grows its M-pool to steady state) then PLATEAU — flat at
    //   ~48 across cycles 7..21, RSS stable. The unnamed "Thread-N" growth is libbox
    //   gomobile JNI-attached goroutine M's (our teardownTunnelResources closes the
    //   BoxService correctly); it is bounded and RSS-neutral, so benign.
    private static java.util.concurrent.ExecutorService newLifecycleExecutor() {
        java.util.concurrent.ThreadPoolExecutor exec = new java.util.concurrent.ThreadPoolExecutor(
                1, 1, 30L, java.util.concurrent.TimeUnit.SECONDS,
                new java.util.concurrent.LinkedBlockingQueue<Runnable>(),
                new java.util.concurrent.ThreadFactory() {
                    @Override
                    public Thread newThread(Runnable r) {
                        Thread t = new Thread(r, "vpn-lifecycle");
                        t.setDaemon(true);
                        return t;
                    }
                });
        exec.allowCoreThreadTimeOut(true);
        return exec;
    }

    /** Enqueue VPN lifecycle work onto the dedicated worker (never the main
     *  thread). Swallows the post-shutdown rejection during onDestroy. */
    private void submitLifecycle(Runnable task) {
        try {
            lifecycleExecutor.execute(task);
        } catch (java.util.concurrent.RejectedExecutionException rex) {
            Log.w(LOG_TAG, "lifecycle executor rejected task (shutting down): " + rex.getMessage());
        }
    }

    /**
     * Run a potentially-blocking native teardown on a throwaway daemon thread and
     * wait up to timeoutMs. If it doesn't finish (e.g. nativeStop joining a
     * Slipstream worker that is stuck in reconnect backoff), log and return so the
     * lifecycle worker proceeds instead of wedging. The leaked thread finishes
     * later or dies with the process; the OS reclaims native resources regardless.
     */
    private void runBounded(String name, long timeoutMs, Runnable action) {
        Thread t = new Thread(action, "vpn-bounded-" + name);
        t.setDaemon(true);
        long start = android.os.SystemClock.elapsedRealtime();
        t.start();
        try {
            t.join(timeoutMs);
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
        }
        if (t.isAlive()) {
            Log.w(LOG_TAG, name + " did not finish within " + timeoutMs
                    + "ms — proceeding (stuck native teardown; leaked thread)");
        } else {
            long dur = android.os.SystemClock.elapsedRealtime() - start;
            if (dur > 250) Log.i(LOG_TAG, name + " took " + dur + "ms");
        }
    }

    /**
     * A3: lazily create + start the shared net-monitor looper thread and return
     * its Handler, so ConnectivityManager.NetworkCallback delivery (and the
     * ~500ms getByName retry + blocking updateDefaultInterface JNI inside
     * fireUpdate) runs OFF the main thread. Synchronized against
     * quitNetCallbackThread.
     */
    private synchronized Handler ensureNetCallbackHandler() {
        if (netCallbackHandler == null) {
            netCallbackThread = new HandlerThread("vpn-net-monitor");
            netCallbackThread.start();
            netCallbackHandler = new Handler(netCallbackThread.getLooper());
        }
        return netCallbackHandler;
    }

    /**
     * A3: stop the shared net-monitor looper thread. Null-safe (may never have
     * been created if the service stopped before any connect). quitSafely drains
     * already-queued interface updates first. Called on the lifecycle worker from
     * onDestroy, AFTER teardown has unregistered the NetworkCallback.
     */
    private synchronized void quitNetCallbackThread() {
        if (netCallbackThread != null) {
            try {
                netCallbackThread.quitSafely();
            } catch (Exception e) {
                Log.w(LOG_TAG, "A3: netCallbackThread.quitSafely threw: " + e.getMessage());
            }
            netCallbackThread = null;
            netCallbackHandler = null;
        }
    }

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
            // B1: capture extras on the (fast) main thread; run the BLOCKING
            // lifecycle on the worker. startForeground MUST be prompt on the main
            // thread (FGS contract) — do it here, not inside startTunnel (which now
            // runs worker-side and could be briefly queued behind a prior op).
            // B7: capture localized notification strings BEFORE the foreground start
            // (ensureForegroundStarted -> buildNotification) so the first FGS notification
            // is localized. Empty/missing extras leave the English defaults.
            String nText = intent.getStringExtra(EXTRA_NOTIF_TEXT);
            if (nText != null && !nText.isEmpty()) notifText = nText;
            String nDisc = intent.getStringExtra(EXTRA_NOTIF_DISCONNECT);
            if (nDisc != null && !nDisc.isEmpty()) notifDisconnect = nDisc;
            if (!ensureForegroundStarted()) {
                return START_STICKY; // background-FGS-start refused — already broadcast + stopSelf
            }
            final String cfg = intent.getStringExtra(EXTRA_CONFIG_JSON);
            final String[] allowed = intent.getStringArrayExtra(EXTRA_ALLOWED_PACKAGES);
            final String perAppMode = intent.getStringExtra(EXTRA_PER_APP_MODE);
            final String[] perAppPkgs = intent.getStringArrayExtra(EXTRA_PER_APP_PACKAGES);
            final String dnsDomain = intent.getStringExtra(EXTRA_DNS_TUNNEL_DOMAIN);
            final String[] dnsResolvers = intent.getStringArrayExtra(EXTRA_DNS_TUNNEL_RESOLVERS);
            final String dnsCert = intent.getStringExtra(EXTRA_DNS_TUNNEL_CERT);
            final int dnsPort = intent.getIntExtra(EXTRA_DNS_TUNNEL_PORT, 7001);
            final boolean dnsUseSystem = intent.getBooleanExtra(EXTRA_DNS_TUNNEL_USE_SYSTEM_RESOLVER, false);
            submitLifecycle(new Runnable() {
                @Override
                public void run() {
                    pendingConfigJson = cfg;
                    pendingAllowedPackages = allowed;
                    pendingPerAppMode = perAppMode;
                    pendingPerAppPackages = perAppPkgs;
                    pendingDnsTunnelDomain = dnsDomain;
                    pendingDnsTunnelResolvers = dnsResolvers;
                    pendingDnsTunnelCert = dnsCert;
                    pendingDnsTunnelPort = dnsPort;
                    pendingDnsTunnelUseSystemResolver = dnsUseSystem;
                    startTunnel();
                }
            });
        } else if (ACTION_STOP.equals(action)) {
            // v2.40.0-r8 (#3): an explicit user Disconnect must defuse any pending
            // onTaskRemoved swipe-recovery alarm, else a swipe-then-Disconnect
            // within the ~1.5s window silently re-establishes the tunnel.
            cancelScheduledRestart();
            submitLifecycle(new Runnable() {
                @Override
                public void run() {
                    stopTunnel();
                    stopSelf();
                }
            });
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
            // v2.40.0 AND-NODOZE — guard against a redundant restart. The
            // onTaskRemoved swipe-recovery schedules an ACTION_RESTART
            // unconditionally (it can't know whether the OEM will actually
            // stopService us on swipe). If the foreground service in fact
            // survived, boxService is still live — re-running startTunnel here
            // would orphan the old BoxService + ParcelFileDescriptor and cause
            // a spurious ~2s tunnel re-establish on every swipe-away. Only
            // restore in a genuinely fresh/killed process (boxService == null).
            if (!ensureForegroundStarted()) {
                return START_STICKY; // background-FGS-start refused — already broadcast + stopSelf
            }
            submitLifecycle(new Runnable() {
                @Override
                public void run() {
                    if (boxService != null) {
                        Log.i(LOG_TAG, "AND-NODOZE: restart/always-on intent but tunnel "
                                + "already running — no-op (service survived the swipe)");
                    } else if (loadLastGoodConfig()) {
                        startTunnel();
                    } else {
                        Log.w(LOG_TAG, "AND-NETRES: no last-good config saved; "
                                + "user must launch app and tap Connect at least once");
                        stopSelf();
                    }
                }
            });
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

    /**
     * Call startForeground promptly on the MAIN thread — the FGS contract requires
     * it within seconds of a startForegroundService. Returns false if a
     * background-FGS-start was refused (already broadcast + stopSelf).
     *
     * <p>Bug-AND-011 / Medium-5 (2026-05-16): 3-arg startForeground on API 34+ with
     * explicit FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED. v2.40.0 AND-NODOZE
     * (2026-06-02): on Android 12+ a background-initiated FGS start can be refused
     * with ForegroundServiceStartNotAllowedException — broadcast an error and stop
     * cleanly instead of reaching the AND-CRASH-HOOK uncaught handler. v2.42.0-r14
     * (B1): split out of startTunnel so the prompt foreground call stays on the
     * main thread while the blocking lifecycle moves to the worker executor.</p>
     */
    private boolean ensureForegroundStarted() {
        try {
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(NOTIFICATION_ID, buildNotification(),
                        android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            } else {
                startForeground(NOTIFICATION_ID, buildNotification());
            }
            return true;
        } catch (Exception e) {
            Log.e(LOG_TAG, "AND-NODOZE: startForeground refused ("
                    + e.getClass().getSimpleName() + ": " + e.getMessage()
                    + ") — likely a background-FGS-start restriction. Stopping cleanly.");
            try {
                Intent err = new Intent(ACTION_TUNNEL_ERROR).setPackage(getPackageName());
                err.putExtra(EXTRA_ERROR_MESSAGE, "foreground-start-blocked");
                sendBroadcast(err);
            } catch (Exception ignored) { }
            setTunnelLive(false);
            stopSelf();
            return false;
        }
    }

    /**
     * v2.42.0 resume re-sync — persist the authoritative live tunnel state to
     * the shared prefs in lockstep with the TUNNEL_UP/DOWN broadcasts. The C#
     * MainActivity.OnResume reads this (AndroidStorage.GetTunnelLive) to demote
     * a stale "Connected" status card when a broadcast was lost because no
     * Activity (hence no receiver) was alive at send time. Best-effort: a prefs
     * failure just means the next resume can't self-heal — it never blocks the
     * tunnel. apply() is async + non-blocking, fine for the worker thread.
     */
    private void setTunnelLive(boolean live) {
        try {
            getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
                    .edit()
                    .putBoolean(KEY_TUNNEL_LIVE, live)
                    .apply();
        } catch (Exception e) {
            Log.w(LOG_TAG, "setTunnelLive(" + live + ") threw: " + e.getMessage());
        }
    }

    /** Bring the tunnel up. Runs on the lifecycle worker (lifecycleExecutor);
     *  startForeground was already invoked on the main thread in onStartCommand. */
    private void startTunnel() {
        // A2 (B3): a re-Start while a tunnel is already live must not orphan the old
        // BoxService/TUN pfd/Slipstream (the old code overwrote boxService without
        // closing it on the second start). Tear down the previous resources first —
        // WITHOUT dropping the foreground notification (we stay foreground across the
        // restart) or broadcasting TUNNEL_DOWN (we are reconfiguring, not stopping).
        if (boxService != null) {
            Log.i(LOG_TAG, "startTunnel: tunnel already live — tearing down previous before re-start");
            teardownTunnelResources();
        }
        // AND-NETRES: hold a partial wake-lock during the ~5 s connect-init
        // window. Without it, on a screen-off / Doze device the kernel
        // can swap us out mid-handshake and the first dial silently
        // hangs. 60 s timeout is the fail-safe — well over the typical
        // libbox.start time (~1-2 s on this hardware).
        acquireConnectWakeLock();

        try {
            ensureLibboxSetup();
            startSlipstreamIfNeeded();
            startLibboxService();
            persistLastGoodConfig();
            sendBroadcast(new Intent(ACTION_TUNNEL_UP).setPackage(getPackageName()));
            setTunnelLive(true);
            startStatsPoller();   // P1: begin polling clash_api for live up/down + conn count
        } catch (Exception e) {
            Log.e(LOG_TAG, "startTunnel failed: " + e.getClass().getName() + ": " + e.getMessage(), e);
            Intent err = new Intent(ACTION_TUNNEL_ERROR).setPackage(getPackageName());
            err.putExtra(EXTRA_ERROR_MESSAGE, e.getClass().getSimpleName() + ": " + e.getMessage());
            sendBroadcast(err);
            setTunnelLive(false);
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
            if (pendingDnsTunnelDomain != null && !pendingDnsTunnelDomain.isEmpty()) {
                editor.putString(KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN, pendingDnsTunnelDomain);
                editor.putString(KEY_LAST_GOOD_DNS_TUNNEL_CERT,
                        pendingDnsTunnelCert != null ? pendingDnsTunnelCert : "");
                editor.putInt(KEY_LAST_GOOD_DNS_TUNNEL_PORT,
                        pendingDnsTunnelPort > 0 ? pendingDnsTunnelPort : 7001);
                editor.putBoolean(KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER,
                        pendingDnsTunnelUseSystemResolver);
                if (pendingDnsTunnelResolvers != null && pendingDnsTunnelResolvers.length > 0) {
                    StringBuilder rb = new StringBuilder();
                    for (int i = 0; i < pendingDnsTunnelResolvers.length; i++) {
                        if (i > 0) rb.append('\n');
                        rb.append(pendingDnsTunnelResolvers[i] != null ? pendingDnsTunnelResolvers[i] : "");
                    }
                    editor.putString(KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS, rb.toString());
                } else {
                    editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS);
                }
            } else {
                editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN);
                editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS);
                editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_CERT);
                editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_PORT);
                editor.remove(KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER);
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
            // DNS-tunnel restore — so an Always-on / swipe-recovery bring-up
            // re-establishes the Slipstream front, not just libbox. Empty
            // domain ⇒ a non-dns-tunnel last-good config (slipstream stays inert).
            pendingDnsTunnelDomain = prefs.getString(KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN, null);
            if (pendingDnsTunnelDomain != null && pendingDnsTunnelDomain.isEmpty()) {
                pendingDnsTunnelDomain = null;
            }
            pendingDnsTunnelCert = prefs.getString(KEY_LAST_GOOD_DNS_TUNNEL_CERT, null);
            pendingDnsTunnelPort = prefs.getInt(KEY_LAST_GOOD_DNS_TUNNEL_PORT, 7001);
            pendingDnsTunnelUseSystemResolver =
                    prefs.getBoolean(KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER, false);
            String packedResolvers = prefs.getString(KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS, null);
            pendingDnsTunnelResolvers = (packedResolvers == null || packedResolvers.isEmpty())
                    ? new String[0] : packedResolvers.split("\n");
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

    /**
     * DNS-tunnel (slipstream): when the active server arrived with dns-tunnel
     * parameters, bring up the in-process Slipstream client BEFORE libbox and
     * wait for its local TCP front to listen. sing-box's generated VLESS
     * outbound dials 127.0.0.1:&lt;port&gt;, so starting libbox before the front
     * is listening would dial a dead local socket — we fail CLOSED here (throw
     * → startTunnel's catch broadcasts the error + stops) rather than leaving a
     * "Connected" UI over a tunnel that can't carry traffic.
     *
     * <p>No-op for every non-dns-tunnel server (pendingDnsTunnelDomain null).
     * The Slipstream resolver UDP:53 traffic bypasses the TUN automatically
     * because the service self-disallows the VPNRouter UID in openTun(), so no
     * extra loop-avoidance routing is needed.</p>
     */
    private void startSlipstreamIfNeeded() throws Exception {
        if (pendingDnsTunnelDomain == null || pendingDnsTunnelDomain.isEmpty()) {
            return;
        }
        int port = pendingDnsTunnelPort > 0 ? pendingDnsTunnelPort : 7001;
        if (!SlipstreamNative.isAvailable()) {
            throw new Exception("dns-tunnel: native Slipstream library not available for this device ABI");
        }
        String[] resolvers = pendingDnsTunnelResolvers != null
                ? pendingDnsTunnelResolvers : new String[0];
        // System-resolver mode (link sentinel "system"): use the active network's
        // OS resolver(s) — the operator-agnostic WL-BYPASS path. On a strict RU
        // mobile whitelist the operator's own resolver (e.g. 10.x) is the only
        // reachable DNS, so a link cannot hardcode НСДИ IPs. The forwarded literals
        // stay as the fallback when no OS resolver is discoverable.
        if (pendingDnsTunnelUseSystemResolver) {
            String[] sys = readSystemResolvers();
            if (sys.length > 0) {
                Log.i(LOG_TAG, "dns-tunnel: system-resolver mode — using " + sys.length
                        + " OS resolver(s) over " + resolvers.length + " link resolver(s)");
                resolvers = sys;
            } else {
                Log.w(LOG_TAG, "dns-tunnel: system-resolver mode but no OS resolver discovered — "
                        + "falling back to " + resolvers.length + " link resolver(s)");
            }
        }
        // The Slipstream client's ClientConfig.cert is a FILE PATH (it does
        // fs::read on it for the leaf-cert pin) — NOT the PEM text. Desktop's
        // SlipstreamManager writes the PEM to disk and passes the path; mirror
        // that here. Passing the raw PEM made run_client fail
        // "Failed to read cert <PEM>: No such file or directory".
        String certPath = "";
        if (pendingDnsTunnelCert != null && !pendingDnsTunnelCert.isEmpty()) {
            java.io.File certFile = new java.io.File(getFilesDir(), "slipstream-cert.pem");
            try (java.io.FileOutputStream fos = new java.io.FileOutputStream(certFile)) {
                fos.write(pendingDnsTunnelCert.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            }
            certPath = certFile.getAbsolutePath();
            Log.i(LOG_TAG, "dns-tunnel: wrote leaf cert to " + certPath);
        }
        Log.i(LOG_TAG, "dns-tunnel: starting Slipstream front on 127.0.0.1:" + port
                + " (domain=" + pendingDnsTunnelDomain + ", resolvers=" + resolvers.length + ")");
        boolean spawned = SlipstreamNative.nativeStart(
                certPath,
                pendingDnsTunnelDomain,
                port,
                resolvers);
        if (!spawned) {
            throw new Exception("dns-tunnel: Slipstream nativeStart returned false");
        }
        slipstreamRunning = true;
        // The local TCP listener binds almost immediately on spawn (before the
        // QUIC-over-DNS handshake even completes), so this normally returns in
        // well under a second. The 8 s cap is the fail-closed worst case and
        // stays comfortably inside the foreground-service onStartCommand ANR
        // window — the connect wake-lock is held for the whole window.
        if (!waitForLocalPort(port, 8_000L)) {
            throw new Exception("dns-tunnel: Slipstream front did not start listening on 127.0.0.1:" + port);
        }
        Log.i(LOG_TAG, "dns-tunnel: Slipstream front is listening on 127.0.0.1:" + port);
    }

    /**
     * Discover the active network's OS resolver(s) as "ip:53" strings via
     * ConnectivityManager — the operator-agnostic WL-BYPASS path. Prefers the
     * active default network, falling through to other networks only if it yields
     * none. IPv4 only (the covert path uses ip:53); loopback / link-local skipped;
     * deduped. Best-effort: returns an empty array on any failure so the caller
     * falls back to the link's forwarded resolvers. The underlying network is up
     * here (slipstream starts before the TUN), so on a strict mobile whitelist this
     * returns the operator resolver (e.g. 10.x) — the only DNS reachable there.
     */
    private String[] readSystemResolvers() {
        java.util.List<String> out = new java.util.ArrayList<>();
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            if (cm == null) return new String[0];
            java.util.List<Network> nets = new java.util.ArrayList<>();
            Network active = cm.getActiveNetwork();
            if (active != null) nets.add(active);
            for (Network n : cm.getAllNetworks()) {
                if (!nets.contains(n)) nets.add(n); // active first, then the rest as fallback
            }
            for (Network net : nets) {
                LinkProperties lp = cm.getLinkProperties(net);
                if (lp == null || lp.getDnsServers() == null) continue;
                for (java.net.InetAddress a : lp.getDnsServers()) {
                    if (!(a instanceof java.net.Inet4Address)) continue; // IPv4 covert path
                    if (a.isLoopbackAddress() || a.isLinkLocalAddress()) continue;
                    String h = a.getHostAddress();
                    if (h == null) continue;
                    String ep = h + ":53";
                    if (!out.contains(ep)) out.add(ep);
                }
                if (!out.isEmpty()) break; // active network's resolvers suffice
            }
        } catch (Exception e) {
            Log.w(LOG_TAG, "dns-tunnel: readSystemResolvers threw: " + e.getMessage());
        }
        return out.toArray(new String[0]);
    }

    /**
     * Poll 127.0.0.1:port for a connectable listener up to timeoutMs. The TCP
     * connect MUST run off the main thread — startTunnel runs on the service's
     * main thread, where a blocking socket connect throws
     * NetworkOnMainThreadException (StrictMode). Doing it inline made every
     * probe "fail" even though the Slipstream front was listening the whole
     * time, so the front was always torn down by the fail-closed timeout.
     */
    private boolean waitForLocalPort(int port, long timeoutMs) {
        final java.util.concurrent.atomic.AtomicBoolean ok =
                new java.util.concurrent.atomic.AtomicBoolean(false);
        Thread probe = new Thread(() -> {
            long deadline = android.os.SystemClock.elapsedRealtime() + timeoutMs;
            while (android.os.SystemClock.elapsedRealtime() < deadline) {
                java.net.Socket s = new java.net.Socket();
                try {
                    s.connect(new java.net.InetSocketAddress("127.0.0.1", port), 500);
                    ok.set(true);
                    return;
                } catch (Exception ignored) {
                    // front not listening yet
                } finally {
                    try { s.close(); } catch (Exception ignored) { }
                }
                try {
                    Thread.sleep(200);
                } catch (InterruptedException ie) {
                    Thread.currentThread().interrupt();
                    return;
                }
            }
        }, "slipstream-portcheck");
        probe.start();
        try {
            probe.join(timeoutMs + 2000);
        } catch (InterruptedException ie) {
            Thread.currentThread().interrupt();
        }
        return ok.get();
    }

    /** Tear down the Slipstream client if it was started. Idempotent. */
    private void stopSlipstreamIfRunning() {
        if (!slipstreamRunning) return;
        slipstreamRunning = false;
        // B1: bound nativeStop — if the Slipstream worker is stuck in QUIC reconnect
        // backoff (the НСДИ rate-limit-drop case), the join inside nativeStop can
        // hang. Cap it so the lifecycle worker proceeds and the UI never wedges.
        runBounded("nativeStop", 4_000L, new Runnable() {
            @Override
            public void run() {
                try {
                    SlipstreamNative.nativeStop();
                    Log.i(LOG_TAG, "dns-tunnel: Slipstream front stopped");
                } catch (Throwable t) {
                    Log.w(LOG_TAG, "dns-tunnel: nativeStop threw: " + t.getMessage());
                }
            }
        });
    }

    /**
     * Close the tunnel's native resources (BoxService, Slipstream front, TUN pfd) and
     * release the connect wake-lock — WITHOUT touching the foreground notification or
     * broadcasting TUNNEL_DOWN. Used by {@link #stopTunnel} (full stop) and by
     * {@link #startTunnel}'s stop-old-before-start (A2: a re-Start must not orphan the
     * previous BoxService/pfd/Slipstream). Bounded so a stuck native teardown can't
     * wedge the lifecycle worker.
     */
    // ── P1 (2026-06-21): live tunnel stats ───────────────────────────────────
    // Poll clash_api /connections via a VpnService-PROTECTED socket so the request
    // bypasses our OWN tun. (The app's unprotected loopback to 127.0.0.1:9090 fails
    // with "Connection failure" under a full tunnel — root-caused on device 2026-06-21;
    // adb's shell uid bypasses the VPN, which is why external curl worked but the
    // in-process managed HttpClient didn't.) Parse downloadTotal/uploadTotal + the
    // connection count, broadcast to the C# UI. Every 2s while live; fully best-effort.
    private java.util.concurrent.ScheduledExecutorService statsPoller;

    private synchronized void startStatsPoller() {
        stopStatsPoller();
        java.util.concurrent.ScheduledExecutorService ex =
            java.util.concurrent.Executors.newSingleThreadScheduledExecutor(new java.util.concurrent.ThreadFactory() {
                @Override public Thread newThread(Runnable r) {
                    Thread t = new Thread(r, "vpnrouter-stats");
                    t.setDaemon(true);
                    return t;
                }
            });
        statsPoller = ex;
        ex.scheduleWithFixedDelay(new Runnable() {
            @Override public void run() { pollStatsOnce(); }
        }, 1500L, 2000L, java.util.concurrent.TimeUnit.MILLISECONDS);
    }

    private synchronized void stopStatsPoller() {
        if (statsPoller != null) {
            try { statsPoller.shutdownNow(); } catch (Exception ignore) { }
            statsPoller = null;
        }
    }

    private void pollStatsOnce() {
        Socket sock = null;
        try {
            sock = new Socket();
            // bypass our own tun — the whole point of P1. protect() can return
            // false (transient binder / service not yet a VpnService owner); the
            // unprotected connect to loopback then routes into our tun and fails
            // (caught below). Log so a persistently-failing poller is diagnosable
            // rather than silently never reporting stats.
            if (!protect(sock))
                Log.w(LOG_TAG, "pollStatsOnce: protect(socket) returned false — stats may not flow this tick");
            sock.connect(new InetSocketAddress("127.0.0.1", 9090), 2000);
            sock.setSoTimeout(2000);
            java.io.OutputStream os = sock.getOutputStream();
            // HTTP/1.0 + close-delimited body so we never have to de-chunk.
            os.write("GET /connections HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n".getBytes("UTF-8"));
            os.flush();
            java.io.InputStream is = sock.getInputStream();
            java.io.ByteArrayOutputStream buf = new java.io.ByteArrayOutputStream();
            byte[] tmp = new byte[4096];
            int n;
            while ((n = is.read(tmp)) > 0) buf.write(tmp, 0, n);
            String resp = new String(buf.toByteArray(), "UTF-8");
            int bodyIdx = resp.indexOf("\r\n\r\n");
            if (bodyIdx < 0) return;
            String body = resp.substring(bodyIdx + 4);
            int brace = body.indexOf('{');
            int end = body.lastIndexOf('}');
            if (brace < 0 || end <= brace) return;
            org.json.JSONObject obj = new org.json.JSONObject(body.substring(brace, end + 1));
            long down = obj.optLong("downloadTotal", 0L);
            long up = obj.optLong("uploadTotal", 0L);
            // Isolate the connections-array read: if a libbox Clash-API version
            // ships 'connections' as a non-array, getJSONArray throws and would
            // otherwise discard the good down/up totals for this tick too.
            int conn = 0;
            try {
                if (obj.has("connections") && !obj.isNull("connections"))
                    conn = obj.getJSONArray("connections").length();
            } catch (org.json.JSONException ignore) {
                // schema drift — keep the down/up totals we already parsed.
            }
            Intent it = new Intent(ACTION_STATS).setPackage(getPackageName());
            it.putExtra(EXTRA_STATS_DOWN, down);
            it.putExtra(EXTRA_STATS_UP, up);
            it.putExtra(EXTRA_STATS_CONN, conn);
            sendBroadcast(it);
        } catch (Exception e) {
            // best-effort — clash_api not up yet / transient; retry next tick.
        } finally {
            if (sock != null) { try { sock.close(); } catch (Exception ignore) { } }
        }
    }

    private boolean teardownTunnelResources() {
        stopStatsPoller();   // P1: stop the stats poll on every teardown
        final BoxService bs = boxService;
        boxService = null;
        // LOW (double-broadcast guard): record whether anything was actually live
        // BEFORE tearing it down, so stopTunnel can drop the foreground
        // notification + broadcast TUNNEL_DOWN at most once. An explicit Stop
        // enqueues stopTunnel() AND triggers onDestroy() (which enqueues
        // stopTunnel() again on the same serial worker); the second pass finds
        // nothing live and must stay silent. Captured before stopSlipstreamIfRunning
        // clears slipstreamRunning and before currentPfd is nulled below.
        final boolean wasLive = bs != null || slipstreamRunning || currentPfd != null;
        if (bs != null) {
            // B1: bound boxService.close — libbox shutdown can block if sing-box is
            // tearing down while dialing a dead 127.0.0.1 dns-tunnel front.
            runBounded("boxService.close", 4_000L, new Runnable() {
                @Override
                public void run() {
                    try {
                        bs.close();
                    } catch (Exception e) {
                        Log.w(LOG_TAG, "boxService.close threw: " + e.getMessage());
                    }
                }
            });
        }
        // DNS-tunnel: tear down the Slipstream front AFTER libbox so sing-box
        // never dials a dead 127.0.0.1 front during its own shutdown.
        stopSlipstreamIfRunning();
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
        return wasLive;
    }

    private void stopTunnel() {
        // LOW (double-broadcast guard): an explicit Stop runs stopTunnel() once
        // here and again from onDestroy() (stopSelf -> onDestroy enqueues another
        // stopTunnel on the same serial lifecycle worker). teardownTunnelResources
        // reports whether anything was actually live; only the pass that tore
        // something down drops the foreground notification + broadcasts
        // TUNNEL_DOWN, so the UI sees the down-event exactly once.
        if (!teardownTunnelResources()) {
            return;
        }
        stopForeground(STOP_FOREGROUND_REMOVE);
        try {
            sendBroadcast(new Intent(ACTION_TUNNEL_DOWN).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.w(LOG_TAG, "broadcast tunnel-down threw: " + e.getMessage());
        }
        // v2.42.0 resume re-sync — record the down so a resume that missed the
        // (possibly lost) TUNNEL_DOWN broadcast can demote a stale card.
        setTunnelLive(false);
    }

    @Override
    public void onRevoke() {
        Log.i(LOG_TAG, "onRevoke: VPN revoked by system/user — tearing down tunnel");
        // Explicit "stop using VPN" (permission revoked / another VPN took over):
        // defuse any pending swipe-recovery restart so we don't resurrect it.
        cancelScheduledRestart();
        // B1: onRevoke is delivered on a binder thread — never run the blocking
        // teardown inline; enqueue it on the lifecycle worker.
        submitLifecycle(new Runnable() {
            @Override
            public void run() { stopTunnel(); }
        });
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        // B1: enqueue a final teardown, then stop accepting new lifecycle work.
        // shutdown() is non-blocking — it lets the queued stop drain on the daemon
        // worker without blocking onDestroy (main thread). On process death the OS
        // reclaims anything a bounded teardown didn't finish.
        submitLifecycle(new Runnable() {
            @Override
            public void run() {
                stopTunnel();
                // A3: quit the shared net-monitor thread AFTER teardown so
                // boxService.close() has already unregistered its NetworkCallback.
                quitNetCallbackThread();
            }
        });
        lifecycleExecutor.shutdown();
        super.onDestroy();
    }

    /**
     * v2.40.0 AND-NODOZE (2026-06-02) — swipe-away recovery. Aggressive OEMs
     * (KYOCERA/BALMUDA, Xiaomi, Huawei, ...) call stopService when the user
     * swipes the app from Recents — even for a foreground service. START_STICKY
     * only covers a system memory-pressure kill, NOT an explicit stopService,
     * so without this the tunnel silently dies on swipe-away with no recovery.
     *
     * <p>If the tunnel is active AND we're battery-opt exempt (so a background
     * foreground-service start is permitted on Android 12+), schedule a
     * near-immediate self-restart that rebuilds from last-good config via the
     * ACTION_RESTART → restore branch in onStartCommand. When NOT exempt we
     * can't legally restart from the background — log it and rely on the
     * proactive battery-opt prompt (AndroidApp.Permissions) to unlock reliable
     * recovery next time. The exemption is the same lever that lets the
     * scheduled restart's startForeground succeed, so FIX#1 and FIX#2 are
     * intentionally synergistic.</p>
     *
     * <p>Inexact AlarmManager.set is used deliberately: we only need to
     * reappear, not hit a precise deadline, and exact alarms would require the
     * SCHEDULE_EXACT_ALARM permission on Android 12+. A battery-opt-exempt app
     * is exempt from the inexact-alarm Doze deferral anyway, so ~1.5s holds.</p>
     */
    @Override
    public void onTaskRemoved(Intent rootIntent) {
        try {
            if (boxService != null && isIgnoringBatteryOptimizations()) {
                Intent restart = new Intent(getApplicationContext(), VpnRouterService.class)
                        .setAction(ACTION_RESTART);
                PendingIntent pi = PendingIntent.getService(
                        this, 1, restart,
                        PendingIntent.FLAG_ONE_SHOT | PendingIntent.FLAG_IMMUTABLE);
                AlarmManager am = (AlarmManager) getSystemService(ALARM_SERVICE);
                if (am != null && pi != null) {
                    am.set(AlarmManager.ELAPSED_REALTIME_WAKEUP,
                            SystemClock.elapsedRealtime() + 1500L, pi);
                    Log.i(LOG_TAG, "AND-NODOZE: onTaskRemoved — tunnel active + "
                            + "battery-exempt; scheduled restart in 1.5s");
                }
            } else if (boxService != null) {
                Log.w(LOG_TAG, "AND-NODOZE: onTaskRemoved — tunnel active but NOT "
                        + "battery-exempt; cannot safely restart from background. "
                        + "Grant battery exemption for swipe-away recovery.");
            }
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NODOZE: onTaskRemoved restart-schedule threw: " + e.getMessage());
        }
        super.onTaskRemoved(rootIntent);
    }

    /**
     * v2.40.0-r8 (#3 bug-scout HIGH/regression fix) — cancel a pending
     * onTaskRemoved swipe-recovery restart alarm. Called on an EXPLICIT user stop
     * (ACTION_STOP / onRevoke) so that if the user swiped the app away and then
     * deliberately Disconnected within the ~1.5s window, the scheduled
     * ACTION_RESTART does NOT fire and silently re-establish the tunnel the user
     * just turned off. Deliberately NOT called from onDestroy — a swipe the OEM
     * honours by killing the FGS lands there, and the pending restart is the
     * intended recovery. Reconstructs the same PendingIntent (request code 1 +
     * ACTION_RESTART + FLAG_IMMUTABLE) so am.cancel matches by filterEquals; the
     * alarm lives in the AlarmManager system service so this holds across the
     * stopSelf that follows.
     */
    private void cancelScheduledRestart() {
        try {
            Intent restart = new Intent(getApplicationContext(), VpnRouterService.class)
                    .setAction(ACTION_RESTART);
            PendingIntent pi = PendingIntent.getService(
                    this, 1, restart,
                    PendingIntent.FLAG_NO_CREATE | PendingIntent.FLAG_IMMUTABLE);
            if (pi != null) {
                AlarmManager am = (AlarmManager) getSystemService(ALARM_SERVICE);
                if (am != null) am.cancel(pi);
                pi.cancel();
                Log.i(LOG_TAG, "AND-NODOZE: cancelled pending swipe-recovery restart "
                        + "(explicit user stop)");
            }
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NODOZE: cancelScheduledRestart threw: " + e.getMessage());
        }
    }

    /**
     * v2.40.0 AND-NODOZE — live battery-optimization-exemption read. Mirrors
     * the C#-side AndroidApp.Permissions.IsIgnoringBatteryOptimizations so the
     * service can decide whether a background self-restart is permitted.
     * Returns false on any error (fail-safe: don't attempt a restart that
     * would throw).
     */
    private boolean isIgnoringBatteryOptimizations() {
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            return pm != null && pm.isIgnoringBatteryOptimizations(getPackageName());
        } catch (Exception e) {
            return false;
        }
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
                .setContentText(notifText)
                .setSmallIcon(android.R.drawable.ic_lock_idle_lock)
                .setOngoing(true)
                .addAction(android.R.drawable.ic_menu_close_clear_cancel, notifDisconnect, stopPi)
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
            // A10: serve the process-lifetime cache when warm. Hand out a fresh
            // copy so the iterator can never disturb the shared (immutable) list.
            List<String> cached = sCachedSystemCertificatePems;
            if (cached != null) {
                return new SimpleStringIterator(new ArrayList<>(cached));
            }
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
                // Publish an immutable snapshot. A benign race just recomputes the
                // same set — no lock needed.
                sCachedSystemCertificatePems = Collections.unmodifiableList(certs);
                return new SimpleStringIterator(new ArrayList<>(certs));
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
                    cm.registerBestMatchingNetworkCallback(request, defaultCallback, service.ensureNetCallbackHandler());
                    Log.i(LOG_TAG, "Phase 6.2: registerBestMatchingNetworkCallback (API 31+)");
                } else if (Build.VERSION.SDK_INT >= 28) {
                    NetworkRequest request = new NetworkRequest.Builder()
                            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                            .build();
                    cm.requestNetwork(request, defaultCallback, service.ensureNetCallbackHandler());
                    Log.i(LOG_TAG, "Phase 6.2: requestNetwork (API 28-30)");
                } else if (Build.VERSION.SDK_INT >= 26) {
                    cm.registerDefaultNetworkCallback(defaultCallback, service.ensureNetCallbackHandler());
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
