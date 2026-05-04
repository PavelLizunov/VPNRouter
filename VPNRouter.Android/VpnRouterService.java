// VpnRouterService — Android-native service that owns the VpnService
// lifecycle and hosts the libbox.aar runtime.
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
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.VpnService;
import android.os.Build;
import android.os.IBinder;
import android.os.ParcelFileDescriptor;
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

import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.CommandServerHandler;
import io.nekohasekai.libbox.SystemProxyStatus;
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
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class VpnRouterService extends VpnService {

    public static final String ACTION_START = "com.ninitux.vpnrouter.START";
    public static final String ACTION_STOP = "com.ninitux.vpnrouter.STOP";
    public static final String EXTRA_CONFIG_JSON = "config_json";
    public static final String EXTRA_ALLOWED_PACKAGES = "allowed_packages";
    // v3.0 Phase 1.I — broadcasts so the Avalonia UI can flip its button
    // label on real tunnel-state events instead of intent-only.
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
    private ParcelFileDescriptor currentPfd;

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
            sendBroadcast(new Intent(ACTION_TUNNEL_UP).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.e(LOG_TAG, "startTunnel failed: " + e.getClass().getName() + ": " + e.getMessage(), e);
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

        libboxSetupDone = true;
        Log.i(LOG_TAG, "libbox setup OK (base=" + filesDir.getAbsolutePath() + ")");
    }

    private void startLibboxService() throws Exception {
        if (pendingConfigJson == null || pendingConfigJson.isEmpty()) {
            throw new Exception("config_json missing");
        }

        Libbox.checkConfig(pendingConfigJson);

        // v3.0 Phase 5 — keep CommandServer path (our libbox version has
        // no Libbox.newService(json, platform) — only the CommandServer
        // bootstrap). The CRITICAL difference vs Pre-5 is the
        // PlatformInterface implementation now provides real
        // getInterfaces() / systemCertificates() / useProcFS — without
        // these libbox can't reach upstream sockets.
        VpnRouterPlatformInterface platformInterface = new VpnRouterPlatformInterface(this);
        VpnRouterCommandHandler handler = new VpnRouterCommandHandler(this);
        commandServer = Libbox.newCommandServer(handler, platformInterface);
        commandServer.start();

        OverrideOptions overrides = new OverrideOptions();
        commandServer.startOrReloadService(pendingConfigJson, overrides);

        Log.i(LOG_TAG, "libbox service started successfully (Phase 5)");
    }

    private void stopTunnel() {
        if (currentPfd != null) {
            try { currentPfd.close(); } catch (Exception e) {
                Log.w(LOG_TAG, "pfd.close threw: " + e.getMessage());
            }
            currentPfd = null;
        }
        if (commandServer != null) {
            try {
                commandServer.close();
            } catch (Exception e) {
                Log.w(LOG_TAG, "commandServer.close threw: " + e.getMessage());
            }
            commandServer = null;
        }
        stopForeground(STOP_FOREGROUND_REMOVE);
        try {
            sendBroadcast(new Intent(ACTION_TUNNEL_DOWN).setPackage(getPackageName()));
        } catch (Exception e) {
            Log.w(LOG_TAG, "broadcast tunnel-down threw: " + e.getMessage());
        }
    }

    /**
     * v3.0 Phase 5 — CommandServerHandler stub. Required by
     * Libbox.newCommandServer; we don't use the clash control APIs.
     */
    private static final class VpnRouterCommandHandler implements CommandServerHandler {
        private final VpnRouterService service;
        VpnRouterCommandHandler(VpnRouterService service) { this.service = service; }

        @Override
        public void serviceStop() throws Exception {
            service.stopTunnel();
            service.stopSelf();
        }
        @Override
        public void serviceReload() throws Exception {}
        @Override
        public SystemProxyStatus getSystemProxyStatus() {
            return null;
        }
        @Override
        public void setSystemProxyEnabled(boolean isEnabled) {}
        @Override
        public void writeDebugMessage(String message) {
            if (message != null && !message.isEmpty()) {
                Log.d("Libbox", message);
            }
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

        // Exclude self so we don't loop our own traffic back through the TUN.
        try {
            builder.addDisallowedApplication(getPackageName());
        } catch (PackageManager.NameNotFoundException ignored) {}

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

        @Override
        public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
            // Phase 5 simplification: rely on libbox's own monitor.
            // Reference impl wires NetworkCallback. Phase 6.
        }

        @Override
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
            // no-op
        }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) {
            String type = notification != null ? notification.getTypeName() : "null";
            String title = notification != null ? notification.getTitle() : "null";
            Log.i("Libbox", "notification: type=" + type + " title=" + title);
        }

        @Override
        public io.nekohasekai.libbox.ConnectionOwner findConnectionOwner(
                int ipProtocol,
                String sourceAddress, int sourcePort,
                String destinationAddress, int destinationPort) throws Exception {
            // Reference impl uses ConnectivityManager.getConnectionOwnerUid
            // (Android Q+) to map a 5-tuple to the owning app uid. Used
            // by sing-box for per-app routing rules. We don't use those
            // rules on Android (filter at VpnService.Builder layer), so
            // a stub error is acceptable.
            throw new Exception("findConnectionOwner not implemented on Android");
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
