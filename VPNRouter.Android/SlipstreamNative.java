package com.ninitux.vpnrouter;

public final class SlipstreamNative {

    private static volatile boolean sLoaded;
    private static volatile boolean sLoadAttempted;

    private SlipstreamNative() {}

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

    public static native boolean nativeStart(String certPath, String domain, int port, String[] resolvers);

    public static native void nativeStop();
}
