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
    public static final String ACTION_RESTART = "com.ninitux.vpnrouter.RESTART";
    public static final String EXTRA_CONFIG_JSON = "config_json";
    public static final String EXTRA_ALLOWED_PACKAGES = "allowed_packages";
    public static final String EXTRA_PER_APP_MODE = "per_app_mode";
    public static final String EXTRA_PER_APP_PACKAGES = "per_app_packages";
    public static final String EXTRA_NOTIF_TEXT = "notif_text";
    public static final String EXTRA_NOTIF_DISCONNECT = "notif_disconnect";

    private String notifText = "Tunnel active";
    private String notifDisconnect = "Disconnect";
    public static final String ACTION_TUNNEL_UP = "com.ninitux.vpnrouter.TUNNEL_UP";
    public static final String ACTION_TUNNEL_DOWN = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    public static final String ACTION_TUNNEL_ERROR = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    public static final String ACTION_STATS = "com.ninitux.vpnrouter.STATS";
    public static final String EXTRA_STATS_DOWN = "stats_down_total";
    public static final String EXTRA_STATS_UP = "stats_up_total";
    public static final String EXTRA_STATS_CONN = "stats_conn";
    public static final String EXTRA_ERROR_MESSAGE = "error_message";
    public static final String EXTRA_DNS_TUNNEL_DOMAIN = "dns_tunnel_domain";
    public static final String EXTRA_DNS_TUNNEL_RESOLVERS = "dns_tunnel_resolvers";
    public static final String EXTRA_DNS_TUNNEL_CERT = "dns_tunnel_cert";
    public static final String EXTRA_DNS_TUNNEL_PORT = "dns_tunnel_port";
    public static final String EXTRA_DNS_TUNNEL_USE_SYSTEM_RESOLVER = "dns_tunnel_use_system_resolver";

    private static final int NOTIFICATION_ID = 100;
    private static final String NOTIFICATION_CHANNEL_ID = "vpnrouter_tunnel";
    private static final String LOG_TAG = "VpnRouter";

    private static final String PREFS_NAME = "vpnrouter_settings";
    private static final String KEY_LAST_GOOD_CONFIG = "last_good_config_json";
    private static final String KEY_LAST_GOOD_PER_APP_MODE = "last_good_per_app_mode";
    private static final String KEY_LAST_GOOD_PER_APP_PACKAGES = "last_good_per_app_packages_lines";
    private static final String KEY_AUTO_RECONNECT = "auto_reconnect_on_network_change";
    private static final String KEY_TUNNEL_LIVE = "tunnel_live";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN = "last_good_dns_tunnel_domain";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS = "last_good_dns_tunnel_resolvers_lines";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_CERT = "last_good_dns_tunnel_cert";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_PORT = "last_good_dns_tunnel_port";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER = "last_good_dns_tunnel_use_system_resolver";

    private static boolean libboxSetupDone = false;
    private static volatile List<String> sCachedSystemCertificatePems;

    private String pendingConfigJson;
    private String[] pendingAllowedPackages;
    private String pendingPerAppMode;
    private String[] pendingPerAppPackages;
    private String pendingDnsTunnelDomain;
    private String[] pendingDnsTunnelResolvers;
    private String pendingDnsTunnelCert;
    private int pendingDnsTunnelPort;
    private boolean pendingDnsTunnelUseSystemResolver;
    private volatile boolean slipstreamRunning;
    private volatile BoxService boxService;
    private volatile ParcelFileDescriptor currentPfd;
    private PowerManager.WakeLock connectWakeLock;

    private HandlerThread netCallbackThread;
    private Handler netCallbackHandler;

    private final java.util.concurrent.ExecutorService lifecycleExecutor = newLifecycleExecutor();

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

    private void submitLifecycle(Runnable task) {
        try {
            lifecycleExecutor.execute(task);
        } catch (java.util.concurrent.RejectedExecutionException rex) {
            Log.w(LOG_TAG, "lifecycle executor rejected task (shutting down): " + rex.getMessage());
        }
    }

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

    private synchronized Handler ensureNetCallbackHandler() {
        if (netCallbackHandler == null) {
            netCallbackThread = new HandlerThread("vpn-net-monitor");
            netCallbackThread.start();
            netCallbackHandler = new Handler(netCallbackThread.getLooper());
        }
        return netCallbackHandler;
    }

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
        installJavaUncaughtHandler();
        initScreenStateReceiver();
    }

    private void installJavaUncaughtHandler() {
        try {
            final Thread.UncaughtExceptionHandler previous =
                    Thread.getDefaultUncaughtExceptionHandler();
            Thread.setDefaultUncaughtExceptionHandler(new Thread.UncaughtExceptionHandler() {
                @Override
                public void uncaughtException(Thread thread, Throwable throwable) {
                    try {
                        writeJavaCrashReport(thread, throwable);
                    } catch (Throwable t) {
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

    private void initScreenStateReceiver() {
        try {
            android.os.PowerManager pm = (android.os.PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT_WATCH) {
                    isScreenOn = pm.isInteractive();
                } else {
                    isScreenOn = pm.isScreenOn();
                }
            }
        } catch (Exception ignored) {}

        try {
            android.content.IntentFilter filter = new android.content.IntentFilter();
            filter.addAction(Intent.ACTION_SCREEN_ON);
            filter.addAction(Intent.ACTION_SCREEN_OFF);
            screenStateReceiver = new android.content.BroadcastReceiver() {
                @Override
                public void onReceive(Context context, Intent intent) {
                    if (intent == null) return;
                    String action = intent.getAction();
                    if (Intent.ACTION_SCREEN_OFF.equals(action)) {
                        isScreenOn = false;
                        stopStatsPoller();
                    } else if (Intent.ACTION_SCREEN_ON.equals(action)) {
                        isScreenOn = true;
                        if (boxService != null) {
                            startStatsPoller();
                        }
                    }
                }
            };
            registerReceiver(screenStateReceiver, filter);
        } catch (Exception ex) {
            Log.w(LOG_TAG, "Failed to register screenStateReceiver: " + ex.getMessage());
        }
    }

    private void releaseScreenStateReceiver() {
        if (screenStateReceiver != null) {
            try {
                unregisterReceiver(screenStateReceiver);
            } catch (Exception ignored) {}
            screenStateReceiver = null;
        }
    }

    private void writeJavaCrashReport(Thread thread, Throwable throwable) {
        try {
            File filesDir = getFilesDir();
            if (filesDir == null) return;
            File crashesDir = new File(filesDir, "crashes");
            if (!crashesDir.exists() && !crashesDir.mkdirs()) {
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
                // Crash text can contain vless URIs and keys: scrub before writing.
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
            String nText = intent.getStringExtra(EXTRA_NOTIF_TEXT);
            if (nText != null && !nText.isEmpty()) notifText = nText;
            String nDisc = intent.getStringExtra(EXTRA_NOTIF_DISCONNECT);
            if (nDisc != null && !nDisc.isEmpty()) notifDisconnect = nDisc;
            if (!ensureForegroundStarted()) {
                return START_STICKY;
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
            cancelScheduledRestart();
            submitLifecycle(new Runnable() {
                @Override
                public void run() {
                    stopTunnel();
                    stopSelf();
                }
            });
        } else {
            Log.i(LOG_TAG, "AND-NETRES: system-initiated start (action=" + action
                    + ") — attempting last-good config restore");
            if (!ensureForegroundStarted()) {
                return START_STICKY;
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
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

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

    private void startTunnel() {
        if (boxService != null) {
            Log.i(LOG_TAG, "startTunnel: tunnel already live — tearing down previous before re-start");
            teardownTunnelResources();
        }
        acquireConnectWakeLock();

        clashApiSecret = extractClashApiSecret(pendingConfigJson);

        try {
            ensureLibboxSetup();
            startSlipstreamIfNeeded();
            startLibboxService();
            persistLastGoodConfig();
            sendBroadcast(new Intent(ACTION_TUNNEL_UP).setPackage(getPackageName()));
            setTunnelLive(true);
            startStatsPoller();
        } catch (Exception e) {
            String safeMsg = scrubSecrets(e.getMessage());
            Log.e(LOG_TAG, "startTunnel failed: " + e.getClass().getName() + ": " + safeMsg);
            try {
                teardownTunnelResources();
            } catch (Exception te) {
                Log.w(LOG_TAG, "teardownTunnelResources on start failure threw: " + te.getMessage());
            }
            Intent err = new Intent(ACTION_TUNNEL_ERROR).setPackage(getPackageName());
            err.putExtra(EXTRA_ERROR_MESSAGE, e.getClass().getSimpleName() + ": " + safeMsg);
            sendBroadcast(err);
            setTunnelLive(false);
            stopSelf();
        } finally {
            releaseConnectWakeLock();
        }
    }

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
            editor.apply();
            Log.i(LOG_TAG, "AND-NETRES: persisted last-good config ("
                    + pendingConfigJson.length() + " chars, perAppMode="
                    + pendingPerAppMode + ", packages="
                    + (pendingPerAppPackages == null ? 0 : pendingPerAppPackages.length) + ")");
        } catch (Exception e) {
            Log.w(LOG_TAG, "AND-NETRES: persistLastGoodConfig threw: " + e.getMessage());
        }
    }

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

        VpnRouterPlatformInterface platformInterface = new VpnRouterPlatformInterface(this);
        boxService = Libbox.newService(pendingConfigJson, platformInterface);
        boxService.start();

        Log.i(LOG_TAG, "libbox service started successfully (v2.32.0)");
    }

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
        if (!waitForLocalPort(port, 8_000L)) {
            throw new Exception("dns-tunnel: Slipstream front did not start listening on 127.0.0.1:" + port);
        }
        Log.i(LOG_TAG, "dns-tunnel: Slipstream front is listening on 127.0.0.1:" + port);
    }

    private String[] readSystemResolvers() {
        java.util.List<String> out = new java.util.ArrayList<>();
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            if (cm == null) return new String[0];
            java.util.List<Network> nets = new java.util.ArrayList<>();
            Network active = cm.getActiveNetwork();
            if (active != null) nets.add(active);
            for (Network n : cm.getAllNetworks()) {
                if (!nets.contains(n)) nets.add(n);
            }
            for (Network net : nets) {
                LinkProperties lp = cm.getLinkProperties(net);
                if (lp == null || lp.getDnsServers() == null) continue;
                for (java.net.InetAddress a : lp.getDnsServers()) {
                    if (!(a instanceof java.net.Inet4Address)) continue;
                    if (a.isLoopbackAddress() || a.isLinkLocalAddress()) continue;
                    String h = a.getHostAddress();
                    if (h == null) continue;
                    String ep = h + ":53";
                    if (!out.contains(ep)) out.add(ep);
                }
                if (!out.isEmpty()) break;
            }
        } catch (Exception e) {
            Log.w(LOG_TAG, "dns-tunnel: readSystemResolvers threw: " + e.getMessage());
        }
        return out.toArray(new String[0]);
    }

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

    private void stopSlipstreamIfRunning() {
        if (!slipstreamRunning) return;
        slipstreamRunning = false;
        // Bound nativeStop: a Slipstream worker stuck in reconnect backoff can hang the join and wedge the UI.
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

    private java.util.concurrent.ScheduledExecutorService statsPoller;
    private android.content.BroadcastReceiver screenStateReceiver = null;
    private volatile boolean isScreenOn = true;

    private volatile String clashApiSecret;

    private static String extractClashApiSecret(String configJson) {
        if (configJson == null) return null;
        try {
            org.json.JSONObject root = new org.json.JSONObject(configJson);
            org.json.JSONObject exp = root.optJSONObject("experimental");
            if (exp == null) return null;
            org.json.JSONObject ca = exp.optJSONObject("clash_api");
            if (ca == null) return null;
            String s = ca.optString("secret", "");
            return s.isEmpty() ? null : s;
        } catch (Exception ignore) {
            return null;
        }
    }

    private synchronized void startStatsPoller() {
        stopStatsPoller();
        if (!isScreenOn) return;
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
        boolean protectedOk = false;
        try {
            sock = new Socket();
            protectedOk = protect(sock);
            sock.connect(new InetSocketAddress("127.0.0.1", 9090), 2000);
            sock.setSoTimeout(2000);
            java.io.OutputStream os = sock.getOutputStream();
            String secret = clashApiSecret;
            String auth = (secret == null || secret.isEmpty())
                ? "" : ("Authorization: Bearer " + secret + "\r\n");
            os.write(("GET /connections HTTP/1.0\r\nHost: 127.0.0.1\r\n" + auth + "\r\n").getBytes("UTF-8"));
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
            int conn = 0;
            try {
                if (obj.has("connections") && !obj.isNull("connections"))
                    conn = obj.getJSONArray("connections").length();
            } catch (org.json.JSONException ignore) {
            }
            Intent it = new Intent(ACTION_STATS).setPackage(getPackageName());
            it.putExtra(EXTRA_STATS_DOWN, down);
            it.putExtra(EXTRA_STATS_UP, up);
            it.putExtra(EXTRA_STATS_CONN, conn);
            sendBroadcast(it);
        } catch (Exception e) {
            if (!protectedOk)
                Log.w(LOG_TAG, "pollStatsOnce: stats tick failed after protect()=false — " + e);
        } finally {
            if (sock != null) { try { sock.close(); } catch (Exception ignore) { } }
        }
    }

    private boolean teardownTunnelResources() {
        stopStatsPoller();
        final BoxService bs = boxService;
        boxService = null;
        final boolean wasLive = bs != null || slipstreamRunning || currentPfd != null;
        if (bs != null) {
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
        stopSlipstreamIfRunning();
        if (currentPfd != null) {
            try { currentPfd.close(); } catch (Exception e) {
                Log.w(LOG_TAG, "pfd.close threw: " + e.getMessage());
            }
            currentPfd = null;
        }
        releaseConnectWakeLock();
        return wasLive;
    }

    private void stopTunnel() {
        if (!teardownTunnelResources()) {
            return;
        }
        stopForeground(STOP_FOREGROUND_REMOVE);
        try {
            sendBroadcast(new Intent(ACTION_TUNNEL_DOWN).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.w(LOG_TAG, "broadcast tunnel-down threw: " + e.getMessage());
        }
        setTunnelLive(false);
    }

    @Override
    public void onRevoke() {
        Log.i(LOG_TAG, "onRevoke: VPN revoked by system/user — tearing down tunnel");
        cancelScheduledRestart();
        submitLifecycle(new Runnable() {
            @Override
            public void run() { stopTunnel(); }
        });
        super.onRevoke();
    }

    @Override
    public void onDestroy() {
        releaseScreenStateReceiver();
        submitLifecycle(new Runnable() {
            @Override
            public void run() {
                stopTunnel();
                quitNetCallbackThread();
            }
        });
        lifecycleExecutor.shutdown();
        super.onDestroy();
    }

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

    int openTun(TunOptions options) throws Exception {
        Builder builder = new Builder()
                .setSession("Virtual Penguin Network")
                .setMtu(options.getMTU());

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            builder.setMetered(false);
        }

        List<String> inet4Addrs = addPrefixesAsAddresses(builder, options.getInet4Address());
        List<String> inet6Addrs = addPrefixesAsAddresses(builder, options.getInet6Address());

        List<String> inet4Routes = addPrefixesAsRoutes(builder, options.getInet4RouteAddress());
        boolean any4 = !inet4Routes.isEmpty();
        if (!any4) builder.addRoute("0.0.0.0", 0);
        List<String> inet6Routes = addPrefixesAsRoutes(builder, options.getInet6RouteAddress());
        boolean any6 = !inet6Routes.isEmpty();
        if (!any6) builder.addRoute("::", 0);

        Log.i(LOG_TAG, "openTun: mtu=" + options.getMTU());
        Log.i(LOG_TAG, "openTun: inet4 addresses=" + joinPrefixes(inet4Addrs));
        Log.i(LOG_TAG, "openTun: inet6 addresses=" + joinPrefixes(inet6Addrs));
        Log.i(LOG_TAG, "openTun: inet4 routes=" + joinPrefixes(inet4Routes)
                + (any4 ? "" : "; adding fallback 0.0.0.0/0"));
        Log.i(LOG_TAG, "openTun: inet6 routes=" + joinPrefixes(inet6Routes)
                + (any6 ? "" : "; adding fallback ::/0"));

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

        boolean isInclude = "include".equalsIgnoreCase(pendingPerAppMode);
        boolean isExclude = "exclude".equalsIgnoreCase(pendingPerAppMode);

        if (isInclude) {
            addPackages(builder, options.getIncludePackage(), true);
            if (pendingPerAppPackages != null) {
                for (String pkg : pendingPerAppPackages) {
                    if (pkg == null || pkg.isEmpty()) continue;
                    try {
                        builder.addAllowedApplication(pkg);
                    } catch (PackageManager.NameNotFoundException ignored) {}
                }
            }
        } else if (isExclude) {
            addPackages(builder, options.getExcludePackage(), false);
            if (pendingPerAppPackages != null) {
                for (String pkg : pendingPerAppPackages) {
                    if (pkg == null || pkg.isEmpty()) continue;
                    try {
                        builder.addDisallowedApplication(pkg);
                    } catch (PackageManager.NameNotFoundException ignored) {}
                }
            }
            try {
                builder.addDisallowedApplication(getPackageName());
            } catch (PackageManager.NameNotFoundException ignored) {}
        } else {
            boolean hasInclude = options.getIncludePackage() != null && options.getIncludePackage().hasNext();
            if (hasInclude) {
                addPackages(builder, options.getIncludePackage(), true);
            } else {
                addPackages(builder, options.getExcludePackage(), false);
                try {
                    builder.addDisallowedApplication(getPackageName());
                } catch (PackageManager.NameNotFoundException ignored) {}
            }
        }

        ParcelFileDescriptor pfd = builder.establish();
        if (pfd == null) {
            throw new Exception("VpnService.Builder.establish returned null");
        }
        currentPfd = pfd;
        return pfd.getFd();
    }

    private static List<String> addPrefixesAsAddresses(Builder builder, RoutePrefixIterator iter) {
        List<String> applied = new ArrayList<>();
        if (iter == null) return applied;
        while (iter.hasNext()) {
            RoutePrefix p = iter.next();
            if (p == null) continue;
            String addr = p.address();
            if (addr != null && !addr.isEmpty()) {
                builder.addAddress(addr, p.prefix());
                applied.add(addr + "/" + p.prefix());
            }
        }
        return applied;
    }

    private static List<String> addPrefixesAsRoutes(Builder builder, RoutePrefixIterator iter) {
        List<String> applied = new ArrayList<>();
        if (iter == null) return applied;
        while (iter.hasNext()) {
            RoutePrefix p = iter.next();
            if (p == null) continue;
            String addr = p.address();
            if (addr != null && !addr.isEmpty()) {
                builder.addRoute(addr, p.prefix());
                applied.add(addr + "/" + p.prefix());
            }
        }
        return applied;
    }

    private static String joinPrefixes(List<String> items) {
        if (items == null || items.isEmpty()) return "<empty>";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < items.size(); i++) {
            if (i > 0) sb.append(", ");
            sb.append(items.get(i));
        }
        return sb.toString();
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

    private static final class VpnRouterPlatformInterface implements PlatformInterface {
        private final VpnRouterService service;

        private InterfaceUpdateListener defaultListener;
        private ConnectivityManager.NetworkCallback defaultCallback;
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
            return Build.VERSION.SDK_INT < Build.VERSION_CODES.Q;
        }

        @Override
        public boolean usePlatformAutoDetectInterfaceControl() {
            return true;
        }

        @Override
        public void autoDetectInterfaceControl(int fd) throws Exception {
            if (!service.protect(fd)) {
                throw new Exception("VpnService.protect(" + fd + ") failed");
            }
        }

        @Override
        public void clearDNSCache() {
        }

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

                    List<String> dnsHosts = new ArrayList<>();
                    if (lp.getDnsServers() != null) {
                        for (java.net.InetAddress a : lp.getDnsServers()) {
                            String h = a.getHostAddress();
                            if (h != null) dnsHosts.add(h);
                        }
                    }
                    bi.setDNSServer(new SimpleStringIterator(dnsHosts));

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

                    List<String> addrs = new ArrayList<>();
                    for (InterfaceAddress ia : sysIface.getInterfaceAddresses()) {
                        java.net.InetAddress a = ia.getAddress();
                        String host = a.getHostAddress();
                        if (host == null) continue;
                        if (a instanceof Inet6Address) {
                            int pct = host.indexOf('%');
                            if (pct >= 0) host = host.substring(0, pct);
                        }
                        addrs.add(host + "/" + ia.getNetworkPrefixLength());
                    }
                    bi.setAddresses(new SimpleStringIterator(addrs));

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

        @Override
        public StringIterator systemCertificates() {
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
                sCachedSystemCertificatePems = Collections.unmodifiableList(certs);
                return new SimpleStringIterator(new ArrayList<>(certs));
            } catch (Exception e) {
                Log.w(LOG_TAG, "systemCertificates failed: " + e.getMessage());
                return new SimpleStringIterator(new ArrayList<>());
            }
        }

        @Override
        public LocalDNSTransport localDNSTransport() {
            return null;
        }

        @Override
        public WIFIState readWIFIState() {
            return null;
        }

        @Override
        public boolean includeAllNetworks() { return false; }

        @Override
        public boolean underNetworkExtension() { return false; }

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
            return -1;
        }

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
            } catch (Exception ignore) { }
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
