package com.ninitux.vpnrouter;

/** All calls run on the tunnel lifecycle queue, including callbacks forwarded by the native handler. */
final class AndroidStatsSubscription<C> {
    interface Client<C> {
        C create(long generation) throws Exception;
        void connect(C client) throws Exception;
        void disconnect(C client) throws Exception;
    }

    interface Timer {
        Runnable schedule(Runnable task, long delayMs);
    }

    private final Client<C> adapter;
    private final Timer timer;
    private boolean enabled;
    private long generation;
    private C client;
    private Runnable cancelRetry;
    private long delayMs = 1000;
    private volatile long lastStatusAt = -1;

    AndroidStatsSubscription(Client<C> adapter, Timer timer) {
        this.adapter = adapter;
        this.timer = timer;
    }

    void start() {
        if (enabled) return;
        enabled = true;
        connect();
    }

    private void connect() {
        long attempt = ++generation;
        try {
            client = adapter.create(attempt);
            adapter.connect(client);
        } catch (Exception e) {
            disconnected(attempt);
        }
    }

    boolean disconnected(long attempt) {
        if (!enabled || attempt != generation) return false;
        long retry = ++generation;
        lastStatusAt = -1;
        closeClient();
        cancelRetry = timer.schedule(() -> {
            if (!enabled || retry != generation) return;
            cancelRetry = null;
            connect();
        }, delayMs);
        delayMs = Math.min(delayMs * 2, 30000);
        return true;
    }

    boolean acceptStatus(long attempt, long receivedAt) {
        if (!enabled || attempt != generation || client == null) return false;
        delayMs = 1000;
        lastStatusAt = receivedAt;
        return true;
    }

    boolean hasRecentStatus(long now) {
        long at = lastStatusAt;
        return at >= 0 && now - at <= 15000;
    }

    void stop() {
        enabled = false;
        lastStatusAt = -1;
        generation++;
        if (cancelRetry != null) {
            cancelRetry.run();
            cancelRetry = null;
        }
        closeClient();
        delayMs = 1000;
    }

    private void closeClient() {
        C previous = client;
        client = null;
        if (previous != null) {
            try { adapter.disconnect(previous); } catch (Exception ignored) { }
        }
    }
}
