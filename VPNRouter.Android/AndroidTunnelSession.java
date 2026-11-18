package com.ninitux.vpnrouter;

import java.io.Closeable;
import java.io.FileDescriptor;
import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/** One engine's descriptors and optional process-wide Slipstream listener. */
final class AndroidTunnelSession {
    private final List<Closeable> descriptors = new ArrayList<>();
    private FileDescriptor establishedTun;
    private int opening;
    private boolean retiring;
    private FutureTask<Void> sidecarClose;

    interface DescriptorAccess<T> {
        FileDescriptor get(T descriptor) throws IOException;
    }

    <T extends Closeable> T openTun(Callable<T> establish, DescriptorAccess<T> access) throws Exception {
        synchronized (this) {
            requireActive();
            opening++;
        }
        try {
            T descriptor = establish.call();
            if (descriptor == null) throw new IllegalStateException("tun-not-established");
            synchronized (this) {
                // A callback can finish after stopMain timed out. Keep even that descriptor
                // with its original generation until the engine has actually closed.
                descriptors.add(descriptor);
                requireActive();
                establishedTun = access.get(descriptor);
                requireEstablishedTun();
                return descriptor;
            }
        } finally {
            synchronized (this) { opening--; }
        }
    }

    synchronized void requireEstablishedTun() {
        requireActive();
        if (establishedTun == null || !establishedTun.valid()) throw new IllegalStateException("tun-not-established");
    }

    private void requireActive() {
        if (retiring) throw new IllegalStateException("tunnel-closing");
    }

    void sidecarStarted(Runnable nativeStop) {
        sidecarClose = new FutureTask<>(nativeStop, null);
    }

    synchronized void retire() {
        retiring = true;
    }

    void stopSidecar(long timeoutMs) {
        if (sidecarClose == null) return;
        Thread thread = new Thread(sidecarClose, "vpn-slipstream-stop");
        thread.setDaemon(true);
        thread.start();
        try {
            sidecarClose.get(timeoutMs, TimeUnit.MILLISECONDS);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (java.util.concurrent.ExecutionException | TimeoutException ignored) {
            // The same future remains the barrier, including after an unsuccessful native close.
        }
    }

    synchronized boolean releaseIfClosed(boolean mainIdle) {
        if (!retiring || !mainIdle || opening != 0) return false;
        while (!descriptors.isEmpty()) {
            int last = descriptors.size() - 1;
            try {
                descriptors.get(last).close();
            } catch (IOException e) {
                return false;
            }
            descriptors.remove(last);
        }
        if (sidecarClose == null) return true;
        if (!sidecarClose.isDone()) return false;
        try {
            sidecarClose.get();
            return true;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        } catch (java.util.concurrent.ExecutionException ignored) {
            // JNI did not confirm shutdown. Do not start another listener in this process.
        }
        return false;
    }
}
