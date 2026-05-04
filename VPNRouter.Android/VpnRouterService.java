// VpnRouterService — Android-native service that owns the VpnService
// lifecycle and hosts the libbox.aar runtime.
//
// v3.0 Android Phase 1.C (2026-04-30) — Java port of the design that
// was originally drafted in C#. Keeping the libbox-touching code in
// Java sidesteps the Mono.Android binding generator (Bind="true" on
// libbox.aar triggered a Mono GC-bridge abort during application init).
// Java sources are first-class citizens in .NET Android via
// <AndroidJavaSource>; libbox's own Java types are imported here
// natively (libbox.aar is on the classpath) so no C# bindings are
// required at all on the libbox side.
//
// The C# UI (Avalonia) talks to this service via Intents — the
// contract:
//   ACTION_START + EXTRA_CONFIG_JSON + EXTRA_ALLOWED_PACKAGES
//   ACTION_STOP
//
// Reference impl: sagernet/sing-box-for-android —
// app/src/main/java/io/nekohasekai/sfa/bg/BoxService.kt

package com.ninitux.vpnrouter;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.VpnService;
import android.os.Build;
import android.os.IBinder;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import androidx.core.app.NotificationCompat;

import java.io.File;
import java.util.Iterator;
import java.util.NoSuchElementException;

import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.CommandServerHandler;
import io.nekohasekai.libbox.ConnectionOwner;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.OverrideOptions;
import io.nekohasekai.libbox.PlatformInterface;
import io.nekohasekai.libbox.RoutePrefix;
import io.nekohasekai.libbox.RoutePrefixIterator;
import io.nekohasekai.libbox.SetupOptions;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.SystemProxyStatus;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class VpnRouterService extends VpnService {

    public static final String ACTION_START = "com.ninitux.vpnrouter.START";
    public static final String ACTION_STOP = "com.ninitux.vpnrouter.STOP";
    public static final String EXTRA_CONFIG_JSON = "config_json";
    public static final String EXTRA_ALLOWED_PACKAGES = "allowed_packages";
    // v3.0 Phase 1.I (2026-05-04) — broadcasts so the Avalonia UI can flip
    // its button label on real tunnel-state events instead of intent-only.
    public static final String ACTION_TUNNEL_UP = "com.ninitux.vpnrouter.TUNNEL_UP";
    public static final String ACTION_TUNNEL_DOWN = "com.ninitux.vpnrouter.TUNNEL_DOWN";
    public static final String ACTION_TUNNEL_ERROR = "com.ninitux.vpnrouter.TUNNEL_ERROR";
    public static final String EXTRA_ERROR_MESSAGE = "error_message";

    private static final int NOTIFICATION_ID = 100;
    private static final String NOTIFICATION_CHANNEL_ID = "vpnrouter_tunnel";
    private static final String LOG_TAG = "VpnRouter";

    private static boolean libboxSetupDone = false;

    private String pendingConfigJson;
    private String[] pendingAllowedPackages;
    private CommandServer commandServer;

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent != null ? intent.getAction() : null;
        if (ACTION_START.equals(action)) {
            pendingConfigJson = intent.getStringExtra(EXTRA_CONFIG_JSON);
            pendingAllowedPackages = intent.getStringArrayExtra(EXTRA_ALLOWED_PACKAGES);
            startTunnel();
        } else if (ACTION_STOP.equals(action)) {
            stopTunnel();
            stopSelf();
        }
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void startTunnel() {
        startForeground(NOTIFICATION_ID, buildNotification());

        try {
            ensureLibboxSetup();
            startLibboxService();
            // v3.0 Phase 1.I — broadcast tunnel-up so UI flips to "Connected"
            // on REAL state, not just intent.
            sendBroadcast(new Intent(ACTION_TUNNEL_UP).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.e(LOG_TAG, "startTunnel failed: " + e.getClass().getName() + ": " + e.getMessage(), e);
            // Phase 1.I — let the UI know we failed so it can revert the
            // optimistic "Connected" intent state.
            Intent err = new Intent(ACTION_TUNNEL_ERROR).setPackage(getPackageName());
            err.putExtra(EXTRA_ERROR_MESSAGE, e.getClass().getSimpleName() + ": " + e.getMessage());
            sendBroadcast(err);
            stopSelf();
        }
    }

    /**
     * Initialise libbox once per process. Sets up the working / base /
     * temp paths so the Go side can write its caches and logs.
     */
    private synchronized void ensureLibboxSetup() throws Exception {
        if (libboxSetupDone) {
            return;
        }
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

        libboxSetupDone = true;
        Log.i(LOG_TAG, "libbox setup OK (base=" + filesDir.getAbsolutePath() +
                " working=" + workingDir.getAbsolutePath() +
                " temp=" + cacheDir.getAbsolutePath() + ")");
    }

    private void startLibboxService() throws Exception {
        if (pendingConfigJson == null || pendingConfigJson.isEmpty()) {
            Log.e(LOG_TAG, "config_json missing — cannot start tunnel");
            stopSelf();
            return;
        }

        // Validate config — libbox throws with parser error on bad JSON.
        try {
            Libbox.checkConfig(pendingConfigJson);
        } catch (Exception e) {
            Log.e(LOG_TAG, "libbox.checkConfig rejected config: " + e.getMessage());
            throw e;
        }

        VpnRouterPlatformInterface platformInterface = new VpnRouterPlatformInterface(this);
        VpnRouterCommandHandler handler = new VpnRouterCommandHandler(this);
        commandServer = new CommandServer(handler, platformInterface);
        commandServer.start();
        // OverrideOptions MUST be non-null — libbox panics with
        // nil-pointer dereference at command_server.go:175 otherwise.
        // We instantiate it with defaults (no per-app override here;
        // include/exclude packages are passed at the VpnService.Builder
        // layer in openTun()).
        OverrideOptions overrides = new OverrideOptions();
        commandServer.startOrReloadService(pendingConfigJson, overrides);
        Log.i(LOG_TAG, "libbox service started successfully");
    }

    private void stopTunnel() {
        if (commandServer != null) {
            // v3.0 Phase 1.I (2026-05-04) — order matters here. Reference
            // impl in sing-box-for-android (BoxService.kt) calls close()
            // directly without closeService() first; closeService() throws
            // "invalid argument" on Android 12+ when the libbox service
            // is mid-startup or already closing. Skip closeService and
            // rely on close() to tear down both the command server and
            // the underlying sing-box service in one step.
            try {
                commandServer.close();
            } catch (Exception e) {
                Log.w(LOG_TAG, "commandServer.close threw: " + e.getMessage());
            }
            commandServer = null;
        }
        stopForeground(STOP_FOREGROUND_REMOVE);
        // v3.0 Phase 1.I — broadcast tunnel-down so UI flips to
        // "Disconnected" on real state.
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
            if (nm != null) {
                nm.createNotificationChannel(channel);
            }
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
     * Open the TUN device given the libbox-supplied options. We translate
     * those into a VpnService.Builder, addAddress / addRoute / addDnsServer
     * calls, then return the underlying file descriptor for libbox to
     * read/write packets through.
     */
    int openTun(TunOptions options) throws Exception {
        Builder builder = new Builder()
                .setSession("VPNRouter")
                .setMtu(options.getMTU());

        addPrefixesAsAddresses(builder, options.getInet4Address());
        addPrefixesAsAddresses(builder, options.getInet6Address());

        boolean any4 = addPrefixesAsRoutes(builder, options.getInet4RouteAddress());
        if (!any4) {
            builder.addRoute("0.0.0.0", 0);
        }
        boolean any6 = addPrefixesAsRoutes(builder, options.getInet6RouteAddress());
        if (!any6) {
            builder.addRoute("::", 0);
        }

        boolean dnsAdded = false;
        try {
            String dns = options.getDNSServerAddress() != null
                    ? options.getDNSServerAddress().getValue()
                    : null;
            if (dns != null && !dns.isEmpty()) {
                builder.addDnsServer(dns);
                dnsAdded = true;
            }
        } catch (Exception ignored) {
        }
        if (!dnsAdded) {
            builder.addDnsServer("1.1.1.1");
        }

        addPackages(builder, options.getIncludePackage(), true);
        addPackages(builder, options.getExcludePackage(), false);

        // Exclude self so we don't loop our own traffic back through the TUN.
        try {
            builder.addDisallowedApplication(getPackageName());
        } catch (PackageManager.NameNotFoundException ignored) {
        }

        ParcelFileDescriptor pfd = builder.establish();
        if (pfd == null) {
            throw new Exception("VpnService.Builder.establish returned null");
        }
        return pfd.detachFd();
    }

    private static void addPrefixesAsAddresses(Builder builder, RoutePrefixIterator iter) {
        if (iter == null) return;
        while (iter.hasNext()) {
            RoutePrefix p = iter.next();
            if (p == null) continue;
            String addr = p.address();
            if (addr != null && !addr.isEmpty()) {
                builder.addAddress(addr, p.prefix());
            }
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
                if (allow) {
                    builder.addAllowedApplication(pkg);
                } else {
                    builder.addDisallowedApplication(pkg);
                }
            } catch (PackageManager.NameNotFoundException ignored) {
            }
        }
    }

    /**
     * Minimal libbox PlatformInterface — handles TUN open + a few
     * housekeeping callbacks. Most callbacks return null/throw and let
     * libbox use its built-in defaults.
     */
    private static final class VpnRouterPlatformInterface implements PlatformInterface {
        private final VpnRouterService service;

        VpnRouterPlatformInterface(VpnRouterService service) {
            this.service = service;
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            return service.openTun(options);
        }

        @Override
        public boolean useProcFS() {
            return false;
        }

        @Override
        public boolean usePlatformAutoDetectInterfaceControl() {
            return true;
        }

        /**
         * v3.0 Phase 3 (2026-05-04) — P0 fix.
         *
         * libbox calls this for every socket the sing-box runtime opens to
         * reach an upstream server (the VLESS endpoint, the
         * direct-outbound for split-tunnel, the DNS DoH endpoints, etc).
         * Pre-3 we were a no-op, which meant those sockets had NO protect()
         * marker and the kernel sent them BACK through our own VpnService
         * TUN → infinite loop → all upstream sockets timed out → DNS
         * resolution + every routed connection failed.
         *
         * <p>Symptom user reported 2026-05-04: "VPN through the app
         * doesn't work" + ERR_NAME_NOT_RESOLVED across all apps in
         * logcat (YouTube Music, Auth, Yandex Maps, etc).</p>
         *
         * <p>The fix is the canonical sagernet/sing-box-for-android
         * pattern: forward to {@code VpnService.protect(int)}, which
         * marks the fd as exempt from VPN routing rules so the kernel
         * uses the underlying physical interface (wlan0 / cellular)
         * for it.</p>
         */
        @Override
        public void autoDetectInterfaceControl(int fd) throws Exception {
            if (!service.protect(fd)) {
                throw new Exception("VpnService.protect(" + fd + ") failed");
            }
        }

        @Override
        public void clearDNSCache() {
            // no-op
        }

        @Override
        public NetworkInterfaceIterator getInterfaces() {
            return null;
        }

        @Override
        public StringIterator systemCertificates() {
            return null;
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
        public boolean includeAllNetworks() {
            return false;
        }

        @Override
        public boolean underNetworkExtension() {
            return false;
        }

        @Override
        public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) throws Exception {
            // libbox's built-in monitor handles this via NetworkCallback
        }

        @Override
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) throws Exception {
            // no-op
        }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) throws Exception {
            Log.i("Libbox", "notification: type=" + (notification != null ? notification.getTypeName() : "null") +
                    " title=" + (notification != null ? notification.getTitle() : "null"));
        }

        @Override
        public ConnectionOwner findConnectionOwner(int ipProtocol,
                                                   String sourceAddress, int sourcePort,
                                                   String destinationAddress, int destinationPort) throws Exception {
            // process_name routing is desktop-only; on Android the OS does
            // per-app routing at the VpnService.Builder layer.
            throw new Exception("findConnectionOwner not implemented on Android");
        }
    }

    /**
     * CommandServerHandler — libbox's CommandServer fires these when
     * something on the Clash API side wants to control the runtime.
     */
    private static final class VpnRouterCommandHandler implements CommandServerHandler {
        private final VpnRouterService service;

        VpnRouterCommandHandler(VpnRouterService service) {
            this.service = service;
        }

        @Override
        public void serviceStop() throws Exception {
            service.stopTunnel();
            service.stopSelf();
        }

        @Override
        public void serviceReload() throws Exception {
            // Phase 1.C: no hot reload from clash side.
        }

        @Override
        public SystemProxyStatus getSystemProxyStatus() throws Exception {
            return null;
        }

        @Override
        public void setSystemProxyEnabled(boolean enabled) throws Exception {
            // no-op
        }

        @Override
        public void writeDebugMessage(String message) {
            Log.d("Libbox", message != null ? message : "");
        }
    }
}
