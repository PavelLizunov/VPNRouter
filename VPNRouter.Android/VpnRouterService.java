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
import android.util.Log;

import androidx.core.app.NotificationCompat;

import java.io.File;
import java.net.Inet6Address;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

import io.nekohasekai.libbox.CommandClient;
import io.nekohasekai.libbox.CommandClientHandler;
import io.nekohasekai.libbox.CommandClientOptions;
import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.ConnectionEvents;
import io.nekohasekai.libbox.LogIterator;
import io.nekohasekai.libbox.OutboundGroupIterator;
import io.nekohasekai.libbox.OutboundGroupItemIterator;
import io.nekohasekai.libbox.StatusMessage;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.RoutePrefix;
import io.nekohasekai.libbox.RoutePrefixIterator;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class VpnRouterService extends VpnService {

    public static final String ACTION_START = "com.ninitux.vpnrouter.START";
    public static final String ACTION_STOP = "com.ninitux.vpnrouter.STOP";
    public static final String ACTION_RESTART = "com.ninitux.vpnrouter.RESTART";
    public static final String EXTRA_CONFIG_JSON = "config_json";
    public static final String EXTRA_COLD_START_ONLY = "cold_start_only";
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
    public static final String EXTRA_STATS_AT = "stats_at_elapsed_ms";
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
    private static final String KEY_LAST_GOOD_PER_APP_PREFS = "last_good_per_app_source_prefs";
    private static final String KEY_AUTO_RECONNECT = "auto_reconnect_on_network_change";
    private static final String KEY_TUNNEL_LIVE = "tunnel_live";

    // Single connection-state record read by the quick-settings tile and the app screen
    // (VPNRouter.Core VpnStateCodec parses these four words; this service is the only writer).
    public static final String ACTION_TILE_START = "com.ninitux.vpnrouter.TILE_START";
    private static final String UI_COMMAND = "vpn_ui_command";
    private static final String STATE_EVENT = "vpn_state_event";

    // AndroidStorage uses this class monitor for the matching managed selection transaction.
    private static synchronized void supersedeUiCommand(SharedPreferences prefs, String command) {
        prefs.edit().putString(UI_COMMAND, command).apply();
    }

    private static final String RESTART_TICKET = "vpn_swipe_restart";
    private static final int RESTART_FLAGS = PendingIntent.FLAG_ONE_SHOT | PendingIntent.FLAG_IMMUTABLE;

    private static final String STATE_DISCONNECTED = "disconnected";
    private static final String STATE_CONNECTING = "connecting";
    private static final String STATE_CONNECTED = "connected";
    private static final String STATE_ERROR = "error";
    private static final String KEY_VPN_STATE = "vpn_state";
    private static final String KEY_VPN_STATE_REASON = "vpn_state_reason";
    private static final String KEY_VPN_STATE_PID = "vpn_state_pid";
    private static final String KEY_VPN_STATE_AT_MS = "vpn_state_at_ms";
    private static final String TILE_SERVICE_CLASS = "com.ninitux.vpnrouter.VpnTileService";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_DOMAIN = "last_good_dns_tunnel_domain";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_RESOLVERS = "last_good_dns_tunnel_resolvers_lines";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_CERT = "last_good_dns_tunnel_cert";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_PORT = "last_good_dns_tunnel_port";
    private static final String KEY_LAST_GOOD_DNS_TUNNEL_USE_SYSTEM_RESOLVER = "last_good_dns_tunnel_use_system_resolver";

    private String pendingConfigJson;
    private String pendingPerAppMode;
    private String[] pendingPerAppPackages;
    private String pendingPerAppPrefs;
    private volatile String establishedAppPolicy;
    private String pendingDnsTunnelDomain;
    private String[] pendingDnsTunnelResolvers;
    private String pendingDnsTunnelCert;
    private int pendingDnsTunnelPort;
    private boolean pendingDnsTunnelUseSystemResolver;
    private volatile CommandServer commandServer;
    private AndroidTunnelSession session;
    private static AndroidTunnelSession closingSession;
    private static volatile VpnRouterService liveService;
    private volatile boolean ended;
    private volatile boolean stopRequested;
    private int executingStartId;
    private String executingUiCommand;
    private volatile String serviceStateEvent;
    private String tunnelSessionId;
    private static final String ACTIVE_SESSION = "vpn_active_tunnel_session";
    private PowerManager.WakeLock connectWakeLock;

    private HandlerThread netCallbackThread;
    private Handler netCallbackHandler;

    // A new Android service instance can arrive while the previous instance is still closing.
    private static final ScheduledThreadPoolExecutor lifecycleExecutor = newLifecycleExecutor();
    private final Handler mainHandler = new Handler(android.os.Looper.getMainLooper());

    private static ScheduledThreadPoolExecutor newLifecycleExecutor() {
        ScheduledThreadPoolExecutor executor = new ScheduledThreadPoolExecutor(1, task -> {
            Thread thread = new Thread(task, "vpn-lifecycle");
            thread.setDaemon(true);
            return thread;
        });
        executor.setRemoveOnCancelPolicy(true);
        return executor;
    }

    private void submitLifecycle(Runnable task) {
        lifecycleExecutor.execute(task);
    }

    private void submitCommand(int startId, String origin, Runnable task) {
        submitLifecycle(() -> {
            if (ended) return;
            executingStartId = startId;
            executingUiCommand = origin;
            task.run();
        });
    }

    private void finishCommand(int startId) {
        mainHandler.post(() -> {
            if (stopSelfResult(startId)) removeForegroundNotification();
        });
    }

    @SuppressWarnings("deprecation")
    private void removeForegroundNotification() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) stopForeground(STOP_FOREGROUND_REMOVE);
        else stopForeground(true);
    }

    private void broadcastTunnelEvent(Intent event) {
        String pkg = getPackageName();
        if (!ACTION_STATS.equals(event.getAction())) {
            event.putExtra(UI_COMMAND, executingUiCommand);
            event.putExtra(STATE_EVENT, serviceStateEvent);
            event.putExtra(ACTIVE_SESSION, tunnelSessionId);
        }
        sendBroadcast(event.setPackage(pkg), pkg + ".permission.TUNNEL_STATUS");
    }

    @androidx.annotation.Keep
    public static boolean hasLiveTunnel() {
        VpnRouterService service = liveService;
        return service != null && !service.ended && !service.stopRequested && service.commandServer != null;
    }

    @androidx.annotation.Keep
    public static boolean hasRecentTunnelStatus() {
        VpnRouterService service = liveService;
        return service != null && service.wantsStats() && service.stats.hasRecentStatus(SystemClock.elapsedRealtime());
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
                        submitLifecycle(VpnRouterService.this::stopStatsPoller);
                    } else if (Intent.ACTION_SCREEN_ON.equals(action)) {
                        isScreenOn = true;
                        submitLifecycle(VpnRouterService.this::startStatsPoller);
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
    public synchronized int onStartCommand(Intent intent, int flags, int startId) {
        if (ended) return START_NOT_STICKY;
        String action = intent != null ? intent.getAction() : null;
        SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
        String uiCommand = intent == null ? null : intent.getStringExtra(UI_COMMAND);
        String restartTicket = intent == null ? null : intent.getStringExtra(RESTART_TICKET);
        boolean scheduledRestart = ACTION_RESTART.equals(action);
        final String origin;
        if (uiCommand == null && (ACTION_STOP.equals(action) || ACTION_START.equals(action) || ACTION_TILE_START.equals(action))) {
            origin = java.util.UUID.randomUUID().toString();
            supersedeUiCommand(prefs, origin);
        } else {
            origin = uiCommand != null ? uiCommand : prefs.getString(UI_COMMAND, null);
        }
        if (scheduledRestart && (restartTicket == null || !restartTicket.equals(prefs.getString(RESTART_TICKET, null)))) {
            submitCommand(startId, origin, () -> { if (commandServer == null) finishCommand(startId); });
            return START_NOT_STICKY;
        }
        if (ACTION_STOP.equals(action)) stopRequested = true;
        else if (uiCommand == null || uiCommand.equals(prefs.getString(UI_COMMAND, null))) stopRequested = false;
        if (ACTION_START.equals(action)) {
            String nText = intent.getStringExtra(EXTRA_NOTIF_TEXT);
            if (nText != null && !nText.isEmpty()) notifText = nText;
            String nDisc = intent.getStringExtra(EXTRA_NOTIF_DISCONNECT);
            if (nDisc != null && !nDisc.isEmpty()) notifDisconnect = nDisc;
            if (!ensureForegroundStarted(startId, origin)) {
                return START_NOT_STICKY;
            }
            final String cfg = intent.getStringExtra(EXTRA_CONFIG_JSON);
            final boolean coldStartOnly = intent.getBooleanExtra(EXTRA_COLD_START_ONLY, false);
            final String[] allowed = intent.getStringArrayExtra(EXTRA_ALLOWED_PACKAGES);
            final String perAppMode = intent.getStringExtra(EXTRA_PER_APP_MODE);
            final String[] perAppPkgs = intent.getStringArrayExtra(EXTRA_PER_APP_PACKAGES);
            final String dnsDomain = intent.getStringExtra(EXTRA_DNS_TUNNEL_DOMAIN);
            final String[] dnsResolvers = intent.getStringArrayExtra(EXTRA_DNS_TUNNEL_RESOLVERS);
            final String dnsCert = intent.getStringExtra(EXTRA_DNS_TUNNEL_CERT);
            final int dnsPort = intent.getIntExtra(EXTRA_DNS_TUNNEL_PORT, 7001);
            final boolean dnsUseSystem = intent.getBooleanExtra(EXTRA_DNS_TUNNEL_USE_SYSTEM_RESOLVER, false);
            submitCommand(startId, origin, new Runnable() {
                @Override
                public void run() {
                    if (uiCommand != null && !uiCommand.equals(prefs.getString(UI_COMMAND, null))) {
                        if (commandServer == null) finishCommand(startId);
                        return;
                    }
                    if (coldStartOnly && commandServer != null) {
                        reportFailure("config-invalid", true);
                        return;
                    }
                    String mode;
                    String[] packages;
                    String sourcePrefs;
                    try {
                        AndroidTunnelPolicy.Preferences selection = readAppPreferences(prefs);
                        sourcePrefs = selection.snapshot();
                        if (uiCommand != null) {
                            // Establish and record the same picker revision.
                            mode = selection.mode == null ? "off" : selection.mode;
                            packages = selection.packages.toArray(new String[0]);
                        } else {
                            boolean legacyInclude = AndroidTunnelPolicy.usesLegacyAllowlist(perAppMode, perAppPkgs, allowed);
                            mode = legacyInclude ? "include" : perAppMode;
                            packages = legacyInclude ? allowed : perAppPkgs;
                        }
                    } catch (Exception e) {
                        reportStartFailure(e, commandServer != null);
                        return;
                    }
                    String checked = prepareTunnelConfig(cfg, mode, packages, dnsDomain);
                    if (checked == null) return;
                    pendingConfigJson = cfg;
                    pendingPerAppMode = mode;
                    pendingPerAppPackages = packages;
                    pendingPerAppPrefs = sourcePrefs;
                    pendingDnsTunnelDomain = dnsDomain;
                    pendingDnsTunnelResolvers = dnsResolvers;
                    pendingDnsTunnelCert = dnsCert;
                    pendingDnsTunnelPort = dnsPort;
                    pendingDnsTunnelUseSystemResolver = dnsUseSystem;
                    startTunnel(checked);
                }
            });
        } else if (ACTION_STOP.equals(action)) {
            cancelScheduledRestart();
            submitCommand(startId, origin, new Runnable() {
                @Override
                public void run() {
                    stopTunnel(true);
                    finishCommand(startId);
                }
            });
            return START_NOT_STICKY;
        } else {
            Log.i(LOG_TAG, "AND-NETRES: system-initiated start (action=" + action
                    + ") — attempting last-good config restore");
            if (!ensureForegroundStarted(startId, origin)) {
                return START_NOT_STICKY;
            }
            submitCommand(startId, origin, new Runnable() {
                @Override
                public void run() {
                    if (scheduledRestart && !restartTicket.equals(prefs.getString(RESTART_TICKET, null))) {
                        if (commandServer == null) finishCommand(startId);
                        return;
                    }
                    if (scheduledRestart) prefs.edit().remove(RESTART_TICKET).apply();
                    if (!java.util.Objects.equals(origin, prefs.getString(UI_COMMAND, null))) {
                        if (commandServer == null) finishCommand(startId);
                        return;
                    }
                    if (commandServer != null) {
                        if (liveService == VpnRouterService.this && hasLiveTunnel() && session != null) {
                            // The tile can write Connecting before asking to restore an already-established tunnel.
                            if (!STATE_CONNECTED.equals(readVpnState())) writeVpnState(STATE_CONNECTED, null);
                            broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP));
                        }
                    } else if (loadLastGoodConfig()) {
                        String checked = prepareTunnelConfig(pendingConfigJson, pendingPerAppMode,
                                pendingPerAppPackages, pendingDnsTunnelDomain);
                        if (checked != null) startTunnel(checked);
                    } else {
                        Log.w(LOG_TAG, "AND-NETRES: no last-good config saved; "
                                + "user must launch app and tap Connect at least once");
                        writeVpnState(STATE_ERROR, "no-config");
                        finishCommand(startId);
                    }
                }
            });
        }
        return START_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return super.onBind(intent);
    }

    private boolean ensureForegroundStarted(int startId, String origin) {
        try {
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                startForeground(NOTIFICATION_ID, buildNotification(),
                        android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED);
            } else {
                startForeground(NOTIFICATION_ID, buildNotification());
            }
            return true;
        } catch (Exception e) {
            submitCommand(startId, origin, () -> reportFailure("foreground-start-blocked", commandServer != null));
            return false;
        }
    }

    private void writeVpnState(String state, String reason) {
        serviceStateEvent = null;
        String stateEvent = java.util.UUID.randomUUID().toString();
        try {
            getSharedPreferences(PREFS_NAME, MODE_PRIVATE)
                    .edit()
                    .putString(KEY_VPN_STATE, state)
                    .putString(STATE_EVENT, stateEvent)
                    .putString(ACTIVE_SESSION, STATE_CONNECTED.equals(state) ? tunnelSessionId : null)
                    .putString("vpn_active_per_app_policy", STATE_CONNECTED.equals(state) ? establishedAppPolicy : null)
                    .putString(KEY_VPN_STATE_REASON, reason)
                    .putInt(KEY_VPN_STATE_PID, android.os.Process.myPid())
                    .putLong(KEY_VPN_STATE_AT_MS, System.currentTimeMillis())
                    .apply();
            serviceStateEvent = stateEvent;
        } catch (Exception e) {
            Log.w(LOG_TAG, "writeVpnState(" + state + ") threw: " + e.getMessage());
        }
        refreshTile();
    }

    private String readVpnState() {
        try {
            return getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(KEY_VPN_STATE, null);
        } catch (Exception e) {
            return null;
        }
    }

    private void refreshTile() {
        if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.N) return;
        try {
            android.service.quicksettings.TileService.requestListeningState(
                    this, new android.content.ComponentName(getPackageName(), TILE_SERVICE_CLASS));
        } catch (Exception e) {
            Log.w(LOG_TAG, "refreshTile threw: " + e.getMessage());
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

    private String prepareTunnelConfig(String json, String mode, String[] packages, String dnsDomain) {
        try {
            LibboxRuntime.ensureSetup(this);
            if (json == null || json.isEmpty()) throw new IllegalArgumentException("VPN configuration is missing");
            List<String> selected = packages == null ? java.util.Collections.emptyList() : java.util.Arrays.asList(packages);
            // Both live switches and cached restores are validated before changing the active tunnel.
            AndroidTunnelPolicy.checkDeclaredPackages(json, mode);
            String checked = AndroidTunnelPolicy.prepare(Libbox.formatConfig(json).getValue(), mode,
                    dnsDomain != null && !dnsDomain.isEmpty(), selected, pkg -> {
                        try {
                            getPackageManager().getApplicationInfo(pkg, 0);
                            return true;
                        } catch (PackageManager.NameNotFoundException e) {
                            return false;
                        }
                    }, getFilesDir());
            if (dnsDomain != null && !dnsDomain.isEmpty() && !SlipstreamNative.isAvailable()) {
                throw new IllegalArgumentException("DNS-tunnel support is unavailable on this device");
            }
            Libbox.checkConfig(checked);
            return checked;
        } catch (Exception | LinkageError e) {
            reportStartFailure(e, commandServer != null);
            return null;
        }
    }

    private void startTunnel(String tunnelConfig) {
        if (ended || stopRequested) return;
        writeVpnState(STATE_CONNECTING, null);
        setTunnelLive(false);
        retireTunnel();
        if (ended || stopRequested) return;
        if (!previousTunnelClosed()) {
            reportFailure("tunnel-closing", false);
            return;
        }
        session = new AndroidTunnelSession();
        tunnelSessionId = java.util.UUID.randomUUID().toString();
        establishedAppPolicy = null;
        acquireConnectWakeLock();
        try {
            startSlipstreamIfNeeded();
            if (ended || stopRequested) return;
            startLibboxService(tunnelConfig);
            if (ended || stopRequested) return; // Stop or destruction already queued retirement.
            session.requireEstablishedTun();
            persistLastGoodConfig();
            liveService = this;
            writeVpnState(STATE_CONNECTED, null);
            setTunnelLive(true);
            broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP));
            startStatsPoller();
        } catch (Exception | LinkageError e) {
            retireTunnel();
            if (!ended) reportFailure("start-failed", false);
        } finally {
            releaseConnectWakeLock();
        }
    }

    private void reportStartFailure(Throwable error, boolean previousTunnelActive) {
        // A native parser may include the supplied configuration in an exception message.
        String code = "config-invalid";
        if (error instanceof IllegalArgumentException && error.getMessage() != null) {
            switch (error.getMessage()) {
                case "apps-empty":
                case "The TUN application allowlist is empty":
                case "Select at least one installed app for per-app VPN":
                    code = "apps-empty";
                    break;
                case "Cannot enforce an Android package exclusion":
                    code = "apps-missing";
                    break;
                case "per-app-listener":
                case "Per-app VPN requires a single TUN inbound":
                case "This service is incompatible with per-app VPN":
                case "This endpoint is incompatible with per-app VPN":
                case "Tor's SOCKS listener is incompatible with per-app VPN":
                case "ACME challenge listeners are incompatible with per-app VPN":
                case "The DNS tunnel's local listener is incompatible with per-app VPN":
                    code = "per-app-listener";
                    break;
                case "dns-unavailable":
                case "DNS-tunnel support is unavailable on this device":
                    code = "dns-unavailable";
                    break;
                case "Unsupported TUN DNS mode":
                case "Android cannot use a wildcard or loopback VPN DNS server":
                case "Android cannot use a loopback TUN DNS server":
                case "TUN needs a usable DNS address for an enabled address family":
                    code = "dns-invalid";
                    break;
            }
        }
        if (!ended) reportFailure(code, previousTunnelActive);
    }

    private void reportFailure(String code, boolean previousTunnelActive) {
        Log.w(LOG_TAG, "VPN start failed: " + code);
        Intent err = new Intent(ACTION_TUNNEL_ERROR);
        err.putExtra(EXTRA_ERROR_MESSAGE, code);
        err.putExtra("previous_tunnel_active", previousTunnelActive);
        if (!previousTunnelActive) {
            writeVpnState(STATE_ERROR, code);
            setTunnelLive(false);
        } else if (liveService == this && !STATE_CONNECTED.equals(readVpnState())) {
            // A pending tile request must not hide the TUN that survived this rejected command.
            writeVpnState(STATE_CONNECTED, null);
        }
        broadcastTunnelEvent(err);
        if (!previousTunnelActive) finishCommand(executingStartId);
    }

    private void persistLastGoodConfig() {
        try {
            SharedPreferences prefs = getSharedPreferences(PREFS_NAME, MODE_PRIVATE);
            SharedPreferences.Editor editor = prefs.edit();
            editor.putString(KEY_LAST_GOOD_CONFIG, pendingConfigJson);
            editor.putString(KEY_LAST_GOOD_PER_APP_PREFS, pendingPerAppPrefs);
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

    private static AndroidTunnelPolicy.Preferences readAppPreferences(SharedPreferences prefs)
            throws org.json.JSONException {
        java.util.Map<String, ?> values = prefs.getAll();
        String packages = (String) values.get(EXTRA_PER_APP_PACKAGES);
        return new AndroidTunnelPolicy.Preferences((String) values.get(EXTRA_PER_APP_MODE),
                packages == null ? "[]" : packages);
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
            AndroidTunnelPolicy.Preferences current = readAppPreferences(prefs);
            if (current.supersedes(prefs.getString(KEY_LAST_GOOD_PER_APP_PREFS, null), pendingPerAppMode)) {
                pendingPerAppMode = current.mode;
                pendingPerAppPackages = current.packages.toArray(new String[0]);
            }
            pendingPerAppPrefs = current.snapshot();
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

    private void startLibboxService(String tunnelConfig) throws Exception {
        VpnRouterPlatformInterface platform = new VpnRouterPlatformInterface(this, session,
                pendingPerAppMode, pendingPerAppPackages);
        commandServer = LibboxRuntime.startChecked(tunnelConfig, platform, true);

        Log.i(LOG_TAG, "libbox service started successfully");
    }

    private void startSlipstreamIfNeeded() throws Exception {
        if (pendingDnsTunnelDomain == null || pendingDnsTunnelDomain.isEmpty()) {
            return;
        }
        int port = pendingDnsTunnelPort > 0 ? pendingDnsTunnelPort : 7001;
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
        session.sidecarStarted(SlipstreamNative::nativeStop);
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

    private android.content.BroadcastReceiver screenStateReceiver;
    private volatile boolean isScreenOn = true;

    private final AndroidStatsSubscription<CommandClient> stats = new AndroidStatsSubscription<>(
            new AndroidStatsSubscription.Client<CommandClient>() {
                public CommandClient create(long generation) throws Exception {
                    CommandClientOptions options = new CommandClientOptions();
                    options.addCommand(Libbox.CommandStatus);
                    options.setStatusInterval(TimeUnit.SECONDS.toNanos(2));
                    return Libbox.newCommandClient(new StatsHandler(generation, tunnelSessionId), options);
                }
                public void connect(CommandClient client) throws Exception { client.connect(); }
                public void disconnect(CommandClient client) throws Exception { client.disconnect(); }
            }, (task, delayMs) -> {
                ScheduledFuture<?> retry = lifecycleExecutor.schedule(() -> {
                    if (wantsStats()) task.run();
                }, delayMs, TimeUnit.MILLISECONDS);
                return () -> retry.cancel(false);
            });

    private boolean wantsStats() {
        return liveService == this && !ended && !stopRequested && isScreenOn && commandServer != null;
    }

    private void startStatsPoller() {
        if (wantsStats()) stats.start();
    }

    private void stopStatsPoller() {
        stats.stop();
    }

    private final class StatsHandler implements CommandClientHandler {
        private final long generation;
        private final String sessionId;
        private long lastSampleAt = -1; // Accessed only on the lifecycle executor.
        StatsHandler(long generation, String sessionId) {
            this.generation = generation;
            this.sessionId = sessionId;
        }

        public void writeStatus(StatusMessage status) {
            if (status == null || !wantsStats()) return;
            long receivedAt = SystemClock.elapsedRealtime();
            // Capture ownership at native callback time, before queuing the sample.
            String stateEvent = serviceStateEvent;
            String command = getSharedPreferences(PREFS_NAME, MODE_PRIVATE).getString(UI_COMMAND, null);
            if (stateEvent == null || !stateEvent.equals(serviceStateEvent)) return;
            boolean traffic = status.getTrafficAvailable();
            final long down = status.getDownlinkTotal(), up = status.getUplinkTotal();
            final int connections = status.getConnectionsIn();
            submitLifecycle(() -> {
                if (!wantsStats() || receivedAt <= lastSampleAt || !stats.acceptStatus(generation, receivedAt)) return;
                lastSampleAt = receivedAt;
                if (!traffic) return;
                Intent event = new Intent(ACTION_STATS);
                event.putExtra(UI_COMMAND, command);
                event.putExtra(STATE_EVENT, stateEvent);
                event.putExtra(ACTIVE_SESSION, sessionId);
                event.putExtra(EXTRA_STATS_AT, receivedAt);
                event.putExtra(EXTRA_STATS_DOWN, down);
                event.putExtra(EXTRA_STATS_UP, up);
                event.putExtra(EXTRA_STATS_CONN, connections);
                broadcastTunnelEvent(event);
            });
        }

        public void connected() { }

        public void disconnected(String message) {
            submitLifecycle(() -> {
                if (wantsStats()) stats.disconnected(generation);
            });
        }
        public void setDefaultLogLevel(int level) { }
        public void clearLogs() { }
        public void writeLogs(LogIterator logs) { }
        public void writeGroups(OutboundGroupIterator groups) { }
        public void writeOutbounds(OutboundGroupItemIterator items) { }
        public void initializeClashMode(StringIterator modes, String currentMode) { }
        public void updateClashMode(String mode) { }
        public void writeConnectionEvents(ConnectionEvents events) { }
    }

    private void retireTunnel() {
        if (session == null) return;
        if (liveService == this) liveService = null;
        AndroidTunnelSession old = session;
        old.retire();
        session = null;
        tunnelSessionId = null;
        closingSession = old;
        CommandServer server = commandServer;
        commandServer = null;
        stopStatsPoller();
        // stopMain already bounds the wait and its gate survives a failed/partial start.
        if (server != null && !LibboxRuntime.stopMain(server)) {
            Log.w(LOG_TAG, "Waiting for native tunnel shutdown");
        }
        old.stopSidecar(4000);
        if (!previousTunnelClosed()) scheduleCloseCheck(old);
        releaseConnectWakeLock();
    }

    private static boolean previousTunnelClosed() {
        boolean mainIdle = LibboxRuntime.isMainIdle();
        if (closingSession == null) return mainIdle;
        if (!closingSession.releaseIfClosed(mainIdle)) return false;
        closingSession = null;
        return true;
    }

    private static void scheduleCloseCheck(AndroidTunnelSession old) {
        lifecycleExecutor.schedule(() -> {
            if (closingSession == old && !previousTunnelClosed()) scheduleCloseCheck(old);
        }, 4, TimeUnit.SECONDS);
    }

    private void stopTunnel(boolean requested) {
        if (liveService != null && liveService != this && session == null) return;
        boolean wasLive = session != null;
        String recorded = readVpnState();
        boolean wasActive = STATE_CONNECTING.equals(recorded) || STATE_CONNECTED.equals(recorded);
        setTunnelLive(false);
        retireTunnel();
        if (wasActive || requested) {
            if (!previousTunnelClosed()) {
                writeVpnState(STATE_ERROR, "tunnel-closing");
                broadcastTunnelEvent(new Intent(ACTION_TUNNEL_ERROR).putExtra(EXTRA_ERROR_MESSAGE, "tunnel-closing"));
                return;
            }
            writeVpnState(STATE_DISCONNECTED, null);
        }
        // Receivers can now reread the terminal state and the cleared app selection together.
        if (wasLive || wasActive || requested) broadcastTunnelEvent(new Intent(ACTION_TUNNEL_DOWN));
    }

    @Override
    public synchronized void onRevoke() {
        if (ended) return;
        ended = true;
        String revoked = java.util.UUID.randomUUID().toString();
        supersedeUiCommand(getSharedPreferences(PREFS_NAME, MODE_PRIVATE), revoked);
        cancelScheduledRestart();
        submitLifecycle(() -> {
            executingUiCommand = revoked;
            writeVpnState(STATE_ERROR, "no-permission");
            stopTunnel(false);
            broadcastTunnelEvent(new Intent(ACTION_TUNNEL_ERROR).putExtra(EXTRA_ERROR_MESSAGE, "no-permission"));
        });
        super.onRevoke();
    }

    @Override
    public synchronized void onDestroy() {
        ended = true;
        releaseScreenStateReceiver();
        removeForegroundNotification();
        submitLifecycle(() -> {
            stopTunnel(false);
            quitNetCallbackThread();
        });
        super.onDestroy();
    }

    @Override
    public synchronized void onTaskRemoved(Intent rootIntent) {
        try {
            if (!ended && !stopRequested && commandServer != null && isIgnoringBatteryOptimizations()) {
                String ticket = java.util.UUID.randomUUID().toString();
                getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().putString(RESTART_TICKET, ticket).apply();
                Intent restart = new Intent(getApplicationContext(), VpnRouterService.class)
                        .setAction(ACTION_RESTART).putExtra(RESTART_TICKET, ticket);
                PendingIntent pi = PendingIntent.getService(
                        this, 1, restart,
                        RESTART_FLAGS | PendingIntent.FLAG_UPDATE_CURRENT);
                AlarmManager am = (AlarmManager) getSystemService(ALARM_SERVICE);
                if (am != null && pi != null) {
                    am.set(AlarmManager.ELAPSED_REALTIME_WAKEUP,
                            SystemClock.elapsedRealtime() + 1500L, pi);
                    Log.i(LOG_TAG, "AND-NODOZE: onTaskRemoved — tunnel active + "
                            + "battery-exempt; scheduled restart in 1.5s");
                }
            } else if (commandServer != null) {
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
        getSharedPreferences(PREFS_NAME, MODE_PRIVATE).edit().remove(RESTART_TICKET).apply();
        try {
            Intent restart = new Intent(getApplicationContext(), VpnRouterService.class)
                    .setAction(ACTION_RESTART);
            PendingIntent pi = PendingIntent.getService(
                    this, 1, restart,
                    RESTART_FLAGS | PendingIntent.FLAG_NO_CREATE);
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
                .setSmallIcon(statusIconResource())
                .setOngoing(true)
                .addAction(android.R.drawable.ic_menu_close_clear_cancel, notifDisconnect, stopPi)
                .build();
    }

    private int statusIconResource() {
        int id = getResources().getIdentifier("ic_stat_vpn", "drawable", getPackageName());
        return id != 0 ? id : android.R.drawable.ic_lock_idle_lock;
    }

    private ParcelFileDescriptor establishTun(TunOptions options, String mode, String[] packages) throws Exception {
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

        if (AndroidTunnelPolicy.usesVpnDns(options.getDNSMode().getValue())) {
            StringIterator dnsServers = options.getDNSServerAddress();
            if (dnsServers == null || !dnsServers.hasNext()) {
                throw new IllegalArgumentException("No VPN DNS server was supplied");
            }
            while (dnsServers.hasNext()) builder.addDnsServer(dnsServers.next());
        }

        boolean isInclude = "include".equalsIgnoreCase(mode);
        boolean isExclude = "exclude".equalsIgnoreCase(mode);

        List<String> profileIncludes = packageList(options.getIncludePackage());
        List<String> profileExcludes = packageList(options.getExcludePackage());
        if (!profileIncludes.isEmpty() && !profileExcludes.isEmpty()) {
            throw new IllegalArgumentException("TUN cannot combine included and excluded applications");
        }
        List<String> selected = packages == null
                ? java.util.Collections.emptyList() : java.util.Arrays.asList(packages);
        java.util.Set<String> applied = new java.util.TreeSet<>();
        boolean allowlist = isInclude || (!isExclude && !profileIncludes.isEmpty());
        if (allowlist) {
            AndroidTunnelPolicy.requireAllowedPackage(isInclude ? selected : profileIncludes,
                    pkg -> addPackage(builder, pkg, true, applied));
        } else {
            Iterable<String> excluded = isExclude ? selected : profileExcludes;
            AndroidTunnelPolicy.requireExcludedPackages(excluded, pkg -> addPackage(builder, pkg, false, applied));
            builder.addDisallowedApplication(getPackageName());
        }

        // The profile can supply its own packages when the picker is off. Keep what the
        // builder accepted separate from the picker selection submitted for this session.
        String activeMode = allowlist ? "include" : (isExclude || !applied.isEmpty() ? "exclude" : "off");
        String snapshot = new org.json.JSONObject()
                .put("ActiveMode", activeMode)
                .put("ActivePackages", new org.json.JSONArray(applied))
                .put("RequestedMode", mode == null ? "off" : mode)
                .put("RequestedPackages", new org.json.JSONArray(selected)).toString();
        requireMainStartPermitted();
        ParcelFileDescriptor pfd = builder.establish();
        if (pfd == null) {
            throw new Exception("VpnService.Builder.establish returned null");
        }
        establishedAppPolicy = snapshot;
        return pfd;
    }

    private void requireMainStartPermitted() {
        if (ended || stopRequested || !LibboxRuntime.mainStartPermitted()) throw new IllegalStateException("tunnel-closing");
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

    private static List<String> packageList(StringIterator iter) {
        List<String> packages = new ArrayList<>();
        while (iter != null && iter.hasNext()) packages.add(iter.next());
        return packages;
    }

    private static boolean addPackage(Builder builder, String pkg, boolean allow, java.util.Set<String> applied) {
        try {
            if (allow) builder.addAllowedApplication(pkg);
            else builder.addDisallowedApplication(pkg);
            applied.add(pkg);
            return true;
        } catch (PackageManager.NameNotFoundException e) {
            return false;
        }
    }

    private static final class VpnRouterPlatformInterface extends LibboxRuntime.PlatformDefaults {
        private final VpnRouterService service;
        private final AndroidTunnelSession session;
        private final String mode;
        private final String[] packages;

        private InterfaceUpdateListener defaultListener;
        private ConnectivityManager.NetworkCallback defaultCallback;
        private boolean firstUpdateFired = false;

        VpnRouterPlatformInterface(VpnRouterService service, AndroidTunnelSession session, String mode, String[] packages) {
            this.service = service;
            this.session = session;
            this.mode = mode;
            this.packages = packages == null ? null : packages.clone();
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            ParcelFileDescriptor pfd = session.openTun(() -> {
                service.requireMainStartPermitted();
                return service.establishTun(options, mode, packages);
            }, ParcelFileDescriptor::getFileDescriptor);
            service.requireMainStartPermitted();
            return pfd.getFd();
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
