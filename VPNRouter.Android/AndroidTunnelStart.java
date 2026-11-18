package com.ninitux.vpnrouter;

import java.util.Objects;

/** Serializes short ownership decisions with the managed profile transaction. */
final class AndroidTunnelStart {
    interface Read { String get(String key); }
    interface Available { boolean get(); }
    interface Preflight { String prepare(); }

    static final String COMMAND = "vpn_ui_command";
    static final String SUPERSEDED = "vpn_superseded_selection";
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

    String prepareAndClaim(Preflight preflight, Runnable claim) {
        if (!current()) return null;
        String checked = preflight.prepare();
        return checked != null && commit(claim) ? checked : null;
    }
}
