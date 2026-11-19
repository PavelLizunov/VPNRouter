package com.ninitux.vpnrouter;

import android.content.Context;
import android.util.Log;

import java.util.ArrayList;
import java.util.Iterator;
import java.util.List;

import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.InterfaceUpdateListener;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.LocalDNSTransport;
import io.nekohasekai.libbox.NetworkInterfaceIterator;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.TunOptions;
import io.nekohasekai.libbox.WIFIState;

public final class AndroidDeepVerifyBox {

    private static final String LOG_TAG = "VpnRouter.DV";

    private final NativeEngineSlot.Session<CommandServer> session;

    private AndroidDeepVerifyBox(NativeEngineSlot.Session<CommandServer> session) {
        this.session = session;
    }

    public static AndroidDeepVerifyBox begin(Context ctx, String configJson) {
        return new AndroidDeepVerifyBox(LibboxRuntime.beginVerification(ctx, configJson, new VerifyPlatformInterface(ctx)));
    }

    public void awaitReady() throws Exception { session.awaitReady(8_000L); }

    public void cancel() { session.cancel(); }

    public void close() {
        if (!session.close(4_000L)) throw new IllegalStateException("Native engine cleanup pending");
    }

    private static final class VerifyPlatformInterface extends LibboxRuntime.PlatformDefaults {

        private final Context ctx;

        VerifyPlatformInterface(Context ctx) {
            this.ctx = ctx;
        }

        @Override
        public int openTun(TunOptions options) throws Exception {
            throw new Exception("verify box has no TUN — openTun unexpected");
        }

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
                Log.w(LOG_TAG, "Network interfaces unavailable");
                return null;
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
                Log.w(LOG_TAG, "Default interface monitor unavailable");
            }
        }

        @Override
        public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) { }

        @Override
        public void sendNotification(io.nekohasekai.libbox.Notification notification) { }

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
