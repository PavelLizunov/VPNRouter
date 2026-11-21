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
        replacementDuringPreflightKeepsEstablishedTunnel(false);
        replacementDuringPreflightKeepsEstablishedTunnel(true);
        lateActivationCannotSaveOrPublish();
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
        // The status probe runs between command reservations. Disposing a LoadClass peer
        // here used to invalidate the monitor cached by the first Connect in this process.
        String runtime = Files.readString(root.resolve("VPNRouter.Core/Platform/Android/AndroidSingBoxRuntime.cs"));
        requireOrdered(runtime, "JniType.GetCachedJniType(", "private static bool QueryService(",
                "var service = ServiceClassReference;", "CallStaticBooleanMethod(service, method)");
        String storage = Files.readString(root.resolve("VPNRouter.Android/AndroidStorage.cs"));
        requireOrdered(storage, "private static T WithCommandPreferences", "AndroidSingBoxRuntime.ServiceClassReference",
                "MonitorEnter(reference)", "action(prefs)", "MonitorExit(reference)");
        requireOrdered(storage, "SelectedSubscriptionRow() =>", "WithCommandPreferences(prefs =>",
                "string? Read(string key) => prefs.GetString(key, null);", "FindFeedRow(GetSubscriptions(), Read)");
        String picker = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.ServerList.cs"));
        requireOrdered(picker, "var selected = AndroidStorage.SelectedSubscriptionRow()?.In(source);",
                "BuildServerRow(source, srv, selected)", "_srvRemoveBtn.IsEnabled = selected != null");
        requireOrdered(activity, "var resolved = AndroidStorage.GetActiveServer(command);",
                "BuildConfigJson(entry, singboxLogPath, OnMissingRuleSet)", "if (!AndroidStorage.IsCurrentUiCommand(command)) return;");
        String builder = Files.readString(root.resolve("VPNRouter.Android/AndroidConfigBuilder.cs"));
        require(activity.contains("ExtraColdStartOnly = \"cold_start_only\"")
                && source.contains("EXTRA_COLD_START_ONLY = \"cold_start_only\""), "cold start marker keys differ");
        requireOrdered(activity, "void OnMissingRuleSet(string _) => coldStartOnly = true;",
                "BuildConfigJsonFromCustom(rawJson, singboxLogPath, OnMissingRuleSet)",
                "origin.CompletePreparation(ReadTunnelEvent, _ => DispatchTunnelStart(command, configJson, coldStartOnly))",
                "BuildConfigJson(entry, singboxLogPath, OnMissingRuleSet)",
                "origin.CompletePreparation(ReadTunnelEvent, _ => DispatchTunnelStart(command, configJson, coldStartOnly, entry))",
                ".PutExtra(ExtraColdStartOnly, coldStartOnly)");
        String imported = builder.substring(builder.indexOf("internal static string BuildConfigJsonFromCustom("));
        requireOrdered(imported, "settings.App.BlockAds = AndroidStorage.GetBlockAds();",
                "CustomConfigInjector.Inject(rawJson, processNames, settings, onMissingRuleSet)");
        require(builder.contains("resolveRuleSet: null, onMissingRuleSet: onMissingRuleSet)")
                && builder.contains("CustomConfigInjector.Inject(rawJson, processNames, settings, onMissingRuleSet)"),
                "Android builder did not propagate rule-set availability");
        requireOrdered(dispatch, "getBooleanExtra(EXTRA_COLD_START_ONLY, false)", "submitCommand(startId,",
                "if (coldStartOnly && commandServer != null)", "reportFailure(\"config-invalid\", true);",
                "return;", "startTunnel(() -> prepareTunnelConfig(cfg,", "pendingConfigJson = cfg;");
        require(!dispatch.contains("retireTunnel()"), "service retired the old tunnel during preflight");
        requireOrdered(dispatch, "else if (loadLastGoodConfig())", "startTunnel(() -> prepareTunnelConfig(pendingConfigJson,");

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
        requireOrdered(start, "start.prepareAndClaim(preflight, () -> {", "select.run();", "detachTunnel();",
                "if (tunnelConfig == null)", "closeDetachedTunnel(previous, previousServer);", "if (!start.current()) return;",
                "session = new AndroidTunnelSession();", "startSlipstreamIfNeeded();", "if (!start.current()) return;",
                "startLibboxService(tunnelConfig);",
                "session.requireEstablishedTun();", "if (pendingProbeManagedDnstt && !DnsttReadyProbe.ready(start)) {",
                "retireTunnel();", "reportFailure(\"proxy-unreachable\", false);", "start.commit(() -> {",
                "persistLastGoodConfig();", "liveService = this;",
                "writeVpnState(STATE_CONNECTED,", "setTunnelLive(true);", "broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP))");
        String probe = start.substring(start.indexOf("class DnsttReadyProbe"));
        requireOrdered(probe, "Libbox.CommandOutbounds", "Libbox.newCommandClient(", "client.connect();",
                "client.urlTest(\"proxy\");", "while (start.current())", "probe.finished.await(",
                "probe.passed && start.current()", "finally", "client.disconnect()");
        requireOrdered(probe, "\"proxy\".equals(item.getTag())", "\"ssh\".equals(item.getType())",
                "item.getURLTestDelay() > 0", "complete(true)");
        require(!probe.contains("external_controller") && !probe.contains("new Socket(")
                && !probe.contains("ServerSocket"), "DNSTT readiness must use the private command server");
        String libbox = Files.readString(root.resolve("VPNRouter.Android/LibboxRuntime.java"));
        require(libbox.contains("options.setCommandServerListenPort(0);"), "Native status socket must remain private");

        requireOrdered(activity, "if (configMode == \"custom\")", "DispatchTunnelStart(command, configJson, coldStartOnly)",
                "DispatchTunnelStart(command, configJson, coldStartOnly, entry)",
                ".PutExtra(ExtraProbeManagedDnstt, dnsTunnelEntry?.Dnstt is not null)");
        requireOrdered(source, "intent.getBooleanExtra(EXTRA_PROBE_MANAGED_DNSTT, false)",
                "pendingProbeManagedDnstt = probeManagedDnstt;",
                "editor.putBoolean(KEY_LAST_GOOD_PROBE_MANAGED_DNSTT, pendingProbeManagedDnstt);",
                "pendingProbeManagedDnstt = prefs.getBoolean(KEY_LAST_GOOD_PROBE_MANAGED_DNSTT, false);");

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
                ".PutExtra(AndroidStorage.UiCommandKey, command)", "if (!AndroidStorage.IsCurrentUiCommand(command)) return;",
                "StartForegroundService(intent)");
        requireOrdered(dispatch, "if (!start.current())", "return;",
                "AndroidTunnelPolicy.Preferences selection = readAppPreferences(prefs);", "sourcePrefs = selection.snapshot();",
                "mode = selection.mode", "packages = selection.packages", "startTunnel(() -> prepareTunnelConfig(cfg,",
                "pendingPerAppPrefs = sourcePrefs;");
        requireOrdered(source, "RESTART_FLAGS = PendingIntent.FLAG_ONE_SHOT | PendingIntent.FLAG_IMMUTABLE",
                "RESTART_FLAGS | PendingIntent.FLAG_UPDATE_CURRENT", "private void cancelScheduledRestart()", "remove(RESTART_TICKET)",
                "RESTART_FLAGS | PendingIntent.FLAG_NO_CREATE");
        requireOrdered(activity, "new AndroidTunnelEvent(intent?.GetStringExtra(AndroidStorage.UiCommandKey)",
                "owner.StateEvent == null || (action == ActionStats ? ReadServiceEvent() : ReadTunnelEvent()) != owner",
                "case ActionTunnelError:", "PresentTunnelError(context, owner,");
        requireOrdered(activity, "void ShowPreparationError(string message) => origin.CompletePreparation(",
                "PresentTunnelError(this, owner, message", "origin.CompletePreparation(ReadTunnelEvent, _ => DispatchTunnelStart(");
        requireOrdered(source, "String stateEvent = serviceStateEvent;", "String command = serviceStateCommand;",
                "event.putExtra(UI_COMMAND, command);", "event.putExtra(STATE_EVENT, stateEvent);", "event.putExtra(ACTIVE_SESSION, sessionId);");
        String tile = Files.readString(root.resolve("VPNRouter.Android/VpnTileService.cs"));
        requireOrdered(tile, "var command = AndroidStorage.BeginUiCommand();", "intent.PutExtra(AndroidStorage.UiCommandKey, command);",
                "PendingIntent.GetActivity(");
        requireOrdered(activity, "case VPNRouter.Core.Services.TileIntentContract.ReasonConnect:", "intent.GetStringExtra(AndroidStorage.UiCommandKey)",
                "AndroidStorage.IsCurrentUiCommand(command)", "Post(() => RequestConnect(command))");
        requireOrdered(dispatch, "if (!start.current())", "if (commandServer != null)", "hasLiveTunnel()",
                "broadcastTunnelEvent(new Intent(ACTION_TUNNEL_UP))", "else if (loadLastGoodConfig())");
        require(source.contains("synchronized void onTaskRemoved(") && source.contains("synchronized void onRevoke(")
                && source.contains("synchronized int onStartCommand("), "swipe scheduling must serialize with revoke and stop");
        require(activity.contains("private ConnectConsentGate _consentGate = new();"), "fresh launches must start with an empty consent gate");
        requireOrdered(activity, "base.OnCreate(savedInstanceState);", "if (savedInstanceState != null)",
                "_consentGate = _retainedConsentGate ?? new ConnectConsentGate(",
                "savedInstanceState.GetString(AndroidStorage.UiCommandKey), savedInstanceState.GetString(QueuedConsentKey)",
                "_retainedConsentGate = _consentGate;", "Instance = this;");
        requireOrdered(activity, "protected override void OnSaveInstanceState(", "var (owner, pending) = _consentGate.Requests;",
                "outState.PutString(AndroidStorage.UiCommandKey, owner);", "outState.PutString(QueuedConsentKey, pending);");
        String resume = activity.substring(activity.indexOf("protected override void OnResume()"),
                activity.indexOf("protected override void OnNewIntent("));
        requireOrdered(resume, "base.OnResume();", "_consentGate.ClaimPending(ReferenceEquals(Instance, this) && !IsFinishing,",
                "AndroidStorage.IsCurrentUiCommand) is { } pending)", "RequestConnect(pending);");
        String result = activity.substring(activity.indexOf("protected override void OnActivityResult("),
                activity.indexOf("private static string? _pendingExportContent;"));
        requireOrdered(result, "if (requestCode != RequestVpnConsent)", "if (!ReferenceEquals(Instance, this)) return;",
                "var command = _consentGate.ResolveResult(AndroidStorage.IsCurrentUiCommand);",
                "if (command is null || !AndroidStorage.IsCurrentUiCommand(command)) return;", "resultCode == Result.Ok",
                "StartTunnelService(command);", "else", "PresentStartError(Localization.TunnelError(\"no-permission\"), command);");
        require(!result.contains("RequestConnect(") && !resume.contains("Post(() => RequestConnect("),
                "consent retry must be claimed and started on the resumed activity");
        String free = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.FreeConfigs.cs"));
        requireOrdered(free, "AndroidStorage.SelectSingleServer(", "RefreshConfigModeUiFromStorage();",
                "RequestConnect(command);");
        String simple = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.VpnLifecycle.cs"));
        requireOrdered(simple, "if (AndroidSubscriptionRefresh.HasServerInputEdit(input, AndroidStorage.SavedServerInput()))", "command = ReserveConnectCommand();",
                "!TrySaveServerInput(command)",
                "activity.RequestConnect(command);");
        requireOrdered(simple, "private string? ReserveConnectCommand()", "AndroidStorage.BeginUiCommand();",
                "if (command == null) ShowMenuFeedback(Localization.TunnelError(\"command-unavailable\"));", "return command;");
        String external = Files.readString(root.resolve("VPNRouter.Android/VpnControlReceiver.cs"));
        require(external.contains("SvcActionStart = \"com.ninitux.vpnrouter.TILE_START\""), "external restore requires an unticketed action");
        String qr = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.QrScanApply.cs"));
        requireOrdered(qr, "var command = reserved ?? ReserveConnectCommand();", "if (command == null || !AndroidStorage.IsCurrentUiCommand(command)) return;", "await RefreshStoredSubscriptionAsync(",
                "if (!AndroidStorage.IsCurrentUiCommand(command)) return;", "RequestConnect(command);");
        String app = Files.readString(root.resolve("VPNRouter.Android/AndroidApp.axaml.cs"));
        requireOrdered(app, "_configRowValue = new TextBlock", "TextWrapping = TextWrapping.Wrap",
                "Children = { header, _configRowValue }");
        String summary = app.substring(app.indexOf("private void UpdatePerAppSummary("),
                app.indexOf("private bool TrySaveServerInput("));
        requireOrdered(summary, "PerAppPolicySummary(AndroidStorage.GetActivePerAppPolicy()", "_appPickerModeHint.Text =", "_configRowValue.Text = policy;");
    }

    private static void requireOrdered(String source, String... steps) {
        int offset = 0;
        for (String step : steps) {
            int found = source.indexOf(step, offset);
            require(found >= offset, "Missing or misplaced service step: " + step);
            offset = found + step.length();
        }
    }

    private static void lateActivationCannotSaveOrPublish() throws Exception {
        Object monitor = new Object();
        java.util.Map<String, String> prefs = new java.util.HashMap<>();
        prefs.put(AndroidTunnelStart.COMMAND, "A");
        prefs.put("last-good", "fixture-live");
        AndroidTunnelStart start = new AndroidTunnelStart(monitor, prefs::get, () -> true, "A", true);
        CountDownLatch activating = new CountDownLatch(1);
        CountDownLatch ready = new CountDownLatch(1);
        FutureTask<Boolean> late = new FutureTask<>(() -> {
            String profile = start.prepareAndClaim(() -> "fixture-A", () -> {
                require(Thread.holdsLock(monitor), "replacement was claimed outside the selection transaction");
            });
            require(profile != null, "current start failed preflight");
            activating.countDown();
            await(ready);
            return start.commit(() -> prefs.put("last-good", profile));
        });
        new Thread(late).start();
        await(activating);
        synchronized (monitor) { prefs.put(AndroidTunnelStart.SUPERSEDED, "A"); }
        ready.countDown();
        require(!late.get(3, TimeUnit.SECONDS), "late activation published after import");
        require("fixture-live".equals(prefs.get("last-good")), "late activation replaced last-good");
    }

    private static void replacementDuringPreflightKeepsEstablishedTunnel(boolean newerCommand) throws Exception {
        Object monitor = new Object();
        java.util.Map<String, String> prefs = new java.util.HashMap<>();
        prefs.put(AndroidTunnelStart.COMMAND, "A");
        AndroidTunnelStart start = new AndroidTunnelStart(monitor, prefs::get, () -> true, "A", true);
        AndroidTunnelSession live = new AndroidTunnelSession();
        Path fixture = Files.createTempFile("vpn-preflight", ".test");
        try (FileInputStream descriptor = new FileInputStream(fixture.toFile())) {
            live.openTun(() -> descriptor, FileInputStream::getFD);
            CountDownLatch checking = new CountDownLatch(1);
            CountDownLatch release = new CountDownLatch(1);
            FutureTask<String> prepared = new FutureTask<>(() -> start.prepareAndClaim(() -> {
                require(!Thread.holdsLock(monitor), "preflight blocked profile changes");
                checking.countDown();
                await(release);
                return "{\"inbounds\":[{\"type\":\"tun\"}]}";
            }, live::retire));
            new Thread(prepared).start();
            await(checking);
            synchronized (monitor) {
                if (newerCommand) prefs.put(AndroidTunnelStart.COMMAND, "B");
                else prefs.put(AndroidTunnelStart.SUPERSEDED, "A");
            }
            release.countDown();
            require(prepared.get(3, TimeUnit.SECONDS) == null, "cancelled config reached native activation");
            live.requireEstablishedTun();
            require(!start.commit(() -> { throw new AssertionError("stale last-good or error published"); }),
                    "superseded command can publish");
            synchronized (monitor) { prefs.put(AndroidTunnelStart.COMMAND, "C"); }
            AndroidTunnelStart next = new AndroidTunnelStart(monitor, prefs::get, () -> true, "C", true);
            require("fixture-C".equals(next.prepareAndClaim(() -> "fixture-C", live::retire)),
                    "replacement blocked after supersession");
            requireNotReady(live);
            require(live.releaseIfClosed(true), "fixture descriptor did not close");
        } finally {
            Files.deleteIfExists(fixture);
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
