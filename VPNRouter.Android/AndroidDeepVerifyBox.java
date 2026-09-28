package com.ninitux.vpnrouter;

import android.content.Context;
import android.util.Base64;
import android.util.Log;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.InetSocketAddress;
import java.net.Proxy;
import java.net.Socket;
import java.net.URL;
import java.security.KeyStore;
import java.security.cert.Certificate;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.Iterator;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;

import io.nekohasekai.libbox.BoxService;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.PlatformInterface;
import io.nekohasekai.libbox.SetupOptions;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class AndroidDeepVerifyBox {

    private static final String LOG_TAG = "VpnRouter.DV";
    private static final AtomicBoolean libboxSetupDone = new AtomicBoolean(false);

    private AndroidDeepVerifyBox() { }

    public static String verifyConfigSync(
            Context ctx,
            String configJson,
            int socksPort,
            int timeoutMs,
            String probeUrl) {
        long start = System.currentTimeMillis();
        BoxService boxService = null;
        try {
            ensureLibboxSetup(ctx);

            Libbox.checkConfig(configJson);

            VerifyPlatformInterface platform = new VerifyPlatformInterface(ctx);
            boxService = Libbox.newService(configJson, platform);
            boxService.start();

            if (!waitForPortBound(socksPort, 2000)) {
                return jsonError(0, "sing-box didn't bind");
            }

            long httpStart = System.currentTimeMillis();
            ProbeResult probe = probeViaSocks(socksPort, probeUrl, timeoutMs);
            int latencyMs = (int) (System.currentTimeMillis() - httpStart);

            if (probe.ok) {
                return jsonOk(latencyMs);
            } else {
                return jsonError(0, probe.err != null ? probe.err : "http failed");
            }
        } catch (Throwable t) {
            Log.w(LOG_TAG, "verifyConfigSync threw after "
                    + (System.currentTimeMillis() - start) + " ms: "
                    + t.getClass().getSimpleName() + ": " + t.getMessage());
            return jsonError(0, t.getClass().getSimpleName() + ": "
                    + (t.getMessage() != null ? t.getMessage() : "(no message)"));
        } finally {
            if (boxService != null) {
                try {
                    boxService.close();
                } catch (Throwable t) {
                    Log.w(LOG_TAG, "boxService.close threw: " + t.getMessage());
                }
            }
        }
    }

    private static String jsonOk(int latencyMs) {
        return "{\"ok\":true,\"latencyMs\":" + latencyMs + ",\"err\":null}";
    }

    private static String jsonError(int latencyMs, String err) {
        return "{\"ok\":false,\"latencyMs\":" + latencyMs
                + ",\"err\":\"" + escapeJsonString(err) + "\"}";
    }

    private static String escapeJsonString(String s) {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default:
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
            }
        }
        return sb.toString();
    }

    private static void ensureLibboxSetup(Context ctx) throws Exception {
        if (libboxSetupDone.get()) return;
        synchronized (libboxSetupDone) {
            if (libboxSetupDone.get()) return;

            File filesDir = ctx.getFilesDir();
            File workingDir = new File(filesDir, "data");
            File cacheDir = ctx.getCacheDir();
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

            libboxSetupDone.set(true);
            Log.i(LOG_TAG, "libbox setup OK (verify path, base="
                    + filesDir.getAbsolutePath() + ")");
        }
    }

    private static boolean waitForPortBound(int port, int maxWaitMs) {
        long deadline = System.currentTimeMillis() + maxWaitMs;
        while (System.currentTimeMillis() < deadline) {
            try (Socket s = new Socket()) {
                s.connect(new InetSocketAddress("127.0.0.1", port), 200);
                return true;
            } catch (Exception ignored) {
            }
            try { Thread.sleep(100); } catch (InterruptedException ignored) { return false; }
        }
        return false;
    }

    private static final class ProbeResult {
        final boolean ok;
        final String err;
        ProbeResult(boolean ok, String err) { this.ok = ok; this.err = err; }
    }

    private static ProbeResult probeViaSocks(int socksPort, String probeUrl, int timeoutMs) {
        HttpURLConnection conn = null;
        try {
            Proxy proxy = new Proxy(Proxy.Type.SOCKS,
                    new InetSocketAddress("127.0.0.1", socksPort));
            URL url = new URL(probeUrl);
            conn = (HttpURLConnection) url.openConnection(proxy);
            conn.setConnectTimeout(Math.min(timeoutMs, 5000));
            conn.setReadTimeout(Math.min(timeoutMs, 8000));
            conn.setUseCaches(false);
            conn.setInstanceFollowRedirects(false);
            conn.setRequestProperty("User-Agent", "VPNRouter-Android/DeepVerify");

            int code = conn.getResponseCode();
            if (code < 200 || code >= 300) {
                return new ProbeResult(false, "http " + code);
            }

            String body;
            try (InputStream is = conn.getInputStream()) {
                byte[] buf = new byte[4096];
                int n, total = 0;
                StringBuilder sb = new StringBuilder(2048);
                while ((n = is.read(buf)) > 0 && total < 8192) {
                    sb.append(new String(buf, 0, n, java.nio.charset.StandardCharsets.UTF_8));
                    total += n;
                }
                body = sb.toString();
            }

            if (!body.contains("ip=")) {
                return new ProbeResult(false, "bad response");
            }

            for (String line : body.split("\n")) {
                if (line.startsWith("ip=")) {
                    String ip = line.substring(3).trim();
                    if (isPrivateOrLoopback(ip)) {
                        return new ProbeResult(false, "local ip in response");
                    }
                    break;
                }
            }
            return new ProbeResult(true, null);
        } catch (java.net.SocketTimeoutException ste) {
            return new ProbeResult(false, "http timeout");
        } catch (java.io.IOException ioe) {
            String m = ioe.getMessage();
            if (m == null) m = ioe.getClass().getSimpleName();
            if (m.length() > 60) m = m.substring(0, 60);
            return new ProbeResult(false, "http: " + m);
        } catch (Exception e) {
            return new ProbeResult(false, e.getClass().getSimpleName());
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private static boolean isPrivateOrLoopback(String ipStr) {
        try {
            java.net.InetAddress ip = java.net.InetAddress.getByName(ipStr);
            if (ip.isLoopbackAddress() || ip.isAnyLocalAddress()) return true;
            byte[] b = ip.getAddress();
            if (b.length != 4) return false;
            int b0 = b[0] & 0xFF, b1 = b[1] & 0xFF;
            if (b0 == 10) return true;
            if (b0 == 172 && b1 >= 16 && b1 <= 31) return true;
            if (b0 == 192 && b1 == 168) return true;
            if (b0 == 100 && b1 >= 64 && b1 <= 127) return true;
            return false;
        } catch (Exception e) {
            return false;
        }
    }

    private static final class VerifyPlatformInterface implements PlatformInterface {

        private final Context ctx;

        VerifyPlatformInterface(Context ctx) {
            this.ctx = ctx;
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            throw new Exception("verify box has no TUN — openTun unexpected");
        }

        @Override public boolean useProcFS() { return false; }
        @Override public boolean usePlatformAutoDetectInterfaceControl() { return false; }
        @Override public void autoDetectInterfaceControl(int fd) { }
        @Override public void clearDNSCache() { }

        @Override
        public NetworkInterfaceIterator getInterfaces() {
            try {
                android.net.ConnectivityManager cm =
                        (android.net.ConnectivityManager) ctx.getSystemService(Context.CONNECTIVITY_SERVICE);
                if (cm == null) return null;

                android.net.Network[] networks = cm.getAllNetworks();
                List<java.net.NetworkInterface> sysIfaces;
                try {
                    sysIfaces = java.util.Collections.list(java.net.NetworkInterface.getNetworkInterfaces());
                } catch (Exception e) {
                    sysIfaces = new ArrayList<>();
                }

                List<io.nekohasekai.libbox.NetworkInterface> list = new ArrayList<>();
                for (android.net.Network net : networks) {
                    android.net.LinkProperties lp = cm.getLinkProperties(net);
                    android.net.NetworkCapabilities nc = cm.getNetworkCapabilities(net);
                    if (lp == null || nc == null) continue;
                    String ifName = lp.getInterfaceName();
                    if (ifName == null) continue;

                    java.net.NetworkInterface sysIface = null;
                    for (java.net.NetworkInterface si : sysIfaces) {
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
                    if (nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI)) {
                        t = Libbox.InterfaceTypeWIFI;
                    } else if (nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_CELLULAR)) {
                        t = Libbox.InterfaceTypeCellular;
                    } else if (nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_ETHERNET)) {
                        t = Libbox.InterfaceTypeEthernet;
                    } else {
                        t = Libbox.InterfaceTypeOther;
                    }
                    bi.setType(t);
                    bi.setIndex(sysIface.getIndex());
                    try { bi.setMTU(sysIface.getMTU()); } catch (Exception ignored) {}

                    List<String> addrs = new ArrayList<>();
                    for (java.net.InterfaceAddress ia : sysIface.getInterfaceAddresses()) {
                        java.net.InetAddress a = ia.getAddress();
                        String host = a.getHostAddress();
                        if (host == null) continue;
                        if (a instanceof java.net.Inet6Address) {
                            int pct = host.indexOf('%');
                            if (pct >= 0) host = host.substring(0, pct);
                        }
                        addrs.add(host + "/" + ia.getNetworkPrefixLength());
                    }
                    bi.setAddresses(new SimpleStringIterator(addrs));

                    int flags = 0;
                    if (nc.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                        flags = android.system.OsConstants.IFF_UP | android.system.OsConstants.IFF_RUNNING;
                    }
                    try {
                        if (sysIface.isLoopback()) flags |= android.system.OsConstants.IFF_LOOPBACK;
                        if (sysIface.isPointToPoint()) flags |= android.system.OsConstants.IFF_POINTOPOINT;
                        if (sysIface.supportsMulticast()) flags |= android.system.OsConstants.IFF_MULTICAST;
                    } catch (Exception ignored) {}
                    bi.setFlags(flags);

                    bi.setMetered(!nc.hasCapability(
                            android.net.NetworkCapabilities.NET_CAPABILITY_NOT_METERED));
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
                return new SimpleStringIterator(new ArrayList<String>());
            }
        }

        @Override public LocalDNSTransport localDNSTransport() { return null; }
        @Override public WIFIState readWIFIState() { return null; }
        @Override public boolean includeAllNetworks() { return false; }
        @Override public boolean underNetworkExtension() { return false; }

        @Override
        public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
            try {
                android.net.ConnectivityManager cm =
                        (android.net.ConnectivityManager) ctx.getSystemService(Context.CONNECTIVITY_SERVICE);
                if (cm == null || listener == null) return;
                android.net.Network active = cm.getActiveNetwork();
                if (active == null) return;
                android.net.LinkProperties lp = cm.getLinkProperties(active);
                if (lp == null) return;
                String name = lp.getInterfaceName();
                if (name == null || name.isEmpty()) return;
                int index = -1;
                try {
                    java.net.NetworkInterface ni = java.net.NetworkInterface.getByName(name);
                    if (ni != null) index = ni.getIndex();
                } catch (Exception ignored) {}
                listener.updateDefaultInterface(name, index, false, false);
            } catch (Exception e) {
                Log.w(LOG_TAG, "startDefaultInterfaceMonitor (verify) threw: " + e.getMessage());
            }
        }

        @Override
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) { }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) { }

        @Override
        public int findConnectionOwner(int ipProtocol, String sa, int sp, String da, int dp) {
            return -1;
        }

        @Override
        public void writeLog(String message) {
            if (message != null && !message.isEmpty()) {
                Log.d("VpnRouter.DV.Libbox", message);
            }
        }

        @Override
        public String packageNameByUid(int uid) {
            return "uid=" + uid;
        }

        @Override
        public int uidByPackageName(String packageName) {
            return -1;
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
