package com.ninitux.vpnrouter;

import java.util.Objects;

/** Serializes short ownership decisions with the managed profile transaction. */
final class AndroidTunnelStart {
    interface Read { String get(String key); }
    interface Available { boolean get(); }
    interface Preflight { String prepare(); }
    interface MonotonicMillis { long now(); }
    interface ReadinessWait { boolean await(long timeoutMillis) throws InterruptedException; }

    static final String COMMAND = "vpn_ui_command";
    static final String SUPERSEDED = "vpn_superseded_selection";
    // libbox's 15-second HTTP timeout starts after its transport dial.
    private static final long DNSTT_READINESS_MILLIS = 25_000L;
    private static final long READINESS_CANCEL_POLL_MILLIS = 100L;
    final String command;
    private final Object monitor;
    private final Read read;
    private final Available available;
    private final boolean reserved;

    AndroidTunnelStart(Object monitor, Read read, Available available, String command, boolean reserved) {
        this.monitor = monitor;
        this.read = read;
        this.available = available;
        this.command = command;
        this.reserved = reserved;
    }

    boolean current() {
        synchronized (monitor) {
            return available.get() && Objects.equals(command, read.get(COMMAND))
                    && (!reserved || command != null && !command.equals(read.get(SUPERSEDED)));
        }
    }

    boolean commit(Runnable transition) {
        synchronized (monitor) {
            if (!current()) return false;
            transition.run();
            return true;
        }
    }

    boolean awaitDnsttReadiness(long startedAt, MonotonicMillis clock, ReadinessWait wait,
                                Available passed) throws InterruptedException {
        long deadline = startedAt + DNSTT_READINESS_MILLIS;
        while (current()) {
            long remaining = deadline - clock.now();
            if (remaining <= 0) return false;
            if (wait.await(Math.min(remaining, READINESS_CANCEL_POLL_MILLIS))) {
                return clock.now() <= deadline && passed.get() && current();
            }
        }
        return false;
    }

    String prepareAndClaim(Preflight preflight, Runnable claim) {
        if (!current()) return null;
        String checked = preflight.prepare();
        return checked != null && commit(claim) ? checked : null;
    }
}
