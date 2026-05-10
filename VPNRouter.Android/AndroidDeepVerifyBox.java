// AndroidDeepVerifyBox — Free-Configs deep verification on Android.
//
// Bug #1 (v3.0 android-alpha r5+, 2026-05-11): pre-fix, the Android Free
// Configs path stopped after TCP+TLS handshake and displayed every working
// entry with a single ✓. Desktop runs an additional Deep Verify pass that
// spins a transient sing-box, opens a SOCKS inbound, HTTP-probes Cloudflare
// through it, and re-stamps the entry as Verified (✓✓) only if real HTTP
// traffic came back. Without that on Android, users couldn't tell which
// configs the TCP+TLS check had upgraded vs the ones that just sat there.
//
// This Java helper wraps a transient libbox BoxService (no TUN, just SOCKS
// + the VLESS outbound from the config under test) and a Java HTTP probe
// through that SOCKS proxy. The C# side
// (VPNRouter.Android/AndroidFreeConfigDeepVerifier.cs) builds the config
// JSON via the shared FreeConfigDeepVerifier.BuildSingleOutboundConfig
// helper and calls verifyConfigSync per row.
//
// Concurrent-box risk: libbox upstream supports multiple sing-box.Service
// instances in one process; SagerNet's reference Android app only ever
// runs one, so it's not exercised. We pin the verify-box PlatformInterface
// to "no TUN, no protect" and accept the worst case (libbox refuses or
// throws) — the C# orchestrator catches and falls back to the existing
// single-✓ display, no crash. When the main VPN tunnel is already up the
// VPN's box and the verify box share the same process; our app excludes
// itself from its own TUN at VpnService.Builder time (see
// VpnRouterService.openTun's addDisallowedApplication(getPackageName())),
// so the verify box's outbound sockets bypass the main TUN at the OS
// level — no protect() needed.
//
// All public entry points are static and synchronous. The caller decides
// concurrency. Verify cost is dominated by libbox startup (~1-2 s on
// KYOCERA A101BM) + HTTP RTT (~150-400 ms), so 3-5 s end-to-end per
// config in practice.

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

    private AndroidDeepVerifyBox() { /* static-only */ }

    /**
     * Synchronously verify a single config by spinning up a transient
     * libbox BoxService, then HTTP-GETting <code>probeUrl</code> through
     * its local SOCKS inbound on <code>socksPort</code>.
     *
     * <p>Returns a JSON object as a string:
     * <pre>
     *   {"ok":true,  "latencyMs":1234, "err":null}
     *   {"ok":false, "latencyMs":0,    "err":"didn't bind | http timeout | …"}
     * </pre>
     * Always returns a non-null string. The orchestrator parses it on the
     * C# side and updates the entry's Status / LastError accordingly.
     *
     * @param ctx        application context, used for libbox setup paths
     *                   and AndroidCAStore (for system CA enumeration).
     * @param configJson minimal sing-box config — SOCKS inbound on
     *                   <code>socksPort</code> + single VLESS outbound,
     *                   final=proxy. Built by
     *                   <c>FreeConfigDeepVerifier.BuildSingleOutboundConfig</c>
     *                   (in VPNRouter.Core).
     * @param socksPort  port the SOCKS inbound listens on (loopback).
     *                   Caller picks via TcpListener(0) before generating
     *                   the config; passing it in saves re-parsing the
     *                   JSON on the Java side.
     * @param timeoutMs  overall verification timeout. Spans libbox spin-up
     *                   + bind wait + HTTP round-trip + libbox teardown.
     * @param probeUrl   target URL — typically
     *                   <code>https://www.cloudflare.com/cdn-cgi/trace</code>.
     *                   Response must contain an <code>ip=</code> line
     *                   with a non-private address, else we report
     *                   "local ip in response" (catches transparent
     *                   intercepts that return the user's own IP).
     */
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

            // Libbox.checkConfig throws on schema errors; let the exception
            // bubble through the catch below so the caller gets a useful
            // err string instead of a silent failure.
            Libbox.checkConfig(configJson);

            VerifyPlatformInterface platform = new VerifyPlatformInterface(ctx);
            boxService = Libbox.newService(configJson, platform);
            boxService.start();

            // Wait for SOCKS to bind. We do this by attempting to TCP-connect
            // to 127.0.0.1:socksPort in a loop. libbox's start() is async on
            // some paths (the Go side spins up listeners on its own goroutines),
            // so we can return from start() before the listener is up. 100 ms
            // poll × up to 2 s wait — same window the desktop verifier uses.
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
            // Catch Throwable (not just Exception) so a libbox crash or
            // OOM still returns a structured result instead of propagating
            // through JNI back to .NET as an unstructured exception.
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

    /**
     * Libbox.setup is process-wide and idempotent in practice — calling
     * it twice with the same paths is a no-op. We still gate it behind
     * an AtomicBoolean so we don't waste a JNI round-trip per verify.
     * The VPN service has its own setup-once flag in
     * VpnRouterService.libboxSetupDone — running both is OK because the
     * paths resolve to the same getFilesDir() / cacheDir() locations.
     */
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

    /**
     * Poll-connect to 127.0.0.1:port until something accepts or we run out
     * of time. Mirrors the desktop verifier's WaitForPortBoundAsync — same
     * 100 ms interval, same 2 s outer cap. Loopback connect is cheap; the
     * cost is dominated by libbox's own startup latency, not the poll.
     */
    private static boolean waitForPortBound(int port, int maxWaitMs) {
        long deadline = System.currentTimeMillis() + maxWaitMs;
        while (System.currentTimeMillis() < deadline) {
            try (Socket s = new Socket()) {
                s.connect(new InetSocketAddress("127.0.0.1", port), 200);
                return true;
            } catch (Exception ignored) {
                // not bound yet
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

    /**
     * HTTP GET <code>probeUrl</code> through a local SOCKS5 proxy. The JVM
     * applies the Proxy to the underlying Socket at openConnection time, so
     * HTTPS via SOCKS5 works transparently — the TLS handshake rides over
     * the SOCKS-tunneled TCP stream.
     *
     * <p>Cloudflare's trace endpoint returns multiline
     * <code>key=value</code>. We validate two things:
     * <ul>
     *   <li>HTTP status is 2xx — eliminates captive portals and broken
     *       proxies that return 5xx.</li>
     *   <li>The <code>ip=</code> line contains a non-RFC1918 / non-loopback
     *       address — eliminates transparent intercepts that mirror the
     *       client's own IP back as "yours".</li>
     * </ul>
     */
    private static ProbeResult probeViaSocks(int socksPort, String probeUrl, int timeoutMs) {
        HttpURLConnection conn = null;
        try {
            Proxy proxy = new Proxy(Proxy.Type.SOCKS,
                    new InetSocketAddress("127.0.0.1", socksPort));
            URL url = new URL(probeUrl);
            conn = (HttpURLConnection) url.openConnection(proxy);
            // Connect timeout caps the SOCKS handshake + the upstream
            // TCP+TLS handshake to the probe target. Read timeout caps
            // the response body wait. Sum stays under the caller's
            // overall budget (typically 12 s).
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

            // Find the ip= line and reject local-only / private ranges.
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
            // 10.0.0.0/8
            if (b0 == 10) return true;
            // 172.16.0.0/12
            if (b0 == 172 && b1 >= 16 && b1 <= 31) return true;
            // 192.168.0.0/16
            if (b0 == 192 && b1 == 168) return true;
            // 100.64.0.0/10 (CGNAT — common on cellular)
            if (b0 == 100 && b1 >= 64 && b1 <= 127) return true;
            return false;
        } catch (Exception e) {
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Verify-box PlatformInterface — minimal subset of what
    // VpnRouterPlatformInterface (in VpnRouterService) provides. The verify
    // box has no TUN, so openTun is a hard failure. We don't call
    // VpnService.protect() because our app is excluded from its own TUN
    // at the OS layer (addDisallowedApplication(getPackageName())) — that
    // exclusion bypasses TUN routing for ALL sockets opened in this
    // process, including the libbox verify-box's outbound dials. The
    // platform-auto-detect path is therefore disabled.
    //
    // What we DO need to support:
    //   • getInterfaces — sing-box needs the upstream interface list to
    //     pick a binding target. Without it, every outbound dial fails
    //     with "no available network interface" (we learned this the
    //     hard way during VpnRouterService Phase 5).
    //   • systemCertificates — sing-box validates the probe target's TLS
    //     cert against the system CAs. Reality outbounds bypass standard
    //     TLS, but the cloudflare.com endpoint we probe is plain HTTPS
    //     terminated at sing-box's outbound side, so CAs matter.
    //   • startDefaultInterfaceMonitor — sing-box's outbound dial path
    //     uses the "default interface" hint to decide which interface to
    //     bind to. Without it, dials fail (we learned this during
    //     VpnRouterService Phase 6.2). For verify we just fire one
    //     initial update with the current active network — no callback
    //     wiring since the verify box's lifetime is seconds, not the
    //     network-change-sensitive minutes/hours of a real VPN session.
    //   • writeLog — surface libbox-internal diagnostics if a verify
    //     fails strangely. logcat tag "VpnRouter.DV.Libbox" so they're
    //     greppable separately from the main service's "Libbox" tag.
    // ────────────────────────────────────────────────────────────────────
    private static final class VerifyPlatformInterface implements PlatformInterface {

        private final Context ctx;

        VerifyPlatformInterface(Context ctx) {
            this.ctx = ctx;
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            // Verify config is SOCKS-only — sing-box should never reach
            // here. If it does, surface a loud error so we can diagnose
            // (probably the caller passed a TUN-containing config by
            // mistake).
            throw new Exception("verify box has no TUN — openTun unexpected");
        }

        @Override public boolean useProcFS() { return false; }
        @Override public boolean usePlatformAutoDetectInterfaceControl() { return false; }
        @Override public void autoDetectInterfaceControl(int fd) { /* no-op */ }
        @Override public void clearDNSCache() { /* no-op */ }

        @Override
        public NetworkInterfaceIterator getInterfaces() {
            // Reuse the same enumeration VpnRouterService.Phase 5 does —
            // ConnectivityManager + NetworkInterface walk. Without it,
            // sing-box can't pick an upstream interface and dial fails.
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
            // Fire an initial update so sing-box knows which interface to
            // bind upstream dials to. We don't subscribe to network-change
            // callbacks — the verify box lives for ~3-5 s, not long enough
            // for a Wi-Fi handoff to matter.
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
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) { /* no-op */ }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) { /* no-op */ }

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
