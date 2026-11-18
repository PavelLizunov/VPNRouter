package com.ninitux.vpnrouter;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/** Holds exclusive ownership until native close finishes, including after the caller times out. */
final class NativeEngineSlot<T> {
    interface Lifecycle<T> {
        T create() throws Exception;
        void activate(T engine) throws Exception;
        void close(T engine) throws Exception;
    }

    static final class Session<T> {
        private final CountDownLatch ready = new CountDownLatch(1);
        private final CountDownLatch stop = new CountDownLatch(1);
        private final CountDownLatch done = new CountDownLatch(1);
        private volatile T engine;
        private volatile boolean activated;
        private volatile boolean closed;

        T awaitReady(long timeoutMs) throws Exception {
            try {
                if (!ready.await(timeoutMs, TimeUnit.MILLISECONDS))
                    throw new TimeoutException("Native engine startup timed out");
                if (!activated || stop.getCount() == 0)
                    throw new IllegalStateException("Native engine startup failed");
                return engine;
            } catch (Exception e) {
                cancel();
                if (e instanceof InterruptedException) Thread.currentThread().interrupt();
                throw e;
            }
        }

        void cancel() {
            stop.countDown();
            ready.countDown();
        }

        boolean close(long timeoutMs) {
            cancel();
            try {
                return done.await(timeoutMs, TimeUnit.MILLISECONDS) && closed;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
    }

    private final String workerName;
    private Session<T> current;

    NativeEngineSlot(String workerName) { this.workerName = workerName; }

    synchronized boolean isIdle() { return current == null; }

    synchronized boolean callbacksPermitted() {
        return current != null && current.stop.getCount() != 0;
    }

    T start(Lifecycle<T> lifecycle, long timeoutMs) throws Exception {
        return begin(lifecycle).awaitReady(timeoutMs);
    }

    Session<T> begin(Lifecycle<T> lifecycle) {
        Session<T> session = new Session<>();
        Thread worker = new Thread(() -> run(session, lifecycle), workerName);
        worker.setDaemon(true);
        synchronized (this) {
            if (current != null) throw new IllegalStateException("Native engine is busy");
            current = session;
            try {
                worker.start();
            } catch (RuntimeException | Error e) {
                current = null;
                throw e;
            }
        }
        return session;
    }

    boolean stop(T engine, long timeoutMs) {
        Session<T> session;
        synchronized (this) {
            session = current;
            if (session == null || session.engine != engine) return true;
        }
        return session.close(timeoutMs);
    }

    private void run(Session<T> session, Lifecycle<T> lifecycle) {
        try {
            if (session.stop.getCount() != 0) {
                session.engine = lifecycle.create();
                if (session.stop.getCount() != 0) {
                    lifecycle.activate(session.engine);
                    session.activated = true;
                    session.ready.countDown();
                    session.stop.await();
                }
            }
        } catch (Exception | LinkageError ignored) {
            // Native diagnostics may contain profile material. The caller gets a fixed error.
        } finally {
            session.stop.countDown();
            try {
                if (session.engine != null) lifecycle.close(session.engine);
                session.closed = true;
            } catch (Exception | LinkageError ignored) {
                // The native resources are in an unknown state. Keep the slot unavailable.
            } finally {
                synchronized (this) {
                    if (session.closed) current = null;
                }
                session.ready.countDown();
                session.done.countDown();
            }
        }
    }
}
