package com.ninitux.vpnrouter;

/**
 * JNI binding to libslipstream_jni.so — the in-process Slipstream DNS-tunnel
 * client (Rust/picoquic, QUIC-over-DNS). Built from Mygod/slipstream-rust +
 * our slipstream-android JNI crate, bundled per-ABI in the APK's lib/ dir.
 *
 * <p>Runs under the VPNRouter UID inside {@code VpnRouterService}, so its
 * resolver UDP:53 queries bypass the TUN automatically (the service's
 * {@code addDisallowedApplication(getPackageName())} self-exclusion). No extra
 * loop-avoidance code is needed.</p>
 *
 * <p>Lifecycle: when the active server is dns-tunnel, the service calls
 * {@link #nativeStart} BEFORE starting libbox, then polls 127.0.0.1:&lt;port&gt;
 * for listening (fail-closed — never start sing-box over a dead local front).
 * On teardown it stops libbox first, then {@link #nativeStop}.</p>
 */
public final class SlipstreamNative {

    private static volatile boolean sLoaded;
    private static volatile boolean sLoadAttempted;

    private SlipstreamNative() {}

    /**
     * Try to load the native library. Returns false if the .so is absent
     * (e.g. an ABI/build without it) so callers can fail-closed and refuse the
     * dns-tunnel scheme rather than crashing with UnsatisfiedLinkError.
     */
    public static synchronized boolean isAvailable() {
        if (sLoadAttempted) return sLoaded;
        sLoadAttempted = true;
        try {
            System.loadLibrary("slipstream_jni");
            sLoaded = true;
        } catch (Throwable t) {
            android.util.Log.w("slipstream", "libslipstream_jni not available: " + t.getMessage());
            sLoaded = false;
        }
        return sLoaded;
    }

    /**
     * Start the DNS tunnel. Returns true if the worker thread spawned; the
     * tunnel comes up asynchronously, so poll 127.0.0.1:port for listening.
     *
     * @param certPem   the server leaf certificate PEM (from the dns-tunnel:// profile)
     * @param domain    the tunnel domain (e.g. t.example.org)
     * @param port      local TCP port for sing-box's VLESS outbound to dial (e.g. 7001)
     * @param resolvers recursive resolvers as "ip:port" (e.g. 195.208.4.1:53)
     */
    public static native boolean nativeStart(String certPem, String domain, int port, String[] resolvers);

    /** Stop and tear down the tunnel. Idempotent. */
    public static native void nativeStop();
}
