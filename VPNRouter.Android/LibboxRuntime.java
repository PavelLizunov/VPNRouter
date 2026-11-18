package com.ninitux.vpnrouter;

import android.content.Context;
import android.util.Log;
import android.system.Os;

import java.io.File;
import java.io.IOException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;

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
 * {@link CommandServer} per running engine. Only the main tunnel serves status, on an app-private Unix socket.
 */
public final class LibboxRuntime {

    private static final String LOG_TAG = "VpnRouter.Libbox";
    private static final String CRASH_REPORT_SOURCE = "vpnrouter";
    private static final long STOP_TIMEOUT_MS = 4_000L;
    private static final NativeEngineSlot<CommandServer> MAIN = new NativeEngineSlot<>("libbox-main");
    private static final NativeEngineSlot<CommandServer> VERIFY = new NativeEngineSlot<>("libbox-verify");
    private static final Object SETUP_LOCK = new Object();
    private static FutureTask<Void> setup;

    private LibboxRuntime() { }

    public static String coreVersion() {
        return Libbox.version();
    }

    static void ensureSetup(Context ctx) throws Exception {
        FutureTask<Void> pending;
        synchronized (SETUP_LOCK) {
            if (setup == null) {
                Context app = ctx.getApplicationContext();
                FutureTask<Void> init = new FutureTask<>(() -> { setupOnce(app); return null; });
                Thread worker = new Thread(init, "libbox-setup");
                worker.setDaemon(true);
                worker.start();
                setup = init;
            }
            pending = setup;
        }
        try {
            pending.get(6, TimeUnit.SECONDS);
        } catch (ExecutionException e) {
            synchronized (SETUP_LOCK) { if (setup == pending) setup = null; }
            throw new IOException("Native runtime setup failed");
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw e;
        }
    }

    private static void setupOnce(Context ctx) throws Exception {
        File filesDir = ctx.getFilesDir().getCanonicalFile();
        File workingDir = new File(filesDir, "data");
        if (!workingDir.equals(workingDir.getCanonicalFile()) ||
                (!workingDir.isDirectory() && !workingDir.mkdir()))
            throw new IOException("Native working directory unavailable");

        // The pinned AAR writes every config here, and copies it into reports on a crash.
        // Replace only its diagnostic destinations, before setup can archive an older run.
        NativeReportPrivacy.prepare(filesDir, workingDir, ctx.getNoBackupFilesDir().getCanonicalFile(),
                "CrashReport-" + CRASH_REPORT_SOURCE + ".log", Os::symlink);

        SetupOptions options = new SetupOptions();
        options.setBasePath(filesDir.getAbsolutePath());
        options.setCommandServerListenPort(0); // app-private command.sock
        options.setWorkingPath(workingDir.getAbsolutePath());
        options.setTempPath(ctx.getCacheDir().getAbsolutePath());
        options.setFixAndroidStack(false);
        options.setCrashReportSource(CRASH_REPORT_SOURCE);
        Libbox.setup(options);
    }

    static NativeEngineSlot.Session<CommandServer> beginVerification(Context ctx, String json, PlatformInterface platform) {
        return VERIFY.begin(engineLifecycle(ctx, json, platform, false));
    }

    static CommandServer startChecked(String json, PlatformInterface platform, boolean servePrivateStatus) throws Exception {
        if (!servePrivateStatus) throw new IllegalArgumentException("Main tunnel status required");
        return MAIN.start(engineLifecycle(null, json, platform, true), 30_000L);
    }

    private static NativeEngineSlot.Lifecycle<CommandServer> engineLifecycle(
            Context ctx, String config, PlatformInterface platform, boolean main) {
        return new NativeEngineSlot.Lifecycle<CommandServer>() {
            public CommandServer create() throws Exception {
                if (!main) {
                    ensureSetup(ctx);
                    Libbox.checkConfig(config);
                }
                return Libbox.newCommandServer(new QuietHandler(), platform);
            }

            public void activate(CommandServer server) throws Exception {
                if (main) server.start();
                server.startOrReloadService(config, new OverrideOptions());
            }

            public void close(CommandServer server) throws Exception {
                try { server.closeService(); }
                finally { server.close(); }
            }
        };
    }

    static boolean isMainIdle() { return MAIN.isIdle(); }
    static boolean mainStartPermitted() { return MAIN.callbacksPermitted(); }
    static boolean stopMain(CommandServer server) { return MAIN.stop(server, STOP_TIMEOUT_MS); }

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
            // Native debug output may contain configuration fields.
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
