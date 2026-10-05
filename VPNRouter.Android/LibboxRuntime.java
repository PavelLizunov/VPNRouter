package com.ninitux.vpnrouter;

import android.content.Context;
import android.util.Log;

import java.io.File;

import io.nekohasekai.libbox.BridgeOptions;
import io.nekohasekai.libbox.BridgeSession;
import io.nekohasekai.libbox.CommandServer;
import io.nekohasekai.libbox.CommandServerHandler;
import io.nekohasekai.libbox.ConnectionOwner;
import io.nekohasekai.libbox.Libbox;
import io.nekohasekai.libbox.NeighborUpdateListener;
import io.nekohasekai.libbox.OverrideOptions;
import io.nekohasekai.libbox.PlatformInterface;
import io.nekohasekai.libbox.PlatformUser;
import io.nekohasekai.libbox.SetupOptions;
import io.nekohasekai.libbox.ShellSession;
import io.nekohasekai.libbox.StringIterator;
import io.nekohasekai.libbox.SystemProxyStatus;

/**
 * The one place that talks to the libbox lifecycle API (sing-box 1.14): process-wide setup and a
 * {@link CommandServer} per running engine. The command server's gRPC listener is never started,
 * so there is no socket or port to clash with a second engine in the same process.
 */
final class LibboxRuntime {

    private static final String LOG_TAG = "VpnRouter.Libbox";
    private static final String CRASH_REPORT_SOURCE = "vpnrouter";
    private static final Object SETUP_LOCK = new Object();
    private static boolean setupDone;

    private LibboxRuntime() { }

    /** Writes the Go runtime's stderr to files/data/CrashReport-vpnrouter.log (set up by libbox). */
    static void ensureSetup(Context ctx) throws Exception {
        synchronized (SETUP_LOCK) {
            if (setupDone) return;

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
            options.setCrashReportSource(CRASH_REPORT_SOURCE);
            Libbox.setup(options);

            setupDone = true;
            Log.i(LOG_TAG, "libbox setup OK (base=" + filesDir.getAbsolutePath() + ")");
        }
    }

    /** Validates the config and starts the engine; the returned server must be passed to {@link #stop}. */
    static CommandServer start(String configJson, PlatformInterface platform) throws Exception {
        Libbox.checkConfig(configJson);
        CommandServer server = Libbox.newCommandServer(new QuietHandler(), platform);
        try {
            server.startOrReloadService(configJson, new OverrideOptions());
        } catch (Exception e) {
            stop(server);
            throw e;
        }
        return server;
    }

    static void stop(CommandServer server) {
        if (server == null) return;
        try {
            server.closeService();
        } catch (Exception e) {
            Log.w(LOG_TAG, "closeService threw: " + e.getMessage());
        }
        try {
            server.close();
        } catch (Exception e) {
            Log.w(LOG_TAG, "close threw: " + e.getMessage());
        }
    }

    /** Only the engine's own requests reach this handler: the app runs no command client. */
    private static final class QuietHandler implements CommandServerHandler {
        @Override
        public int connectSSHAgent() throws Exception {
            throw new Exception("ssh agent is not supported");
        }

        @Override
        public SystemProxyStatus getSystemProxyStatus() {
            return new SystemProxyStatus();
        }

        @Override
        public void serviceReload() {
            Log.i(LOG_TAG, "engine asked for a service reload; ignored");
        }

        @Override
        public void serviceStop() {
            Log.i(LOG_TAG, "engine asked to stop the service; ignored");
        }

        @Override
        public void setSystemProxyEnabled(boolean enabled) { }

        @Override
        public void triggerNativeCrash() {
            Log.w(LOG_TAG, "engine asked for a native crash; ignored");
        }

        @Override
        public void writeDebugMessage(String message) {
            if (message != null && !message.isEmpty()) {
                Log.d("Libbox", message);
            }
        }
    }

    /** What the app does not offer to the engine: shell, bridge, neighbor table, owner lookup, SSH. */
    abstract static class PlatformDefaults implements PlatformInterface {
        @Override
        public void cancelNotification(String identifier, int typeID) { }

        @Override
        public void checkPlatformShell() throws Exception {
            throw new Exception("platform shell is not supported");
        }

        @Override
        public void closeNeighborMonitor(NeighborUpdateListener listener) { }

        @Override
        public BridgeSession createBridge(BridgeOptions options) throws Exception {
            throw new Exception("platform bridge is not supported");
        }

        @Override
        public ConnectionOwner findConnectionOwner(
                int ipProtocol,
                String sourceAddress, int sourcePort,
                String destinationAddress, int destinationPort) throws Exception {
            throw new Exception("connection owner lookup is not supported");
        }

        @Override
        public String lookupSFTPServer() throws Exception {
            throw new Exception("sftp is not supported");
        }

        @Override
        public PlatformUser lookupUser(String username) throws Exception {
            throw new Exception("user lookup is not supported");
        }

        @Override
        public ShellSession openShellSession(
                PlatformUser user, String command, StringIterator environ,
                String term, int rows, int cols) throws Exception {
            throw new Exception("platform shell is not supported");
        }

        @Override
        public String readSystemSSHHostKey() throws Exception {
            throw new Exception("ssh host key is not supported");
        }

        @Override
        public void registerMyInterface(String name) { }

        @Override
        public void startNeighborMonitor(NeighborUpdateListener listener) { }

        @Override
        public String tailscaleHostname() {
            return "";
        }

        @Override
        public boolean usePlatformBridge() {
            return false;
        }

        @Override
        public boolean usePlatformShell() {
            return false;
        }
    }
}
