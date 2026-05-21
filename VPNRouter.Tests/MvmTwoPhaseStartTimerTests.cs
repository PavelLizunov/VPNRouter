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

        // Use tight budgets so the test runs in milliseconds.
        var phaseA = TimeSpan.FromMilliseconds(500);
        var phaseB = TimeSpan.FromMilliseconds(500);

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB);

        // Yield so the coordinator wires up subscriptions and enters its
        // Task.WhenAny on Phase A before we fire.
        await Task.Delay(50);
        fake.FireStarted(12345);
        await Task.Delay(50);
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
            phaseBBudget: phaseB);
        var elapsed = DateTime.UtcNow - start;

        Assert.Equal(TwoPhaseStartOutcome.PhaseATimeout, outcome);
        // Approximate elapsed near phase A budget (give a fat 2s slop for
        // CI jitter / thread-pool contention).
        Assert.True(elapsed >= phaseA, $"Expected at least {phaseA} elapsed, got {elapsed}");
        Assert.True(elapsed < phaseA + TimeSpan.FromSeconds(2), $"Expected at most {phaseA + TimeSpan.FromSeconds(2)} elapsed, got {elapsed}");
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

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB);

        await Task.Delay(100);
        fake.FireStarted(54321);

        // Wait inside Phase B for ~200ms then fire Connected.
        await Task.Delay(100);
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

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTask,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: phaseA,
            phaseBBudget: phaseB);

        await Task.Delay(50);
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

        var coordinatorTask = TwoPhaseStartCoordinator.RunAsync(
            startTask: startTcs.Task,
            subscribeStarted: fake.SubscribeStarted,
            subscribeConnected: fake.SubscribeConnected,
            phaseABudget: TimeSpan.FromSeconds(5),
            phaseBBudget: TimeSpan.FromSeconds(5));

        await Task.Delay(50);
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
            phaseBBudget: TimeSpan.FromSeconds(5));

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
}
