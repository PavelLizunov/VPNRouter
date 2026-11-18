package com.ninitux.vpnrouter;

import java.io.Closeable;
import java.io.FileDescriptor;
import java.io.FileInputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;

public final class AndroidTunnelLifecycleTest {
    public static void main(String[] args) throws Exception {
        serviceChecksSwitchAndRestoreBeforePublishing();
        onlyTheSessionWithAnOpenDescriptorIsReady();
        lateTunWaitsForMainClose();
        slowOrFailedSidecarBlocksReplacement();
        statsReconnectOnlyForTheCurrentGeneration();
    }

    // The policy and descriptor cases run on the host JVM. Also pin where the Android service
    // uses those guards: moving preflight past teardown would disconnect a working VPN.
    private static void serviceChecksSwitchAndRestoreBeforePublishing() throws IOException {
        Path root = Path.of(System.getenv().getOrDefault("GITHUB_WORKSPACE", "."));
        String source = Files.readString(root.resolve("VPNRouter.Android/VpnRouterService.java"));
        String dispatch = source.substring(source.indexOf("int onStartCommand("),
                source.indexOf("public IBinder onBind("));
        String activity = Files.readString(root.resolve("VPNRouter.Android/MainActivity.cs"));
        String builder = Files.readString(root.resolve("VPNRouter.Android/AndroidConfigBuilder.cs"));
        require(activity.contains("ExtraColdStartOnly = \"cold_start_only\"")
                && source.contains("EXTRA_COLD_START_ONLY = \"cold_start_only\""), "cold start marker keys differ");
        requireOrdered(activity, "void OnMissingRuleSet(string _) => coldStartOnly = true;",
                "BuildConfigJsonFromCustom(rawJson, singboxLogPath, OnMissingRuleSet)",
                "DispatchTunnelStart(command, configJson, coldStartOnly);", "BuildConfigJson(entry, singboxLogPath, OnMissingRuleSet)",
                "DispatchTunnelStart(command, configJson, coldStartOnly, entry);", ".PutExtra(ExtraColdStartOnly, coldStartOnly)");
        String imported = builder.substring(builder.indexOf("internal static string BuildConfigJsonFromCustom("));
        requireOrdered(imported, "settings.App.BlockAds = AndroidStorage.GetBlockAds();",
                "CustomConfigInjector.Inject(rawJson, processNames, settings, onMissingRuleSet)");
        require(builder.contains("resolveRuleSet: null, onMissingRuleSet: onMissingRuleSet)")
                && builder.contains("CustomConfigInjector.Inject(rawJson, processNames, settings, onMissingRuleSet)"),
                "Android builder did not propagate rule-set availability");
        requireOrdered(dispatch, "getBooleanExtra(EXTRA_COLD_START_ONLY, false)", "submitCommand(startId,",
                "if (coldStartOnly && commandServer != null)", "reportFailure(\"config-invalid\", true);",
                "return;", "prepareTunnelConfig(cfg,", "if (checked == null) return;",
                "pendingConfigJson = cfg;", "startTunnel(checked);");
        require(!dispatch.contains("retireTunnel()"), "service retired the old tunnel during preflight");
        requireOrdered(dispatch, "else if (loadLastGoodConfig())", "prepareTunnelConfig(pendingConfigJson,",
                "if (checked != null) startTunnel(checked);");

        String preflight = source.substring(source.indexOf("private String prepareTunnelConfig("),
                source.indexOf("private void startTunnel("));
        requireOrdered(preflight, "AndroidTunnelPolicy.prepare(", "reportStartFailure(e, commandServer != null);", "return null;");
        String failure = source.substring(source.indexOf("private void reportFailure("),
                source.indexOf("private void persistLastGoodConfig("));
        requireOrdered(failure, "if (!previousTunnelActive) {", "writeVpnState(STATE_ERROR,", "setTunnelLive(false);",
                "if (!previousTunnelActive) finishCommand(");
        require(!failure.contains("retireTunnel()"), "validation failure retired a working VPN");

        String start = source.substring(source.indexOf("private void startTunnel("),
                source.indexOf("private void reportStartFailure("));
        requireOrdered(start, "retireTunnel();", "session = new AndroidTunnelSession();", "startLibboxService(tunnelConfig);",
                "session.requireEstablishedTun();", "persistLastGoodConfig();", "liveService = this;",
                "writeVpnState(STATE_CONNECTED,", "setTunnelLive(true);", "broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP))");

        String stop = source.substring(source.indexOf("private void stopTunnel("),
                source.indexOf("public synchronized void onRevoke("));
        requireOrdered(stop, "retireTunnel();", "if (!previousTunnelClosed())", "writeVpnState(STATE_DISCONNECTED, null);",
                "broadcastTunnelEvent(new Intent(ACTION_TUNNEL_DOWN))");
        int requestStart = activity.indexOf("public void RequestDisconnect()");
        String request = activity.substring(requestStart, activity.indexOf("protected override", requestStart));
        require(request.contains("StartService(intent);") && !request.contains("SetIntent("),
                "requesting stop published a result before the service");
        requireOrdered(activity, "case ActionTunnelUp:", "case ActionTunnelDown:", "SetIntent(AndroidStorage.HasConfirmedTunnel(), owner, refresh: action == ActionTunnelDown);",
                "AndroidApp.RefreshPerAppSelection();", "case ActionTunnelError:");
        requireOrdered(activity, "private void DispatchTunnelStart(", "if (!AndroidStorage.IsCurrentUiCommand(command)) return;",
                ".PutExtra(AndroidStorage.UiCommandKey, command)", "StartForegroundService(intent)");
        requireOrdered(dispatch, "if (uiCommand != null && !uiCommand.equals(prefs.getString(UI_COMMAND, null)))", "return;",
                "AndroidTunnelPolicy.Preferences selection = readAppPreferences(prefs);", "sourcePrefs = selection.snapshot();",
                "mode = selection.mode", "packages = selection.packages", "prepareTunnelConfig(cfg,",
                "pendingPerAppPrefs = sourcePrefs;", "startTunnel(checked);");
        requireOrdered(source, "RESTART_FLAGS = PendingIntent.FLAG_ONE_SHOT | PendingIntent.FLAG_IMMUTABLE",
                "RESTART_FLAGS | PendingIntent.FLAG_UPDATE_CURRENT", "private void cancelScheduledRestart()", "remove(RESTART_TICKET)",
                "RESTART_FLAGS | PendingIntent.FLAG_NO_CREATE");
        requireOrdered(activity, "new AndroidTunnelEvent(intent?.GetStringExtra(AndroidStorage.UiCommandKey)",
                "owner.StateEvent == null || ReadTunnelEvent() != owner", "case ActionTunnelError:", "PresentTunnelError(context, owner,");
        String tile = Files.readString(root.resolve("VPNRouter.Android/VpnTileService.cs"));
        requireOrdered(tile, "var command = AndroidStorage.BeginUiCommand();", "intent.PutExtra(AndroidStorage.UiCommandKey, command);",
                "PendingIntent.GetActivity(");
        requireOrdered(activity, "case VPNRouter.Core.Services.TileIntentContract.ReasonConnect:", "intent.GetStringExtra(AndroidStorage.UiCommandKey)",
                "AndroidStorage.IsCurrentUiCommand(command)", "Post(() => RequestConnect(command))");
        requireOrdered(dispatch, "if (!java.util.Objects.equals(origin,", "if (commandServer != null)", "hasLiveTunnel()",
                "broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP))", "else if (loadLastGoodConfig())");
        require(source.contains("synchronized void onTaskRemoved(") && source.contains("synchronized void onRevoke(")
                && source.contains("synchronized int onStartCommand("), "swipe scheduling must serialize with revoke and stop");
        String external = Files.readString(root.resolve("VPNRouter.Android/VpnControlReceiver.cs"));
        require(external.contains("SvcActionStart = \"com.ninitux.vpnrouter.TILE_START\""), "external restore requires an unticketed action");
        String qr = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.QrScanApply.cs"));
        requireOrdered(qr, "var command = reserved ?? AndroidStorage.BeginUiCommand();", "await RefreshStoredSubscriptionAsync(",
                "if (!AndroidStorage.IsCurrentUiCommand(command)) return;", "RequestConnect(command);");
        String app = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.axaml.cs"));
        String summary = app.substring(app.indexOf("private void UpdatePerAppSummary("),
                app.indexOf("private void OnSaveClicked("));
        requireOrdered(summary, "PerAppPolicySummary(AndroidStorage.GetActivePerAppPolicy()", "_appPickerModeHint.Text =", "_configRowValue.Text =");
    }

    private static void requireOrdered(String source, String... steps) {
        int offset = 0;
        for (String step : steps) {
            int found = source.indexOf(step, offset);
            require(found >= offset, "Missing or misplaced service step: " + step);
            offset = found + step.length();
        }
    }

    private static void onlyTheSessionWithAnOpenDescriptorIsReady() throws Exception {
        AndroidTunnelSession active = new AndroidTunnelSession();
        AndroidTunnelSession restore = new AndroidTunnelSession();
        Path fixture = Files.createTempFile("tunnel-session", ".test");
        try (FileInputStream stream = new FileInputStream(fixture.toFile())) {
            requireNotReady(active);
            active.openTun(() -> stream, FileInputStream::getFD);
            active.requireEstablishedTun();
            requireNotReady(restore);
            try {
                restore.openTun(() -> { throw new IOException("establish failed"); }, FileInputStream::getFD);
                throw new AssertionError("failed establish returned a descriptor");
            } catch (IOException expected) { }
            requireNotReady(restore);
            try {
                restore.openTun(() -> null, FileInputStream::getFD);
                throw new AssertionError("null descriptor accepted");
            } catch (IllegalStateException expected) { }
            requireNotReady(restore);
            try {
                restore.openTun(() -> (Closeable) () -> { }, ignored -> new FileDescriptor());
                throw new AssertionError("invalid descriptor accepted");
            } catch (IllegalStateException expected) { }
            requireNotReady(restore);
            active.requireEstablishedTun();
            try (FileInputStream reopened = new FileInputStream(fixture.toFile())) {
                restore.openTun(() -> reopened, FileInputStream::getFD);
                restore.requireEstablishedTun();
                restore.retire();
                require(reopened.getFD().valid(), "retiring the session prematurely closed its descriptor");
                requireNotReady(restore);
                require(restore.releaseIfClosed(true), "restored descriptor cleanup failed");
            }
        } finally {
            Files.deleteIfExists(fixture);
        }
        requireNotReady(active); // The object is still owned, but its OS descriptor is closed.
        active.retire();
        require(active.releaseIfClosed(true), "descriptor cleanup failed");
    }

    private static void requireNotReady(AndroidTunnelSession session) {
        try {
            session.requireEstablishedTun();
            throw new AssertionError("session without a live TUN accepted as connected");
        } catch (IllegalStateException expected) { }
    }

    private static void lateTunWaitsForMainClose() throws Exception {
        AndroidTunnelSession session = new AndroidTunnelSession();
        AtomicInteger descriptorsClosed = new AtomicInteger();
        CountDownLatch establishing = new CountDownLatch(1);
        CountDownLatch release = new CountDownLatch(1);
        FutureTask<Boolean> callback = new FutureTask<>(() -> {
            try {
                session.openTun(() -> {
                    establishing.countDown();
                    await(release);
                    return (Closeable) descriptorsClosed::incrementAndGet;
                }, ignored -> { throw new AssertionError("retired descriptor accepted"); });
                return false;
            } catch (IllegalStateException retired) {
                return true;
            }
        });
        new Thread(callback).start();
        await(establishing);
        requireNotReady(session);
        session.retire();
        require(!session.releaseIfClosed(true), "replacement passed an in-flight openTun");
        release.countDown();
        require(callback.get(3, TimeUnit.SECONDS), "retired engine received a descriptor");
        requireNotReady(session);
        require(!session.releaseIfClosed(false) && descriptorsClosed.get() == 0, "descriptor closed before main");
        require(session.releaseIfClosed(true) && descriptorsClosed.get() == 1, "late descriptor leaked");
        require(session.releaseIfClosed(true) && descriptorsClosed.get() == 1, "descriptor closed twice");
        try {
            session.openTun(() -> { throw new AssertionError("retired session established a TUN"); }, ignored -> null);
            throw new AssertionError("stale openTun accepted");
        } catch (IllegalStateException expected) { }
    }

    private static void slowOrFailedSidecarBlocksReplacement() throws Exception {
        AndroidTunnelSession session = new AndroidTunnelSession();
        CountDownLatch release = new CountDownLatch(1);
        session.sidecarStarted(() -> await(release));
        session.retire();
        session.stopSidecar(1);
        require(!session.releaseIfClosed(true), "replacement passed a timed-out sidecar");
        release.countDown();
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
        while (!session.releaseIfClosed(true)) {
            require(System.nanoTime() < deadline, "sidecar completion did not release the barrier");
            Thread.sleep(1);
        }
        AndroidTunnelSession failed = new AndroidTunnelSession();
        failed.sidecarStarted(() -> { throw new IllegalStateException("test fixture"); });
        failed.retire();
        failed.stopSidecar(1000);
        require(!failed.releaseIfClosed(true), "failed native stop was treated as success");
    }

    private static void statsReconnectOnlyForTheCurrentGeneration() {
        class Fixture implements AndroidStatsSubscription.Client<Long>, AndroidStatsSubscription.Timer {
            long generation, delay;
            int starts, disconnects, retries, cancellations;
            boolean fail = true;
            Runnable retry;
            public Long create(long id) { starts++; generation = id; return id; }
            public void connect(Long id) throws Exception { if (fail) throw new Exception("test fixture"); }
            public void disconnect(Long id) { disconnects++; }
            public Runnable schedule(Runnable task, long delayMs) {
                retry = task;
                delay = delayMs;
                retries++;
                return () -> cancellations++;
            }
        }
        Fixture f = new Fixture();
        AndroidStatsSubscription<Long> stats = new AndroidStatsSubscription<>(f, f);
        stats.start();
        long first = f.generation;
        require(f.starts == 1 && f.disconnects == 1 && f.delay == 1000, "failed connect cleanup/retry");
        for (long delay : new long[]{2000, 4000, 8000, 16000, 30000, 30000}) {
            f.retry.run();
            require(f.delay == delay && f.disconnects == f.starts, "retry backoff");
        }
        f.fail = false;
        f.retry.run();
        long connected = f.generation;
        require(!stats.acceptStatus(first, 200) && !stats.disconnected(first), "old callback affected new client");
        require(!stats.hasRecentStatus(200), "connect alone reported healthy");
        require(stats.acceptStatus(connected, 200) && stats.hasRecentStatus(201), "current stats dropped");
        require(!stats.hasRecentStatus(20200), "stale status reported healthy");
        require(stats.disconnected(connected) && f.delay == 1000, "stream loss did not reconnect");
        require(!stats.hasRecentStatus(201), "disconnected stream still reported healthy");
        int retries = f.retries;
        require(!stats.disconnected(connected) && f.retries == retries, "duplicate retry");
        Runnable staleRetry = f.retry;
        stats.stop(); // screen off or tunnel stop
        int starts = f.starts;
        staleRetry.run();
        require(f.starts == starts && f.cancellations == 1, "retry survived stop");
        stats.start(); // screen on or replacement
        staleRetry.run();
        stats.start();
        require(f.starts == starts + 1 && stats.acceptStatus(f.generation, 300), "duplicate client after resume");
        stats.stop();
        require(!stats.acceptStatus(f.generation, 400) && !stats.hasRecentStatus(400)
                && f.disconnects == f.starts, "stats survived stop");
    }

    private static void await(CountDownLatch latch) {
        try {
            require(latch.await(3, TimeUnit.SECONDS), "fixture timed out");
        } catch (InterruptedException e) {
            throw new AssertionError(e);
        }
    }

    private static void require(boolean condition, String error) {
        if (!condition) throw new AssertionError(error);
    }
}
