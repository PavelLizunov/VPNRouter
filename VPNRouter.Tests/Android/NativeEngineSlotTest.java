package com.ninitux.vpnrouter;

import java.io.IOException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.FutureTask;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

public final class NativeEngineSlotTest {
    private static final long DEADLINE = 5_000L;

    private static final class Engine implements NativeEngineSlot.Lifecycle<Object> {
        final Object handle = new Object();
        final CountDownLatch activating = new CountDownLatch(1);
        final CountDownLatch finishStart = new CountDownLatch(1);
        final CountDownLatch closing = new CountDownLatch(1);
        final CountDownLatch finishClose = new CountDownLatch(1);
        boolean failClose;

        public Object create() { return handle; }
        public void activate(Object ignored) throws InterruptedException {
            activating.countDown();
            finishStart.await();
        }
        public void close(Object ignored) throws Exception {
            closing.countDown();
            finishClose.await();
            if (failClose) throw new IOException("synthetic close failure");
        }
    }

    public static void main(String[] args) throws Exception {
        lateStartAndClose();
        cancelBeforeReady();
        closeFailure();
    }

    private static void cancelBeforeReady() throws Exception {
        NativeEngineSlot<Object> slot = new NativeEngineSlot<>("test-cancel");
        Engine first = new Engine();
        NativeEngineSlot.Session<Object> session = slot.begin(first);
        require(first.activating.await(DEADLINE, TimeUnit.MILLISECONDS));
        session.cancel();
        try {
            session.awaitReady(DEADLINE);
            throw new AssertionError("Canceled activation returned a ready engine");
        } catch (IllegalStateException expected) { }
        require(!slot.callbacksPermitted());
        expectBusy(slot);
        first.finishStart.countDown();
        require(first.closing.await(DEADLINE, TimeUnit.MILLISECONDS));
        require(!session.close(2L));
        expectBusy(slot);
        first.finishClose.countDown();
        require(session.close(DEADLINE));

        Engine replacement = new Engine();
        replacement.finishStart.countDown();
        NativeEngineSlot.Session<Object> next = slot.begin(replacement);
        require(next.awaitReady(DEADLINE) == replacement.handle);
        session.cancel();
        require(slot.callbacksPermitted());
        replacement.finishClose.countDown();
        require(next.close(DEADLINE));
    }

    private static void lateStartAndClose() throws Exception {
        NativeEngineSlot<Object> main = new NativeEngineSlot<>("test-main");
        NativeEngineSlot<Object> verify = new NativeEngineSlot<>("test-verify");
        Engine first = new Engine();
        FutureTask<Object> start = new FutureTask<>(() -> main.start(first, 500L));
        new Thread(start).start();
        require(first.activating.await(DEADLINE, TimeUnit.MILLISECONDS));
        try {
            start.get(DEADLINE, TimeUnit.MILLISECONDS);
            throw new AssertionError("Expected startup timeout");
        } catch (ExecutionException e) {
            require(e.getCause() instanceof TimeoutException);
        }
        require(!main.callbacksPermitted());
        expectBusy(main);
        first.finishStart.countDown();
        require(first.closing.await(DEADLINE, TimeUnit.MILLISECONDS));
        require(!main.stop(first.handle, 2L));
        expectBusy(main);

        Engine other = new Engine();
        other.finishStart.countDown();
        require(verify.start(other, DEADLINE) == other.handle);
        require(verify.callbacksPermitted());
        other.finishClose.countDown();
        require(verify.stop(other.handle, DEADLINE));
        require(verify.isIdle());

        first.finishClose.countDown();
        require(main.stop(first.handle, DEADLINE));
        require(main.isIdle());

        Engine replacement = new Engine();
        replacement.finishStart.countDown();
        require(main.start(replacement, DEADLINE) == replacement.handle);
        require(main.stop(first.handle, DEADLINE));
        require(main.callbacksPermitted());
        replacement.finishClose.countDown();
        require(main.stop(replacement.handle, DEADLINE));
    }

    private static void closeFailure() throws Exception {
        NativeEngineSlot<Object> slot = new NativeEngineSlot<>("test-failure");
        Engine engine = new Engine();
        engine.failClose = true;
        engine.finishStart.countDown();
        engine.finishClose.countDown();
        require(slot.start(engine, DEADLINE) == engine.handle);
        require(!slot.stop(engine.handle, DEADLINE));
        expectBusy(slot);
    }

    private static void expectBusy(NativeEngineSlot<Object> slot) throws Exception {
        require(!slot.isIdle());
        try {
            slot.start(new Engine(), 1L);
            throw new AssertionError("Overlapping engine accepted");
        } catch (IllegalStateException expected) { }
    }

    private static void require(boolean condition) {
        if (!condition) throw new AssertionError("Native engine lifecycle regression");
    }
}
