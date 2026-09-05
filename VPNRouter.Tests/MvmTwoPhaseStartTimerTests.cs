// Task #41 Stage 2 (PinkuDani 2026-05-21) — App-side two-phase Start timer
// tests. Verifies TwoPhaseStartCoordinator.RunAsync's outcome surface for
// every (Phase A status × Phase B status × cancellation) combo.
//
// Why: replaces the pre-Stage-2 single 60s CancellationTokenSource in
// MainWindowViewModel's ToggleConnectionAsync / ReconnectAsync /
// ApplyFreeConfigAsync. The coordinator is a static helper that races a
// StartAsync task against two budgets keyed off the SingBoxStarted +
// Connected events from VpnEngine.
//
// The tests intentionally do NOT touch VpnEngine itself — the coordinator
// accepts subscribe/unsubscribe lambdas, so a test can drive synthetic
// "events" via plain delegate invocation. This keeps the test surface
// independent of:
//   * VpnEngine ctor signature (3 nullable abstractions + logger).
//   * Pipeline phase 6 / 8 (real netsh / HKLM mutation).
//   * The deferred IHttpClient seam needed for an end-to-end warmup
//     probe drive (documented in VpnEngineLifecycleTests / Stage 1 brief).
//
// Scope realised:
//   1. PhaseA_SingBoxStartsBefore60s_ProceedsToPhaseB
//   2. PhaseA_NoSingBoxIn60s_PhaseATimeout
//   3. PhaseB_ConnectedFiresBefore20s_ReturnsConnected
//   4. PhaseB_NoConnectedIn20s_PhaseBTimeout
//   5. PreCancelled_ReturnsCancelled
//   6. StartTaskFaultsBeforeEvents_PropagatesAndReturnsStartTaskCompleted
//   7. SubscriptionsUnhookedOnTimeout — defensive pin: finally-block runs
//      even on timeout, so the engine event handlers don't leak into
//      subsequent connects.
//
// Cross-references:
//   * VPNRouter.App/ViewModels/Internals/TwoPhaseStartCoordinator.cs
//   * VpnEngineConnectedEventTests.cs (Stage 1 event-fire contract)
//   * plans/phase4-vm-two-phase-timer-stage2-2026-05-21.md

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using VPNRouter.App.ViewModels.Internals;

namespace VPNRouter.Tests;

public sealed class MvmTwoPhaseStartTimerTests
{
    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight in-test "engine" surface: records subscriptions, exposes
    /// methods to fire events synchronously from the test body. Captures
    /// unsubscribe count for the defensive leak-pin test.
    /// </summary>
    private sealed class FakeEngineEvents
    {
        public int StartedSubscriptions { get; private set; }
        public int StartedUnsubscriptions { get; private set; }
        public int ConnectedSubscriptions { get; private set; }
        public int ConnectedUnsubscriptions { get; private set; }

        private Action<int>? _startedHandler;
        private Action<int>? _connectedHandler;

        public Action SubscribeStarted(Action<int> handler)
        {
            StartedSubscriptions++;
            _startedHandler = handler;
            return () =>
            {
                StartedUnsubscriptions++;
                _startedHandler = null;
            };
        }

        public Action SubscribeConnected(Action<int> handler)
        {
            ConnectedSubscriptions++;
            _connectedHandler = handler;
            return () =>
            {
                ConnectedUnsubscriptions++;
                _connectedHandler = null;
            };
        }

        public void FireStarted(int pid) => _startedHandler?.Invoke(pid);
        public void FireConnected(int pid) => _connectedHandler?.Invoke(pid);
    }

    private static (Task task, TaskCompletionSource<bool> control) ControlledTask()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (tcs.Task, tcs);
    }

    // ─── Test 1: Phase A success → Phase B success → Connected ──────────

    [Fact]
    public async Task PhaseA_SingBoxStartsBefore60s_ProceedsToPhaseB()
    {
        // Drive: fire SingBoxStarted within Phase A budget, then fire
        // Connected within Phase B budget. Coordinator must return
        // Connected.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        // Use tight budgets so the test runs in milliseconds.
        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        // Yield so the coordinator wires up subscriptions and enters its
        // Task.WhenAny on Phase A before we fire.
        await Task.Delay(50, ct);
        fake.FireStarted(12345);
        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        // Subscriptions were attached AND released.
        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 2: Phase A timeout (no SingBoxStarted) ────────────────────

    [Fact]
    public async Task PhaseA_NoSingBoxIn60s_PhaseATimeout()
    {
        // Drive: never fire SingBoxStarted. Coordinator must return
        // PhaseATimeout after the budget elapses.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(200);
        var phaseB = TimeSpan.FromMilliseconds(2000); // shouldn't matter

        var start = DateTime.UtcNow;
        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(TwoPhaseStartOutcome.PhaseATimeout, outcome);
        // Approximate elapsed near phase A budget. Lower bound is fuzzy
        // because Task.Delay is "approximately N" not "at least N exactly"
        // — Linux CI's tight loop saw 199.6ms for a 200ms delay (FX timer
        // granularity ≈ 16ms). Allow a 20ms early grace for CI jitter.
        var lowerBound = phaseA - TimeSpan.FromMilliseconds(20);
        Assert.True(elapsed >= lowerBound,
            $"Expected at least {lowerBound} elapsed (= phaseA {phaseA} - 20ms grace), got {elapsed}");
        Assert.True(elapsed < phaseA + TimeSpan.FromSeconds(2),
            $"Expected at most {phaseA + TimeSpan.FromSeconds(2)} elapsed, got {elapsed}");
    }

    // ─── Test 3: Phase B success ────────────────────────────────────────

    [Fact]
    public async Task PhaseB_ConnectedFiresBefore20s_ReturnsConnected()
    {
        // Drive: fire SingBoxStarted near end of Phase A (still in budget),
        // fire Connected mid Phase B. Coordinator must return Connected.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(400);
        var phaseB = TimeSpan.FromMilliseconds(400);
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(100, ct);
        fake.FireStarted(54321);

        // Wait inside Phase B for ~200ms then fire Connected.
        await Task.Delay(100, ct);
        fake.FireConnected(54321);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);
    }

    // ─── Test 4: Phase B timeout ────────────────────────────────────────

    [Fact]
    public async Task PhaseB_NoConnectedIn20s_PhaseBTimeout()
    {
        // Drive: fire SingBoxStarted promptly, never fire Connected.
        // Coordinator must return PhaseBTimeout after the Phase B budget.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(300);
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(99999);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);
    }

    // ─── Test 5: Pre-cancelled token ────────────────────────────────────

    [Fact]
    public async Task PreCancelled_ReturnsCancelled()
    {
        // Drive: cancel the outer CT before any event fires AND before any
        // budget expires. Coordinator must return Cancelled.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);

        Assert.Equal(TwoPhaseStartOutcome.Cancelled, outcome);

        // Subscriptions cleaned up regardless.
        Assert.Equal(fake.StartedSubscriptions, fake.StartedUnsubscriptions);
        Assert.Equal(fake.ConnectedSubscriptions, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 6: Start task faults before events fire ───────────────────

    [Fact]
    public async Task StartTaskFaultsBeforeEvents_ReturnsStartTaskCompleted()
    {
        // Drive: the StartAsync task throws (e.g. ConflictingVpnException)
        // BEFORE either event fires. Coordinator returns
        // StartTaskCompleted; caller is responsible for awaiting the
        // task to surface the exception.
        var fake = new FakeEngineEvents();
        var startTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var ct = TestContext.Current.CancellationToken;
        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTcs.Task,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        startTcs.TrySetException(new InvalidOperationException("conflicting VPN"));

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);

        // Pin caller responsibility — awaiting startTcs.Task surfaces the
        // exception. This mirrors the production pattern in
        // ToggleConnectionAsync's StartTaskCompleted branch.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await startTcs.Task);
        Assert.Equal("conflicting VPN", ex.Message);
    }

    // ─── Test 7: Subscriptions are always unhooked ──────────────────────

    [Fact]
    public async Task SubscriptionsUnhookedOnTimeout()
    {
        // Defence pin: regardless of outcome, the unsubscribe lambdas
        // returned from the subscribe callbacks must be invoked. Otherwise
        // a stale handler would accumulate on the engine event surface
        // across multiple Connect attempts → multi-fire + leak.
        var fake = new FakeEngineEvents();
        var (startTask, _) = ControlledTask();

        // Tight Phase A budget so the test completes quickly.
        await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromMilliseconds(100),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 8: Default budgets sanity ─────────────────────────────────

    [Fact]
    public void DefaultBudgets_Are60sPhaseA_And20sPhaseB()
    {
        // Pin the production defaults so a future refactor that bumps them
        // (or accidentally swaps them) fires this test. The values match
        // the brief's spec and ToggleConnectionAsync's call-site comment.
        Assert.Equal(60, (int)TwoPhaseStartCoordinator.DefaultPhaseABudget.TotalSeconds);
        Assert.Equal(20, (int)TwoPhaseStartCoordinator.DefaultPhaseBBudget.TotalSeconds);
    }

    // ─── Test 9: Started then clean start completion, later Connected succeeds ───

    [Fact]
    public async Task Started_ThenCleanStartCompletion_LaterConnectedSucceeds()
    {
        // NIGHT-07: Phase B must not abort upon clean startTask completion.
        // If startTask completes cleanly after SingBoxStarted, coordinator continues
        // waiting for Connected vs SAME phaseBDelay; when Connected fires, returns Connected.
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        // startTask completes cleanly after Started fired
        await Task.Delay(50, ct);
        startTcs.TrySetResult(true);

        // Connected fires later, still within Phase B budget
        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 10: Clean start completion in Phase A, then Started, then Connected ───

    [Fact]
    public async Task CleanStartCompletion_ThenStarted_LaterConnectedSucceeds()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        // startTask completes cleanly before Started fires
        await Task.Delay(50, ct);
        startTcs.TrySetResult(true);

        // Started fires within Phase A budget
        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        // Connected fires within Phase B budget
        await Task.Delay(50, ct);
        fake.FireConnected(12345);

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 11: Clean completed, no Started times out not success ──────

    [Fact]
    public async Task CleanCompleted_NoStarted_TimesOutPhaseA_NotSuccess()
    {
        // NIGHT-07: startTask clean completion before Started must NOT falsely green
        // or return StartTaskCompleted. It must wait until Phase A budget expires
        // and return PhaseATimeout.
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();

        // Complete cleanly immediately
        startTcs.TrySetResult(true);

        var phaseA = TimeSpan.FromMilliseconds(200);
        var phaseB = TimeSpan.FromMilliseconds(2000);

        var start = DateTime.UtcNow;
        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: TestContext.Current.CancellationToken);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(TwoPhaseStartOutcome.PhaseATimeout, outcome);
        var lowerBound = phaseA - TimeSpan.FromMilliseconds(20);
        Assert.True(elapsed >= lowerBound,
            $"Expected at least {lowerBound} elapsed, got {elapsed}");

        // Subscriptions must still be unhooked
        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 12: No Connected Phase B timeout with original deadline not reset ───

    [Fact]
    public async Task NoConnected_PhaseBTimeout_OriginalDeadlineNotReset()
    {
        // NIGHT-07: in Phase B, if startTask completes cleanly after some delay,
        // the coordinator continues waiting on the SAME phaseBDelay without resetting the timer.
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(300);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB,
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        // Phase B started. Record time.
        var phaseBStart = DateTime.UtcNow;

        // After 100ms into Phase B, startTask completes cleanly.
        await Task.Delay(100, ct);
        startTcs.TrySetResult(true);

        // Connected NEVER fires.
        var outcome = await coordinatorTask;
        var phaseBElapsed = DateTime.UtcNow - phaseBStart;

        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);

        // Deadline was NOT reset to a new 300ms delay upon startTask completion.
        // It must have elapsed ~300ms from phaseBStart.
        var lowerBound = phaseB - TimeSpan.FromMilliseconds(30);
        Assert.True(phaseBElapsed >= lowerBound,
            $"Expected Phase B elapsed at least {lowerBound}, got {phaseBElapsed}");
        Assert.True(phaseBElapsed < phaseB + TimeSpan.FromMilliseconds(180),
            $"Timer must not reset upon clean completion; expected < 480ms, got {phaseBElapsed}");

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 13: Deterministic regression: Phase B zero budget returns PhaseBTimeout ───

    [Fact]
    public async Task NIGHT07_DeterministicRegression_PhaseBZeroBudget_ReturnsPhaseBTimeout()
    {
        // NIGHT-07: startTask=CompletedTask, subscribeStarted synchronously invokes handler,
        // Connected does not fire, PhaseB=0. Must return PhaseBTimeout, not StartTaskCompleted.
        var startedSubscriptions = 0;
        var startedUnsubscriptions = 0;
        var connectedSubscriptions = 0;
        var connectedUnsubscriptions = 0;

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: Task.CompletedTask,
            subscribeStarted: handler =>
            {
                startedSubscriptions++;
                handler(12345); // synchronously fires Started on subscribe
                return () => startedUnsubscriptions++;
            },
            subscribeConnected: _ =>
            {
                connectedSubscriptions++;
                return () => connectedUnsubscriptions++;
            },
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TwoPhaseStartOutcome.PhaseBTimeout, outcome);
        Assert.Equal(1, startedSubscriptions);
        Assert.Equal(1, startedUnsubscriptions);
        Assert.Equal(1, connectedSubscriptions);
        Assert.Equal(1, connectedUnsubscriptions);
    }

    // ─── Test 14: Fault/cancel prompt in Phase B ─────────────────────────

    [Fact]
    public async Task FaultCancelPrompt_PhaseB_ReturnsStartTaskCompletedPromptly()
    {
        // NIGHT-07: Faulted startTask returns existing StartTaskCompleted promptly for caller await.
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(10),
            phaseBBudget: TimeSpan.FromSeconds(10),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        // In Phase B, startTask faults.
        await Task.Delay(50, ct);
        var faultStart = DateTime.UtcNow;
        startTcs.TrySetException(new InvalidOperationException("warmup failed"));

        var outcome = await coordinatorTask;
        var faultElapsed = DateTime.UtcNow - faultStart;

        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        // Prompt return: must not wait for the 10s Phase B budget!
        Assert.True(faultElapsed < TimeSpan.FromSeconds(2),
            $"Expected prompt return after fault, took {faultElapsed}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await startTask);
        Assert.Equal("warmup failed", ex.Message);
    }

    // ─── Test 15: Internal cancel prompt returns StartTaskCompleted ──────

    [Fact]
    public async Task InternalCancellation_Prompt_ReturnsStartTaskCompleted()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(10),
            phaseBBudget: TimeSpan.FromSeconds(10),
            cancellationToken: ct);

        await Task.Delay(50, ct);
        fake.FireStarted(12345);

        await Task.Delay(50, ct);
        var cancelStart = DateTime.UtcNow;
        startTcs.TrySetCanceled();

        var outcome = await coordinatorTask;
        var cancelElapsed = DateTime.UtcNow - cancelStart;

        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        Assert.True(cancelElapsed < TimeSpan.FromSeconds(2),
            $"Expected prompt return after internal cancel, took {cancelElapsed}");
    }

    // ─── Test 16: External cancellation in Phase B returns Cancelled ─────

    [Fact]
    public async Task ExternalCancellation_PhaseB_ReturnsCancelled()
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();

        using var cts = new CancellationTokenSource();

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5),
            cancellationToken: cts.Token);

        await Task.Delay(50);
        fake.FireStarted(12345);

        // startTask completed cleanly
        await Task.Delay(50);
        startTcs.TrySetResult(true);

        // Now during Phase B wait, cancel outer CTS
        await Task.Delay(50);
        cts.Cancel();

        var outcome = await coordinatorTask;
        Assert.Equal(TwoPhaseStartOutcome.Cancelled, outcome);

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }

    // ─── Test 17: Race Started already fired prioritizes event unless fault ───

    [Fact]
    public async Task Race_StartedAlreadyFired_PrioritizesEvent_WhenClean()
    {
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        // Clean completion + Started fired synchronously
        startTcs.TrySetResult(true);

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: handler =>
            {
                handler(12345);
                return () => { };
            },
            subscribeConnected: handler =>
            {
                handler(12345);
                return () => { };
            },
            phaseABudget: TimeSpan.FromSeconds(1),
            phaseBBudget: TimeSpan.FromSeconds(1),
            cancellationToken: ct);

        // Clean completion + Started fired -> event prioritized -> Connected
        Assert.Equal(TwoPhaseStartOutcome.Connected, outcome);
    }

    [Fact]
    public async Task Race_StartedAlreadyFired_PrioritizesFault_WhenTaskFaulted()
    {
        var (startTask, startTcs) = ControlledTask();
        var ct = TestContext.Current.CancellationToken;

        // Task faults before or during Started race
        startTcs.TrySetException(new InvalidOperationException("race fault"));

        var outcome = await TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: handler =>
            {
                handler(12345);
                return () => { };
            },
            subscribeConnected: _ => () => { },
            phaseABudget: TimeSpan.FromSeconds(1),
            phaseBBudget: TimeSpan.FromSeconds(1),
            cancellationToken: ct);

        // Task fault must be prioritized over Started event!
        Assert.Equal(TwoPhaseStartOutcome.StartTaskCompleted, outcome);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await startTask);
        Assert.Equal("race fault", ex.Message);
    }

    // ─── Test 18: Subscriptions removed across all outcomes ──────────────

    [Theory]
    [InlineData("Connected")]
    [InlineData("PhaseATimeout")]
    [InlineData("PhaseBTimeout")]
    [InlineData("StartTaskFault")]
    [InlineData("Cancelled")]
    public async Task SubscriptionsRemoved_AllOutcomes(string scenario)
    {
        var fake = new FakeEngineEvents();
        var (startTask, startTcs) = ControlledTask();
        using var cts = new CancellationTokenSource();

        Task<TwoPhaseStartOutcome> coordinatorTask;

        switch (scenario)
        {
            case "Connected":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), cts.Token);
                fake.FireStarted(123);
                fake.FireConnected(123);
                await coordinatorTask;
                break;

            case "PhaseATimeout":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), cts.Token);
                await coordinatorTask;
                break;

            case "PhaseBTimeout":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), cts.Token);
                fake.FireStarted(123);
                await coordinatorTask;
                break;

            case "StartTaskFault":
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token);
                startTcs.TrySetException(new InvalidOperationException("fault"));
                await coordinatorTask;
                break;

            case "Cancelled":
                cts.Cancel();
                coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
                    startTask, fake.SubscribeStarted, fake.SubscribeConnected,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), cts.Token);
                await coordinatorTask;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.Equal(1, fake.StartedSubscriptions);
        Assert.Equal(1, fake.StartedUnsubscriptions);
        Assert.Equal(1, fake.ConnectedSubscriptions);
        Assert.Equal(1, fake.ConnectedUnsubscriptions);
    }
}
